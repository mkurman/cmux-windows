using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;
using Cmux.Services;

namespace Cmux;

public partial class App : Application
{
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }
    public static SnippetService SnippetService { get; } = new();
    public static CommandLogService CommandLogService { get; } = new();
    public static AgentConversationStoreService AgentConversationStore { get; } = new();
    public static AgentRuntimeService AgentRuntime { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
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

        // Wire up Windows toast notifications
        NotificationService.NotificationAdded += notification =>
        {
            // Only show toast when the app window is not focused
            var mainWindow = Current.MainWindow;
            if (mainWindow != null && !mainWindow.IsActive)
            {
                var workspaceName = "Terminal"; // Will be enriched by MainViewModel
                Services.ToastNotificationHelper.ShowToast(notification, workspaceName);
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        AgentRuntime.Dispose();
        base.OnExit(e);
    }
}
