using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;

namespace Cmux.ViewModels;

public partial class SurfaceViewModel : ObservableObject, IDisposable
{
    public Surface Surface { get; }
    private readonly string _workspaceId;
    private readonly NotificationService _notificationService;
    private readonly Dictionary<string, TerminalSession> _sessions = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private SplitNode _rootNode;

    [ObservableProperty]
    private string? _focusedPaneId;

    public event Action<string>? WorkingDirectoryChanged;

    public SurfaceViewModel(Surface surface, string workspaceId, NotificationService notificationService)
    {
        Surface = surface;
        _workspaceId = workspaceId;
        _notificationService = notificationService;
        _name = surface.Name;
        _rootNode = surface.RootSplitNode;
        _focusedPaneId = surface.FocusedPaneId;

        // Start terminal sessions for all leaf nodes
        foreach (var leaf in _rootNode.GetLeaves())
        {
            if (leaf.PaneId != null)
                StartSession(leaf.PaneId);
        }

        if (_focusedPaneId == null)
        {
            var firstLeaf = _rootNode.GetLeaves().FirstOrDefault();
            if (firstLeaf?.PaneId != null)
                FocusedPaneId = firstLeaf.PaneId;
        }
    }

    public TerminalSession? GetSession(string paneId)
    {
        return _sessions.GetValueOrDefault(paneId);
    }

    private TerminalSession StartSession(string paneId, string? workingDirectory = null)
    {
        var session = new TerminalSession(paneId);

        session.WorkingDirectoryChanged += dir =>
        {
            if (paneId == FocusedPaneId)
                WorkingDirectoryChanged?.Invoke(dir);
        };

        session.NotificationReceived += (title, subtitle, body) =>
        {
            var source = NotificationSource.Osc9; // Default
            _notificationService.AddNotification(
                _workspaceId, Surface.Id, paneId,
                title, subtitle, body, source);
        };

        _sessions[paneId] = session;
        session.Start(workingDirectory: workingDirectory);
        return session;
    }

    [RelayCommand]
    public void SplitRight()
    {
        SplitFocused(SplitDirection.Vertical);
    }

    [RelayCommand]
    public void SplitDown()
    {
        SplitFocused(SplitDirection.Horizontal);
    }

    public void SplitFocused(SplitDirection direction)
    {
        if (FocusedPaneId == null) return;

        var node = RootNode.FindNode(FocusedPaneId);
        if (node == null || !node.IsLeaf) return;

        var newChild = node.Split(direction);
        if (newChild.PaneId != null)
        {
            var currentSession = GetSession(FocusedPaneId);
            var cwd = currentSession?.WorkingDirectory;
            StartSession(newChild.PaneId, cwd);
            FocusedPaneId = newChild.PaneId;
        }

        // Trigger UI update
        OnPropertyChanged(nameof(RootNode));
    }

    [RelayCommand]
    public void ClosePane()
    {
        ClosePane(FocusedPaneId);
    }

    public void ClosePane(string? paneId)
    {
        if (paneId == null) return;

        // Get adjacent pane before removal
        var nextLeaf = RootNode.GetNextLeaf(paneId) ?? RootNode.GetPreviousLeaf(paneId);
        string? nextPaneId = nextLeaf?.PaneId;

        // Stop and remove the session
        if (_sessions.TryGetValue(paneId, out var session))
        {
            session.Dispose();
            _sessions.Remove(paneId);
        }

        // If this is the only pane, don't remove it
        var leaves = RootNode.GetLeaves().ToList();
        if (leaves.Count <= 1) return;

        RootNode.Remove(paneId);

        if (paneId == FocusedPaneId)
            FocusedPaneId = nextPaneId;

        OnPropertyChanged(nameof(RootNode));
    }

    public void FocusPane(string paneId)
    {
        FocusedPaneId = paneId;
        Surface.FocusedPaneId = paneId;
    }

    [RelayCommand]
    public void FocusNextPane()
    {
        if (FocusedPaneId == null) return;
        var next = RootNode.GetNextLeaf(FocusedPaneId);
        if (next?.PaneId != null)
            FocusPane(next.PaneId);
    }

    [RelayCommand]
    public void FocusPreviousPane()
    {
        if (FocusedPaneId == null) return;
        var prev = RootNode.GetPreviousLeaf(FocusedPaneId);
        if (prev?.PaneId != null)
            FocusPane(prev.PaneId);
    }

    partial void OnFocusedPaneIdChanged(string? value)
    {
        Surface.FocusedPaneId = value;
    }

    partial void OnNameChanged(string value)
    {
        Surface.Name = value;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
