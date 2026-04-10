using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    public Workspace Workspace { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _iconGlyph;

    [ObservableProperty]
    private string _accentColor;

    [ObservableProperty]
    private ObservableCollection<SurfaceViewModel> _surfaces = [];

    [ObservableProperty]
    private SurfaceViewModel? _selectedSurface;

    [ObservableProperty]
    private string? _gitBranch;

    [ObservableProperty]
    private string? _workingDirectory;

    [ObservableProperty]
    private string? _latestNotificationText;

    [ObservableProperty]
    private int _unreadNotificationCount;

    [ObservableProperty]
    private string _portsDisplay = "";

    [ObservableProperty]
    private bool _hasNotification;

    [ObservableProperty]
    private AgentType _detectedAgent;

    public string AgentLabel => AgentDetector.GetLabel(DetectedAgent);
    public string AgentIcon => AgentDetector.GetIcon(DetectedAgent);
    public string IconFontFamily => IsPrivateUseGlyph(IconGlyph) ? "Segoe MDL2 Assets" : "Segoe UI Emoji";

    private readonly NotificationService _notificationService;
    private System.Threading.Timer? _infoRefreshTimer;


    public WorkspaceViewModel(Workspace workspace, NotificationService notificationService)
    {
        Workspace = workspace;
        _name = workspace.Name;
        _iconGlyph = workspace.IconGlyph;
        _accentColor = workspace.AccentColor;
        _notificationService = notificationService;

        // Create surface VMs for existing surfaces
        foreach (var surface in workspace.Surfaces)
        {
            var surfaceVm = new SurfaceViewModel(surface, workspace.Id, notificationService);
            surfaceVm.WorkingDirectoryChanged += OnSurfaceWorkingDirectoryChanged;
            Surfaces.Add(surfaceVm);
        }

        if (workspace.SelectedSurface != null)
        {
            SelectedSurface = Surfaces.FirstOrDefault(s => s.Surface.Id == workspace.SelectedSurface.Id);
        }
        else if (Surfaces.Count > 0)
        {
            SelectedSurface = Surfaces[0];
        }

        // Start periodic info refresh (git branch, ports)
        _infoRefreshTimer = new System.Threading.Timer(_ => RefreshInfo(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [RelayCommand]
    public void CreateNewSurface()
    {
        var surface = new Surface { Name = $"Terminal {Surfaces.Count + 1}" };
        Workspace.Surfaces.Add(surface);

        var surfaceVm = new SurfaceViewModel(surface, Workspace.Id, _notificationService);
        surfaceVm.WorkingDirectoryChanged += OnSurfaceWorkingDirectoryChanged;
        Surfaces.Add(surfaceVm);
        SelectedSurface = surfaceVm;
    }

    [RelayCommand]
    public void CloseSurface(SurfaceViewModel? surface)
    {
        if (surface == null) return;
        if (Surfaces.Count <= 1) return; // Keep at least one

        int index = Surfaces.IndexOf(surface);
        surface.CaptureAllPaneTranscripts("surface-close");
        surface.Dispose();
        Surfaces.Remove(surface);
        Workspace.Surfaces.Remove(surface.Surface);

        if (SelectedSurface == surface)
        {
            SelectedSurface = Surfaces[Math.Min(index, Surfaces.Count - 1)];
        }
    }

    [RelayCommand]
    public void NextSurface()
    {
        if (Surfaces.Count == 0) return;
        int index = SelectedSurface != null ? Surfaces.IndexOf(SelectedSurface) : -1;
        SelectedSurface = Surfaces[(index + 1) % Surfaces.Count];
    }

    [RelayCommand]
    public void PreviousSurface()
    {
        if (Surfaces.Count == 0) return;
        int index = SelectedSurface != null ? Surfaces.IndexOf(SelectedSurface) : 0;
        SelectedSurface = Surfaces[(index - 1 + Surfaces.Count) % Surfaces.Count];
    }

    [RelayCommand]
    public void Rename()
    {
        // This would be handled by the view showing an input box
    }

    private void OnSurfaceWorkingDirectoryChanged(string directory)
    {
        WorkingDirectory = directory;
        Workspace.WorkingDirectory = directory;
    }

    private void RefreshInfo()
    {
        try
        {
            var dir = WorkingDirectory ?? Workspace.WorkingDirectory;
            if (!string.IsNullOrEmpty(dir))
            {
                var branch = GitService.GetBranch(dir);
                if (branch != GitBranch)
                {
                    GitBranch = branch;
                    Workspace.GitBranch = branch;
                }
            }

            // AI agent detection (works for local sessions with ShellPid)
            var activeSurface = SelectedSurface;
            if (activeSurface?.ShellPid is int pid and > 0)
            {
                var agent = AgentDetector.DetectFromProcessId(pid);
                if (agent != DetectedAgent)
                {
                    DetectedAgent = agent;
                    OnPropertyChanged(nameof(AgentLabel));
                    OnPropertyChanged(nameof(AgentIcon));
                }
            }

            // Output idle detection: works for both daemon and local sessions.
            // When terminal output has been active for 2+ seconds and then goes
            // quiet for 5+ seconds, create a notification.
            if (activeSurface != null)
            {
                var lastOutput = activeSurface.LastOutputTicks;
                var burstStart = activeSurface.OutputBurstStartTicks;
                if (lastOutput > 0 && burstStart > 0)
                {
                    var now = DateTime.UtcNow.Ticks;
                    var quietSeconds = (now - lastOutput) / TimeSpan.TicksPerSecond;
                    var burstDuration = (lastOutput - burstStart) / TimeSpan.TicksPerSecond;

                    // Only notify if user has actually sent input to the terminal.
                    // This prevents false notifications from shell startup, daemon
                    // reconnect output, terminal redraws, etc.
                    if (quietSeconds >= 5 && burstDuration >= 2 && !activeSurface.IdleNotified && activeSurface.HasUserInput)
                    {
                        activeSurface.IdleNotified = true;
                        var label = DetectedAgent != AgentType.None
                            ? AgentDetector.GetLabel(DetectedAgent)
                            : "Terminal";
                        // Must dispatch to UI thread — RefreshInfo runs on ThreadPool
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            _notificationService.AddNotification(
                                Workspace.Id,
                                activeSurface.Surface.Id,
                                paneId: null,
                                title: $"{label} ready",
                                subtitle: null,
                                body: "Task output has completed.",
                                source: NotificationSource.AgentCompleted);
                        });
                        // Reset tracking so next input→output cycle can trigger again
                        activeSurface.OutputBurstStartTicks = 0;
                        activeSurface.HasUserInput = false;
                    }
                    else if (quietSeconds < 2)
                    {
                        // Still active — reset idle flag
                        activeSurface.IdleNotified = false;
                    }
                }
            }
        }
        catch
        {
            // Non-critical
        }
    }

    partial void OnUnreadNotificationCountChanged(int value)
    {
        HasNotification = value > 0;
    }

    partial void OnNameChanged(string value)
    {
        Workspace.Name = value;
    }

    partial void OnIconGlyphChanged(string value)
    {
        Workspace.IconGlyph = value;
        OnPropertyChanged(nameof(IconFontFamily));
    }

    partial void OnAccentColorChanged(string value)
    {
        Workspace.AccentColor = value;
    }

    partial void OnSelectedSurfaceChanged(SurfaceViewModel? value)
    {
        // Reset idle tracking when switching surfaces/tabs to avoid
        // false notifications from terminal reconnect/redraw output.
        if (value != null)
        {
            value.OutputBurstStartTicks = 0;
            value.LastOutputTicks = 0;
        }
        if (value != null)
            value.IdleNotified = false;
    }

    private static bool IsPrivateUseGlyph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int codePoint = char.ConvertToUtf32(value, 0);
        return codePoint is >= 0xE000 and <= 0xF8FF;
    }

    public int CaptureAllSurfaceTranscripts(string reason)
    {
        int captured = 0;
        foreach (var surface in Surfaces)
            captured += surface.CaptureAllPaneTranscripts(reason);

        return captured;
    }

    public void Dispose()
    {
        _infoRefreshTimer?.Dispose();
        foreach (var surface in Surfaces)
            surface.Dispose();
    }
}
