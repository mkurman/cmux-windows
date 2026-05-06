# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build / run / test

Targets **.NET 10 SDK**, Windows 10/11. `Directory.Build.props` enables nullable, implicit usings, `LangVersion=14`, and `TreatWarningsAsErrors=true` — warnings break the build.

```powershell
dotnet build Cmux.sln -c Debug
dotnet run --project src/Cmux/Cmux.csproj -c Debug   # launch the WPF app
dotnet test                                          # run all xUnit tests
dotnet test --filter "FullyQualifiedName~VtParserTests.Feed_PrintableCharacters_RaisesOnPrint"
```

Publish artifacts (CI mirrors these in `.github/workflows/ci.yml`):

```powershell
dotnet publish src/Cmux/Cmux.csproj        -c Release -r win-x64 --self-contained true -o publish/app
dotnet publish src/Cmux.Cli/Cmux.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/cli
dotnet publish src/Cmux.Daemon/Cmux.Daemon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/daemon
```

CI bundles `cmux.exe` (CLI) and `cmux-daemon.exe` next to `cmuxw.exe` (app) in the published artifact — they're expected to coexist in the install dir.

## Solution layout

Five projects in `Cmux.sln`:

| Project | Output | Role |
|---|---|---|
| `src/Cmux/` | `cmuxw.exe` (WinExe, WPF) | Desktop shell — Views, ViewModels, terminal control. References `Cmux.Core`. |
| `src/Cmux.Core/` | library (`AllowUnsafeBlocks`) | Terminal engine, IPC, models, services, persistence. No WPF deps. |
| `src/Cmux.Cli/` | `cmux.exe` console | Talks to running app over named pipe `\\.\pipe\cmux`. |
| `src/Cmux.Daemon/` | `cmux-daemon.exe` console | Out-of-process ConPTY host. Pipe `\\.\pipe\cmux-daemon`. |
| `tests/Cmux.Tests/` | xUnit + FluentAssertions | References `Cmux.Core` only. |

WPF code never references the daemon or CLI directly — they communicate over named pipes only.

## Architecture — what requires reading multiple files to grok

### Two-tier IPC

There are **two distinct named-pipe surfaces**, easily confused:

1. **`\\.\pipe\cmux`** — `Cmux.Core/IPC/NamedPipeServer.cs` is hosted by the WPF app (`App.xaml.cs` starts it on launch). The CLI (`Cmux.Cli/Program.cs` → `NamedPipeClient`) sends commands like `NOTIFY`, `WORKSPACE`, `SPLIT`, `STATUS` here. This is for **driving the UI**.
2. **`\\.\pipe\cmux-daemon`** — `Cmux.Core/IPC/DaemonClient.cs` ↔ `Cmux.Daemon/DaemonPipeServer.cs`. The WPF app is the *client*; the daemon is the *server*. This is for **hosting ConPTY sessions out-of-process** so the UI can crash/restart without killing terminals. Daemon idle-exits after 24h of zero clients + zero sessions.

Both pipes use **UTF-8 without BOM** (`PipeEncoding.Utf8NoBom`) — `Encoding.UTF8`'s BOM preamble + `FlushFileBuffers` semantics will deadlock both ends. Don't change this.

`DaemonClient` opens the pipe with `PipeOptions.Asynchronous` (overlapped I/O). Don't switch to `PipeOptions.None` — Windows serializes I/O on non-overlapped handles, and the listen thread's blocking read will deadlock concurrent writes.

### Local vs daemon terminal mode

`TerminalSession` (`Cmux.Core/Terminal/TerminalSession.cs`) supports two execution modes selected at session-create time:

- **Local mode**: owns its own `PseudoConsole` + `TerminalProcess`, reads VT bytes on a dedicated thread.
- **Daemon mode**: `DaemonWrite` / `DaemonResize` delegates are non-null; the session shells writes/resizes through `DaemonClient` and receives output via `RawOutputReceived` events.

`App.xaml.cs` starts a `DaemonConnectTask` on launch (300 ms quick check, then attempts to spawn the daemon). New sessions wait on this task before deciding their mode. If you add new terminal operations, handle **both** code paths.

### Single-instance guards

- WPF app: `Global\CmuxWindowsSingleInstance` mutex in `App.OnStartup` — second launch shows a message box and exits.
- Daemon: `Global\CmuxDaemon` mutex in `Cmux.Daemon/Program.cs` — duplicate daemons silently exit with code 1.

### Terminal pipeline

Bytes flow: `ConPtyInterop` (P/Invoke) → `PseudoConsole` → read thread → `VtParser` (state machine) → `OscHandler` + `TerminalBuffer` (grid, scrollback, attributes) → `TerminalSession` events → WPF terminal control. `OscHandler` ingests OSC 9/99/777 sequences and emits `NotificationReceived` — that's the agent-notification feature surface.

### Persistence

`SessionPersistenceService` (Core/Services) snapshots workspaces / surfaces / panes (`PaneStateSnapshot`, `SessionState`, etc. in Core/Models). `CommandLogService` and `SnippetService` own their own JSON stores. `SecretStoreService` uses DPAPI (`System.Security.Cryptography.ProtectedData`) for credential entries.

### MVVM split

`MainViewModel` (~34 KB) owns workspace/surface tree + global UI state. `SurfaceViewModel` (~23 KB) owns one tab including the `SplitNode` pane tree (`Cmux.Core/Models/SplitNode.cs` is the recursive split model). `WorkspaceViewModel` is a lightweight per-workspace metadata holder. Uses `CommunityToolkit.Mvvm` source generators — don't hand-roll `INotifyPropertyChanged`.

### Theme integration

`Cmux.Core/Config/GhosttyConfigReader.cs` reads Ghostty config files; `TerminalThemes.cs` ships built-in palettes. `SettingsService` is the persistence layer. The settings window (`Cmux/Views/SettingsWindow.xaml`, ~58 KB) is the single source for live theme application.

## Tooling outside the solution

- `tools/cmux_mcp_server.py` — Python MCP server that wraps the `cmux` CLI; lets MCP-capable AIs (Claude Desktop, Cursor, Zed) drive cmux. Requires `pip install mcp`. Looks for `cmux.exe` via env override → dev build → PATH.
- `tools/sandbox-*.ps1` and `tools/sandbox.wsb` — Windows Sandbox helpers for running cmux in an isolated VM and relaying commands back to the host.
- `claude-desktop-config-example.json` — example Claude Desktop MCP config pointing at the Python server.

## UI / styling — ModernWpf is the kit, `DarkTheme.xaml` is the brand layer

The app uses **[ModernWpfUI](https://github.com/Kinnara/ModernWpf)** (`Microsoft.Toolkit.Uwp.Notifications` ≠ this) for Fluent/WinUI-style controls, layered with custom brand styling in `src/Cmux/Themes/DarkTheme.xaml`. The merge order in `App.xaml` is load-bearing:

```xml
<ui:ThemeResources RequestedTheme="Dark" />
<ui:XamlControlsResources />
<ResourceDictionary Source="Themes/DarkTheme.xaml" />   <!-- our overrides win -->
```

ModernWpf supplies the *implicit* (`TargetType`-only) defaults for `Button`/`ComboBox`/`CheckBox`/`TextBox`/`ScrollBar`/`MenuItem`/etc. — every plain `<ComboBox/>` automatically picks up the Fluent look. Our `Cmux*` *keyed* styles in `DarkTheme.xaml` (e.g. `Style="{StaticResource CmuxButton}"`) are explicit opt-ins and override the defaults — they're for places where the brand needs to deviate (gradient `AccentButton`, ghost `CmuxButton`, custom `CmuxColorSwatch`, etc.).

### Rules for new UI

1. **Use ModernWpf controls first.** Prefer `<ui:ToggleSwitch>`, `<ui:NumberBox>`, `<ui:AutoSuggestBox>`, `<ui:ColorPicker>`, `<ui:CommandBar>`, `<ui:Flyout>` over rolling your own. They're already themed, accessible, and animated.
2. **For booleans, use `ui:ToggleSwitch`, not `CheckBox`.** Existing `CheckBox`es bound through the `CmuxCheckBox` style render as a toggle, but new code should reach for the real component (it has `IsOn`, header/off-on content slots, and proper accessibility).
3. **Don't override `SystemColors.*` brushes per-window.** `ThemeResources` already routes those — manual overrides (as `SettingsWindow.xaml` and `ColorPickerWindow.xaml` historically did) fight the kit. Remove them when you touch those files.
4. **Brand accent is `#FF818CF8`** — defined as `SystemAccentColor` in `App.xaml`. ModernWpf reads from there, so accent-colored UI everywhere is automatically on-brand. Don't hardcode the indigo elsewhere; use `{DynamicResource SystemAccentColor}` or `{StaticResource AccentBrush}`.
5. **Keep `DarkTheme.xaml` for design tokens and brand-specific keyed styles only.** New tokens belong there (`ControlHeight`, `ControlCornerRadius`, gradient brushes, drop-shadow effects). New generic control templates do **not** — that's ModernWpf's job.
6. **Custom-rendered controls are exempt.** `TerminalControl` is a `FrameworkElement` doing direct visual rendering; it ignores theming entirely and reads its colors from `SettingsService` / `TerminalThemes`. Don't try to ModernWpf-ify it.

### Adoption roadmap (incremental, no big-bang)

Status: **Phase 1 done** as of 2026-05-05 — package added, resource dictionaries wired, accent overridden. Existing styles untouched, app still builds/tests clean.

| Phase | Scope | Effort | Risk |
|---|---|---|---|
| 1 | Add package + merge `ThemeResources`/`XamlControlsResources` + accent override. ✅ | done | none |
| 2 | Swap settings `CheckBox`es → `<ui:ToggleSwitch>`, integer `TextBox`es → `<ui:NumberBox>`. ✅ | done | none |
| 3 | Delete redundant generic styles from `DarkTheme.xaml`: `ContextMenu`, `MenuItem`, `Separator`, `DarkScrollBarThumb`, `CmuxTextBox`, `CmuxPasswordBox`, `CmuxComboBox`, `CmuxComboBoxItem`, `CmuxCheckBox`. ModernWpf handles them. Keep brand-specific keyed styles (`CmuxButton`, `AccentButton`, `IconButton`, `WindowChromeButton`, `WindowCloseButton`, `CmuxColorSwatch`, `CmuxFormLabel`, `CmuxSectionHeader`, gradient/glow brushes). | ~1h | medium — touch every consumer; verify each window |
| 4 | Replace custom `ColorPickerWindow` with `<ui:ColorPicker>` inside a small host window. Delete the hand-rolled RGB sliders. | ~1h | low |
| 5 | Optional: migrate per-window custom title bars to `<ui:WindowEx>` + `ui:TitleBar.IsBackButtonVisible` etc. for native snap-layouts. Skip unless we want Windows 11 snap fly-outs. | ~2h | medium — `WindowStyle="None" AllowsTransparency="True"` interplay needs care |

When working on settings/dialogs, prefer doing phase 2 *for the file you're already touching* rather than as a separate cleanup pass.

### Trip-wires

- Don't downgrade ModernWpfUI past 0.9.6 — earlier versions don't support `RequestedTheme` resource overrides cleanly.
- ModernWpf's acrylic/mica effects don't compose with `WindowStyle="None" AllowsTransparency="True"` — the cmux windows all use that for custom chrome, so leave acrylic off.
- If a `<ui:ToggleSwitch>` looks misaligned in a Grid form row, set `MinHeight="{StaticResource ControlHeight}"` and `VerticalAlignment="Center"` — its default height is taller than our 32px input row.

## Conventions

- All cross-process JSON over pipes is line-delimited (`StreamReader.ReadLine` / `WriteLine`). Don't pretty-print.
- Logs to the daemon log go through `DaemonClient.LogDaemon` from both sides — don't introduce a second log path.
- Versions are set per-project in `.csproj` (e.g., `<Version>1.0.6</Version>` in `Cmux.csproj`). Bump there, not in a shared file.
- The CLI binary publishes as `cmux.exe`; the WPF app publishes as `cmuxw.exe` (`AssemblyName` in `Cmux.csproj`). `cmux` vs `cmuxw` distinction matters — don't rename.
