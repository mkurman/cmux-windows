# AGENTS.md

## What matters here

- This repo is a work-in-progress fork. Persistence, daemon discovery, OSC handling, and transcript/session edges are known fragile areas in `README.md`; do not assume a feature works end-to-end just because the code exists.
- Targets Windows only and .NET 10. `Directory.Build.props` turns on nullable, implicit usings, `LangVersion=14`, and `TreatWarningsAsErrors=true`, so warnings fail the build.

## Layout

- `src/Cmux/` is the WPF app and builds `cmuxw.exe`.
- `src/Cmux.Core/` holds terminal engine, IPC, models, services, and persistence.
- `src/Cmux.Cli/` builds `cmux.exe` and talks to the running app over `\\.\pipe\cmux`.
- `src/Cmux.Daemon/` builds `cmux-daemon.exe` and hosts ConPTY over `\\.\pipe\cmux-daemon`.
- `tests/Cmux.Tests/` references `Cmux.Core` only.
- `Cmux.sln` includes exactly those five projects.

## Commands

```powershell
dotnet build Cmux.sln -c Debug
dotnet run --project src/Cmux/Cmux.csproj -c Debug
dotnet test
dotnet test --filter "FullyQualifiedName~VtParserTests.Feed_PrintableCharacters_RaisesOnPrint"
```

Publish the three executables together; CI copies `cmux.exe` and `cmux-daemon.exe` next to `cmuxw.exe`:

```powershell
dotnet publish src/Cmux/Cmux.csproj -c Release -r win-x64 --self-contained true -o publish/app
dotnet publish src/Cmux.Cli/Cmux.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/cli
dotnet publish src/Cmux.Daemon/Cmux.Daemon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/daemon
```

## Repo-specific traps

- The app and daemon use different named pipes for different jobs. `\\.\pipe\cmux` drives the UI; `\\.\pipe\cmux-daemon` hosts terminals out of process.
- Both pipes use UTF-8 without BOM. Do not change that encoding.
- `DaemonClient` opens the daemon pipe asynchronously; switching to synchronous pipe options can deadlock concurrent I/O.
- `App.xaml.cs` starts with a single-instance mutex, applies the theme before `base.OnStartup`, then starts the named-pipe server and daemon-connect task. If you touch startup, preserve that order.
- `App.xaml.cs` is the source of truth for app theme loading and toast wiring. It loads `Themes/DarkTheme.xaml` or `Themes/LightTheme.xaml` with a pack URI, so runtime resource paths must stay pack-URI based.
- The app uses ModernWpf plus brand dictionaries layered on top. Avoid reintroducing custom generic `MenuItem`/`CheckBox`/`TextBox`/`ComboBox` styles that ModernWpf already owns.
- `cmuxw.exe` is the GUI app. `cmux.exe` is the CLI. `cmux-daemon.exe` is auto-started by the app and should stay next to the other two binaries in published output.
- Versions live in each `.csproj`; do not centralize versioning into a shared file.

## When changing code

- If you edit terminal or IPC code, check both local and daemon code paths.
- If you edit settings or theme code, remember that app theme changes are restart-required and that terminal theme/font seeding happens separately from the WPF chrome theme.
- If you edit tests, keep them focused on `Cmux.Core`; the current test suite is thin and does not cover the full WPF or IPC round-trip.
