# cmux for Windows

A Windows terminal multiplexer with vertical tabs and notifications for AI coding agents. Windows port of [cmux](https://github.com/manaflow-ai/cmux).

## Features

- **ConPTY terminal emulation** -- Real Windows pseudo-console with full ANSI/VT100 support, not a fake terminal
- **Vertical sidebar** -- Workspaces showing git branch, working directory, listening ports, and latest notification text
- **Horizontal tabs** -- Multiple surfaces (tabs) within each workspace
- **Split panes** -- Binary split tree with draggable dividers (vertical and horizontal)
- **Notification system** -- Detects OSC 9/99/777 terminal notifications from AI coding agents (Claude Code, Codex, etc.)
- **Blue notification rings** -- Panes glow blue when agents need your attention
- **Notification panel** -- See all pending notifications, jump to latest unread
- **In-app browser** -- WebView2-based browser with scriptable API (accessibility tree, click, fill, evaluate JS)
- **Named pipe API** -- CLI tool and IPC for automation (`cmux notify`, `cmux workspace`, `cmux split`)
- **Ghostty compatible** -- Reads your existing `~/.config/ghostty/config` for themes, fonts, and colors
- **Session persistence** -- Saves and restores window layout, workspaces, surfaces, and split pane layout
- **Dark theme** -- Native dark UI matching the terminal aesthetic

## Install

### From Source

Requires [.NET 8 SDK](https://dot.net/download) and Windows 10/11.

```powershell
git clone <repo-url> cmux-windows
cd cmux-windows
dotnet build
dotnet run --project src/Cmux
```

### CLI Tool

```powershell
dotnet publish src/Cmux.Cli/Cmux.Cli.csproj -c Release -r win-x64 --self-contained -o publish/cli
# Add publish/cli to your PATH
```

## Keyboard Shortcuts

### Workspaces

| Shortcut | Action |
|----------|--------|
| Ctrl+N | New workspace |
| Ctrl+1-8 | Jump to workspace 1-8 |
| Ctrl+9 | Jump to last workspace |
| Ctrl+Shift+W | Close workspace |
| Ctrl+Shift+R | Rename workspace |
| Ctrl+B | Toggle sidebar |

### Surfaces (Tabs)

| Shortcut | Action |
|----------|--------|
| Ctrl+T | New surface |
| Ctrl+Shift+] | Next surface |
| Ctrl+Shift+[ | Previous surface |
| Ctrl+Tab | Next surface |
| Ctrl+Shift+Tab | Previous surface |
| Ctrl+W | Close surface |

### Split Panes

| Shortcut | Action |
|----------|--------|
| Ctrl+D | Split right |
| Ctrl+Shift+D | Split down |
| Ctrl+Alt+Arrow | Focus pane directionally |

### Notifications

| Shortcut | Action |
|----------|--------|
| Ctrl+I | Toggle notification panel |
| Ctrl+Shift+U | Jump to latest unread |

### Terminal

| Shortcut | Action |
|----------|--------|
| Ctrl+C | Copy (with selection) / Send interrupt |
| Right-click | Paste |
| Ctrl+V | Paste |

## CLI Usage

```powershell
# Send a notification (wire into agent hooks)
cmux notify --title "Claude Code" --body "Waiting for input"

# Manage workspaces
cmux workspace list
cmux workspace create --name "My Project"
cmux workspace select --index 0

# Create surfaces and splits
cmux surface create
cmux split right
cmux split down

# Check status
cmux status
```

## Architecture

```
cmux-windows/
  src/
    Cmux/                    # WPF application
      Views/                 # Windows (MainWindow, SettingsWindow)
      ViewModels/            # MVVM view models
      Controls/              # Custom WPF controls (TerminalControl, SplitPaneContainer, etc.)
      Converters/            # XAML value converters
      Themes/                # Resource dictionaries (DarkTheme)
    Cmux.Core/               # Shared library
      Terminal/              # ConPTY, VT parser, buffer, selection
      Models/                # Domain models (Workspace, Surface, SplitNode, etc.)
      Config/                # Ghostty config reader
      IPC/                   # Named pipe server/client
      Services/              # Git, port scanner, notifications, persistence
    Cmux.Cli/                # CLI tool
  tests/
    Cmux.Tests/              # Unit tests (xUnit)
```

### Key Components

- **ConPTY** (`PseudoConsole.cs`, `TerminalProcess.cs`): Windows pseudo-console for real terminal emulation
- **VT Parser** (`VtParser.cs`): State machine parser for ANSI/VT100 escape sequences
- **Terminal Buffer** (`TerminalBuffer.cs`): Cell grid with scrollback, cursor, scroll regions
- **Terminal Session** (`TerminalSession.cs`): Ties ConPTY + parser + buffer + OSC handling together
- **Split Node** (`SplitNode.cs`): Binary tree for split pane layout with split/remove/navigate operations
- **Named Pipe Server** (`NamedPipeServer.cs`): `\\.\pipe\cmux` for CLI communication

## Ghostty Config

cmux reads your Ghostty configuration for themes, fonts, and colors:

```
# %USERPROFILE%\.config\ghostty\config
background = #1e1e1e
foreground = #cccccc
font-family = Cascadia Mono
font-size = 13
palette = 0=#1e1e1e
palette = 1=#f44747
# ... etc
```

## Agent Hook Integration

Wire cmux notifications into your AI agent hooks:

### Claude Code

```json
{
  "hooks": {
    "notification": [
      {
        "command": "cmux notify --title \"Claude Code\" --body \"$BODY\""
      }
    ]
  }
}
```

## License

AGPL-3.0-or-later (matching the original cmux project)
