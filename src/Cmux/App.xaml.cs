using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;

namespace Cmux;

public partial class App : Application
{
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        base.OnExit(e);
    }
}
