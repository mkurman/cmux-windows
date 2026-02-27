using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Config;
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
    private readonly Dictionary<string, List<string>> _paneCommandHistory = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private SplitNode _rootNode;

    [ObservableProperty]
    private string? _focusedPaneId;

    [ObservableProperty]
    private bool _isZoomed;

    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>Gets the shell process PID from the focused pane session.</summary>
    public int? ShellPid
    {
        get
        {
            if (FocusedPaneId == null) return null;
            var session = GetSession(FocusedPaneId);
            return session?.ProcessId;
        }
    }

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
            {
                Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot);
                if (snapshot?.CommandHistory is { Count: > 0 })
                {
                    _paneCommandHistory[leaf.PaneId] = snapshot.CommandHistory
                        .Select(App.CommandLogService.SanitizeCommandForStorage)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Cast<string>()
                        .ToList();
                }

                StartSession(leaf.PaneId, snapshot?.WorkingDirectory, snapshot);
            }
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

    public string GetPaneTitle(string paneId, string? fallbackTitle)
    {
        if (Surface.PaneCustomNames.TryGetValue(paneId, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;

        return fallbackTitle ?? "Terminal";
    }

    public void SetPaneCustomName(string paneId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            Surface.PaneCustomNames.Remove(paneId);
        else
            Surface.PaneCustomNames[paneId] = name.Trim();

        OnPropertyChanged(nameof(RootNode));
    }

    public IReadOnlyList<string> GetCommandHistory(string paneId)
    {
        return _paneCommandHistory.TryGetValue(paneId, out var history)
            ? history.AsReadOnly()
            : [];
    }

    private static bool ShouldCaptureTranscript(string reason)
    {
        var settings = SettingsService.Current;

        if (string.Equals(reason, "clear-terminal", StringComparison.OrdinalIgnoreCase))
            return settings.CaptureTranscriptsOnClear;

        return settings.CaptureTranscriptsOnClose;
    }

    public string? CapturePaneTranscript(string paneId, string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return null;

        if (!_sessions.TryGetValue(paneId, out var session))
            return null;

        var text = session.Buffer.ExportPlainText(maxScrollbackLines: 20000);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return App.CommandLogService.SaveTerminalTranscript(
            _workspaceId,
            Surface.Id,
            paneId,
            session.WorkingDirectory,
            text,
            reason);
    }

    public int CaptureAllPaneTranscripts(string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return 0;

        int captured = 0;

        var paneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var paneId in paneIds)
        {
            if (CapturePaneTranscript(paneId, reason) != null)
                captured++;
        }

        return captured;
    }

    public void CapturePaneSnapshotsForPersistence()
    {
        var activePaneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet();

        foreach (var paneId in activePaneIds)
        {
            if (!_sessions.TryGetValue(paneId, out var session))
                continue;

            var state = Surface.PaneSnapshots.TryGetValue(paneId, out var existing)
                ? existing
                : new PaneStateSnapshot();

            state.CapturedAt = DateTime.UtcNow;
            state.WorkingDirectory = session.WorkingDirectory;
            state.BufferSnapshot = session.CreateBufferSnapshot(maxScrollbackLines: 3000);

            if (_paneCommandHistory.TryGetValue(paneId, out var history))
                state.CommandHistory = history.TakeLast(500).ToList();

            Surface.PaneSnapshots[paneId] = state;
        }

        var stalePaneIds = Surface.PaneSnapshots.Keys.Where(id => !activePaneIds.Contains(id)).ToList();
        foreach (var paneId in stalePaneIds)
            Surface.PaneSnapshots.Remove(paneId);
    }

    public void RegisterCommandSubmission(string paneId, string command)
    {
        var sanitized = App.CommandLogService.SanitizeCommandForStorage(command);
        if (string.IsNullOrWhiteSpace(sanitized))
            return;

        AppendToCommandHistory(paneId, sanitized);

        var cwd = _sessions.TryGetValue(paneId, out var session)
            ? session.WorkingDirectory
            : null;

        App.CommandLogService.RecordManualCommandSubmission(
            paneId,
            _workspaceId,
            Surface.Id,
            sanitized,
            cwd);
    }

    private void AppendToCommandHistory(string paneId, string command)
    {
        if (!_paneCommandHistory.TryGetValue(paneId, out var history))
        {
            history = [];
            _paneCommandHistory[paneId] = history;
        }

        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        if (history.Count == 0 || !string.Equals(history[^1], trimmed, StringComparison.Ordinal))
            history.Add(trimmed);

        while (history.Count > 500)
            history.RemoveAt(0);
    }

    private TerminalSession StartSession(string paneId, string? workingDirectory = null, PaneStateSnapshot? restoredState = null)
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

        session.ShellPromptMarker += (marker, payload) =>
        {
            App.CommandLogService.HandlePromptMarker(
                paneId,
                _workspaceId,
                Surface.Id,
                marker,
                payload,
                session.WorkingDirectory);

            if (marker == 'B')
            {
                var sanitized = App.CommandLogService.SanitizeCommandForStorage(payload);
                if (!string.IsNullOrWhiteSpace(sanitized))
                    AppendToCommandHistory(paneId, sanitized);
            }
        };

        _sessions[paneId] = session;
        session.Start(workingDirectory: workingDirectory ?? restoredState?.WorkingDirectory);

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

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

        CapturePaneTranscript(paneId, "pane-close");

        // Get adjacent pane before removal
        var nextLeaf = RootNode.GetNextLeaf(paneId) ?? RootNode.GetPreviousLeaf(paneId);
        string? nextPaneId = nextLeaf?.PaneId;

        // Stop and remove the session
        if (_sessions.TryGetValue(paneId, out var session))
        {
            session.Dispose();
            _sessions.Remove(paneId);
        }

        Surface.PaneCustomNames.Remove(paneId);
        Surface.PaneSnapshots.Remove(paneId);
        _paneCommandHistory.Remove(paneId);

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


    [RelayCommand]
    public void ToggleZoom() => IsZoomed = !IsZoomed;

    public void EqualizePanes()
    {
        RootNode.Equalize();
        OnPropertyChanged(nameof(RootNode));
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
        CapturePaneSnapshotsForPersistence();

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
