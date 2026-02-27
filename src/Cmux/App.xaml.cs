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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        base.OnExit(e);
    }
}
