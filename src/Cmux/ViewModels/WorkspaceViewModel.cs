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
    private bool _isPinned;

    public string IconFontFamily => IsPrivateUseGlyph(IconGlyph) ? "Segoe MDL2 Assets" : "Segoe UI Emoji";

    private readonly NotificationService _notificationService;
    private System.Threading.Timer? _infoRefreshTimer;

    public WorkspaceViewModel(Workspace workspace, NotificationService notificationService)
    {
        Workspace = workspace;
        _name = workspace.Name;
        _iconGlyph = workspace.IconGlyph;
        _accentColor = workspace.AccentColor;
        _isPinned = workspace.IsPinned;
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
        _infoRefreshTimer = new System.Threading.Timer(_ => RefreshInfo(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Raised when CloseSurface is called on the last remaining surface in the workspace.
    /// MainViewModel listens and prompts the user to close the entire workspace.
    /// </summary>
    public event EventHandler? LastSurfaceCloseRequested;

    [RelayCommand]
    public void CreateNewSurface()
    {
        var surface = new Surface { Name = NextSurfaceName() };

        // Inherit the working directory from the currently focused pane of the
        // active surface, so a new tab opens at the same place the user is
        // currently working. Falls back to the workspace's last-known cwd so
        // the very first tab in a workspace starts somewhere sensible.
        var inheritedCwd = SelectedSurface?.GetFocusedPaneWorkingDirectory()
                           ?? WorkingDirectory
                           ?? Workspace.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(inheritedCwd))
        {
            var firstLeaf = surface.RootSplitNode.GetLeaves().FirstOrDefault();
            if (firstLeaf?.PaneId is { } paneId)
            {
                surface.PaneSnapshots[paneId] = new PaneStateSnapshot
                {
                    WorkingDirectory = inheritedCwd,
                };
            }
        }

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

        // Closing the last surface in a workspace is treated as a request to close the
        // whole workspace — surface us a confirmation through the parent (MainViewModel)
        // rather than silently no-op'ing as the old code did.
        if (Surfaces.Count <= 1)
        {
            LastSurfaceCloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

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

    /// <summary>
    /// Picks "Terminal N" with the smallest N that doesn't collide with an existing
    /// surface name. Falls back to count+1 if no existing names match the pattern —
    /// preserves the historical default for fresh workspaces.
    /// </summary>
    private string NextSurfaceName()
    {
        var used = new HashSet<int>();
        foreach (var s in Surfaces)
        {
            var name = s.Name ?? string.Empty;
            if (name.StartsWith("Terminal ", StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan("Terminal ".Length), out var n))
            {
                used.Add(n);
            }
        }
        var candidate = 1;
        while (used.Contains(candidate)) candidate++;
        // If the workspace has no Terminal-N tabs at all, count+1 matches the original
        // behavior (e.g. first tab in a fresh workspace gets "Terminal 1" via Workspace ctor).
        return used.Count == 0 ? $"Terminal {Surfaces.Count + 1}" : $"Terminal {candidate}";
    }

    /// <summary>
    /// VM-side detach: drops the surface from the VM list and unwires events.
    /// The underlying <see cref="Workspace.Surfaces"/> mutation is the caller's
    /// responsibility (typically <see cref="Cmux.Core.Services.SurfaceMover"/>).
    /// </summary>
    internal void DetachSurface(SurfaceViewModel surface)
    {
        if (!Surfaces.Contains(surface)) return;

        surface.WorkingDirectoryChanged -= OnSurfaceWorkingDirectoryChanged;

        int index = Surfaces.IndexOf(surface);
        Surfaces.Remove(surface);

        if (SelectedSurface == surface)
            SelectedSurface = Surfaces.Count > 0 ? Surfaces[Math.Min(index, Surfaces.Count - 1)] : null;
    }

    /// <summary>
    /// VM-side attach: registers the surface VM and re-wires events. The underlying
    /// <see cref="Workspace.Surfaces"/> mutation is done by the caller.
    /// </summary>
    internal void AttachSurface(SurfaceViewModel surface)
    {
        surface.WorkspaceId = Workspace.Id;
        Surfaces.Add(surface);
        surface.WorkingDirectoryChanged += OnSurfaceWorkingDirectoryChanged;
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

            // Scan ports for the active surface's shell process
            var activeSurface = SelectedSurface;
            if (activeSurface?.ShellPid is int pid and > 0)
            {
                var ports = PortScanner.GetListeningPorts(pid);
                var display = ports.Count > 0 ? string.Join(" ", ports) : "";
                if (display != PortsDisplay)
                    PortsDisplay = display;
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

    partial void OnIsPinnedChanged(bool value)
    {
        Workspace.IsPinned = value;
    }

    partial void OnSelectedSurfaceChanged(SurfaceViewModel? value)
    {
        // Activating a tab implicitly clears its unread state — drives the
        // tab notification dot off and lets the pane ring fade.
        if (value != null)
            _notificationService.MarkSurfaceAsRead(value.Surface.Id);
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
