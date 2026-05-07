namespace Cmux.Core.Config;

/// <summary>
/// Application-wide settings with sensible defaults.
/// Serialized to/from settings.json via <see cref="SettingsService"/>.
/// </summary>
public class CmuxSettings
{
    // ── Appearance ──────────────────────────────────────────────

    public string FontFamily { get; set; } = "Cascadia Code";
    public int FontSize { get; set; } = 14;
    public string ThemeName { get; set; } = "Default Dark";

    // App-level WPF chrome theme (separate from the terminal renderer's ThemeName).
    // AppThemeMode is "Light" or "Dark" — drives ModernWpf's RequestedTheme + which
    // brand dictionary loads. AppThemeVariant is the named palette within that mode.
    // Changes require an app restart (no live DynamicResource reflow yet).
    public string AppThemeMode { get; set; } = "Dark";
    public string AppThemeVariant { get; set; } = "Default Dark";

    public bool UseCustomTerminalColors { get; set; } = false;
    public string CustomTerminalBackground { get; set; } = "";
    public string CustomTerminalForeground { get; set; } = "";
    public string CustomTerminalCursor { get; set; } = "";
    public string CustomTerminalSelection { get; set; } = "";
    public double Opacity { get; set; } = 1.0;
    public string CursorStyle { get; set; } = "bar"; // bar | block | underline
    public bool CursorBlink { get; set; } = true;
    public int CursorBlinkMs { get; set; } = 530;
    public double LineHeight { get; set; } = 1.0;
    public int Padding { get; set; } = 0;

    // ── Terminal ────────────────────────────────────────────────

    public string DefaultShell { get; set; } = "";
    public string DefaultShellArgs { get; set; } = "";
    public int ScrollbackLines { get; set; } = 10_000;
    public bool BellSound { get; set; } = false;
    public bool VisualBell { get; set; } = true;
    public bool BracketedPaste { get; set; } = true;
    public string WordSeparators { get; set; } = " \t\n{}[]()\"'`,:;<>";

    // ── Behavior ────────────────────────────────────────────────

    public bool RestoreSessionOnStartup { get; set; } = true;
    public bool ConfirmOnClose { get; set; } = true;
    public bool AutoCopyOnSelect { get; set; } = false;
    public bool RightClickPaste { get; set; } = false;
    public bool CtrlClickOpensUrls { get; set; } = true;

    // ── Notifications ───────────────────────────────────────────

    /// <summary>Whether OSC 9 / pipe-NOTIFY events fire Windows toast notifications.</summary>
    public bool EnableToastNotifications { get; set; } = true;

    /// <summary>
    /// When true, toasts surface even when the cmux window itself is focused.
    /// Default false matches classic behavior (focused = no toast spam).
    /// </summary>
    public bool ShowToastsWhileFocused { get; set; } = false;

    // ── Diagnostics ─────────────────────────────────────────────

    /// <summary>Master toggle for the in-app diagnostic logger.</summary>
    public bool EnableDiagnosticLogging { get; set; } = true;

    /// <summary>
    /// Minimum log level to record: "debug" | "info" | "warn" | "error".
    /// Anything below is filtered before hitting the ring buffer or file.
    /// </summary>
    public string DiagnosticLogLevel { get; set; } = "info";
    public int AutoSaveIntervalSeconds { get; set; } = 30;
    public bool CaptureTranscriptsOnClose { get; set; } = true;
    public bool CaptureTranscriptsOnClear { get; set; } = true;
    // 0 = keep logs forever (no cleanup)
    public int CommandLogRetentionDays { get; set; } = 90;
    // 0 = keep captures forever (no cleanup)
    public int TranscriptRetentionDays { get; set; } = 90;

    // ── Collections ─────────────────────────────────────────────

    public List<ShellProfile> ShellProfiles { get; set; } = [];
    public Dictionary<string, string> KeyBindings { get; set; } = [];
    public List<string> RecentDirectories { get; set; } = [];
}

/// <summary>
/// A named shell profile used to launch terminal sessions.
/// </summary>
public class ShellProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Default";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = [];
    public string? ThemeOverride { get; set; }
    public bool IsDefault { get; set; }
}
