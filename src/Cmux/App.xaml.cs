using System.IO;
using System.Threading;
using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;
using Microsoft.Toolkit.Uwp.Notifications;
using ModernWpf;

namespace Cmux;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }
    public static SnippetService SnippetService { get; } = new();
    public static CommandLogService CommandLogService { get; } = new();
    public static DaemonClient DaemonClient { get; } = new();
    public static Task<bool> DaemonConnectTask { get; private set; } = Task.FromResult(false);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Prevent multiple instances
        _singleInstanceMutex = new Mutex(true, "Global\\CmuxWindowsSingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("cmux is already running.", "cmux", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Apply the user-selected app theme BEFORE base.OnStartup creates the StartupUri MainWindow,
        // so the window inherits the right merged dictionaries on first measure.
        ApplyAppTheme(SettingsService.Current.AppThemeMode);

        base.OnStartup(e);

        // Initialize the unified diagnostic logger (Serilog under the hood) as
        // early as possible so the rest of startup is captured.
        var logSettings = SettingsService.Current;
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");
        Directory.CreateDirectory(logDir);
        Cmux.Core.Logging.Log.Enabled = logSettings.EnableDiagnosticLogging;
        Cmux.Core.Logging.Log.MinLevel = ParseLogLevel(logSettings.DiagnosticLogLevel);
        Cmux.Core.Logging.Log.Configure(Path.Combine(logDir, "cmux-.log"));

        // Add global exception handlers to diagnose crashes
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRASH] DispatcherUnhandledException: {args.Exception}");
            System.Windows.MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            System.Diagnostics.Debug.WriteLine($"[CRASH] UnhandledException: {ex}");
            System.Windows.MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Start the named pipe server for CLI communication
        _pipeServer = new NamedPipeServer();
        PipeServer = _pipeServer;
        _pipeServer.Start();

        // Daemon connect: try existing daemon first, then start one if needed.
        // Sessions wait for this task before deciding local vs daemon mode.
        DaemonConnectTask = Task.Run(() =>
        {
            DaemonLog("[App] Phase 1: Quick daemon check (300ms)...");
            if (DaemonClient.TryConnect(300))
            {
                DaemonLog("[App] Phase 1: Daemon connected!");
                DaemonClient.RaiseConnected();
                return true;
            }
            DaemonLog("[App] Phase 1: Daemon not available, starting daemon...");

            var connected = DaemonClient.StartDaemonAndConnect();
            DaemonLog(connected
                ? "[App] Phase 2: Daemon started and connected"
                : "[App] Phase 2: Daemon failed to start");
            if (connected) DaemonClient.RaiseConnected();
            return connected;
        });

        // Register Windows toast activation. On unpackaged Win10/11 apps, subscribing to
        // OnActivated is what triggers ToastNotificationManagerCompat to create the Start
        // Menu shortcut + COM activator registration that the OS requires before any
        // toast.Show() will actually surface a notification.
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            DaemonLog("[App] Toast activator registered (AUMID: " +
                ToastNotificationManagerCompat.WasCurrentProcessToastActivated() + ")");
        }
        catch (Exception ex)
        {
            DaemonLog($"[App] Toast activator registration failed: {ex.Message}");
        }

        // Wire up Windows toast notifications. Gating rules are tested in
        // Cmux.Core.Services.ToastDispatchPolicy.
        NotificationService.NotificationAdded += notification =>
        {
            var mainWindow = Current.MainWindow;
            var settings = Cmux.Core.Config.SettingsService.Current;
            bool focused = mainWindow?.IsActive == true;

            if (!Cmux.Core.Services.ToastDispatchPolicy.ShouldDispatch(
                    toastsEnabled: settings.EnableToastNotifications,
                    showWhileFocused: settings.ShowToastsWhileFocused,
                    mainWindowFocused: focused))
            {
                DaemonLog($"[Toast] skipped id={notification.Id} (enabled={settings.EnableToastNotifications}, showWhileFocused={settings.ShowToastsWhileFocused}, focused={focused})");
                return;
            }

            DaemonLog($"[Toast] dispatching id={notification.Id} title={notification.Title}");
            var workspaceName = "Terminal"; // Will be enriched by MainViewModel
            Services.ToastNotificationHelper.ShowToast(notification, workspaceName);
        };
    }

    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Toast clicked — bring the window forward and jump to the notification's surface.
        Current.Dispatcher.Invoke(() =>
        {
            var mainWindow = Current.MainWindow;
            if (mainWindow == null) return;

            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
            mainWindow.Focus();

            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("notificationId", out var notificationId))
                NotificationService.MarkAsRead(notificationId);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        DaemonClient.Dispose();
        Cmux.Core.Logging.Log.Shutdown();
        if (_ownsMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Legacy entry point — routes through the unified <see cref="Cmux.Core.Logging.Log"/>
    /// so call sites that haven't been migrated still flow into the ring buffer
    /// and the in-app diagnostics view. New code should call <c>Log.Info(...)</c>
    /// directly with a meaningful category.
    /// </summary>
    internal static void DaemonLog(string message) => Cmux.Core.Logging.Log.Info("Daemon", message);

    /// <summary>
    /// Single source of truth for the displayed app version. Reads `<Version>` from the
    /// running assembly (set in Cmux.csproj) so MainWindow and Settings → About can never
    /// drift apart from each other.
    /// </summary>
    public static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            // .NET stamps Version as Major.Minor.Build.Revision; show 3 segments to match
            // semver-ish conventions used in commit messages and the README.
            return v == null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Loads the dark or light brand resource dictionary into Application.Resources and
    /// flips ModernWpf's <see cref="ThemeManager.ApplicationTheme"/>. Restart-required —
    /// existing windows already resolved their StaticResources before this point.
    /// </summary>
    private static void ApplyAppTheme(string mode)
    {
        ApplyAppTheme(mode, SettingsService.Current.AppThemeVariant);
    }

    private static void ApplyAppTheme(string mode, string variantName)
    {
        // This runs before base.OnStartup — i.e. before the unified Log is configured AND
        // before the DispatcherUnhandledException handler is wired. Any exception bubbles up
        // as a silent process exit, which looks like "no window opens" with nothing in the log.
        // Catch + crash-log to a side file so the failure is recoverable.
        try
        {
            var isLight = string.Equals(mode?.Trim(), "Light", StringComparison.OrdinalIgnoreCase);
            // Pack URI is required for runtime-loaded ResourceDictionaries — a bare relative
            // path silently fails to resolve assembly-embedded resources after compilation.
            // No `;component/<asm>` prefix: the running assembly is implied (works regardless
            // of whether AssemblyName is "Cmux" or "cmuxw").
            var path = isLight
                ? "pack://application:,,,/Themes/LightTheme.xaml"
                : "pack://application:,,,/Themes/DarkTheme.xaml";
            var dict = new ResourceDictionary { Source = new Uri(path, UriKind.Absolute) };
            Current.Resources.MergedDictionaries.Add(dict);

            // Variant overlay: redefines a handful of high-impact brushes (background,
            // foreground, accent, sidebar, surface, border) so non-Default variants like
            // Catppuccin / Dracula / GitHub repaint the chrome without us authoring a
            // full XAML dictionary per family. Last-merged-wins; appended after the base.
            var variant = Cmux.Themes.AppThemeCatalog.Find(variantName);
            if (variant != null && string.Equals(variant.Mode, isLight ? "Light" : "Dark", StringComparison.OrdinalIgnoreCase))
            {
                var overlay = Cmux.Themes.AppThemeCatalog.BuildOverlay(variant);
                if (overlay != null)
                    Current.Resources.MergedDictionaries.Add(overlay);
            }

            ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch (Exception ex)
        {
            try
            {
                var crashLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cmux", "theme-crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
                File.AppendAllText(crashLog, $"{DateTime.UtcNow:O} mode={mode} {ex}\n");
            }
            catch { /* last-resort logger; swallow */ }
            // Fall back to whatever ModernWpf defaults to so the app still launches.
        }
    }

    private static Cmux.Core.Logging.LogLevel ParseLogLevel(string raw) => (raw ?? "info").Trim().ToLowerInvariant() switch
    {
        "debug" => Cmux.Core.Logging.LogLevel.Debug,
        "warn" or "warning" => Cmux.Core.Logging.LogLevel.Warn,
        "error" => Cmux.Core.Logging.LogLevel.Error,
        _ => Cmux.Core.Logging.LogLevel.Info,
    };
}
