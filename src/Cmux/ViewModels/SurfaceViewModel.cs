using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Config;
using Cmux.Core.IPC;
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
    private readonly HashSet<string> _daemonPanes = [];
    private readonly HashSet<string> _daemonOutputLogged = [];
    private static readonly object _daemonWaitLock = new();
    private static bool _daemonWaitDone;

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

        // Wire daemon events for session persistence
        var daemon = App.DaemonClient;
        daemon.RawOutputReceived += OnDaemonRawOutput;
        daemon.CwdChanged += OnDaemonCwdChanged;
        daemon.TitleChanged += OnDaemonTitleChanged;
        daemon.SessionExited += OnDaemonSessionExited;
        daemon.BellReceived += OnDaemonBellReceived;
        daemon.Disconnected += OnDaemonDisconnected;

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

    private void OnDaemonRawOutput(string paneId, byte[] data)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        if (_sessions.TryGetValue(paneId, out var session))
            session.FeedOutput(data);
    }

    private void OnDaemonCwdChanged(string paneId, string dir)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        // Update the session's WorkingDirectory so it's captured in snapshots
        if (_sessions.TryGetValue(paneId, out var session))
            session.WorkingDirectory = dir;
        if (paneId == FocusedPaneId)
            WorkingDirectoryChanged?.Invoke(dir);
    }

    private void OnDaemonTitleChanged(string paneId, string title)
    {
        if (!_daemonPanes.Contains(paneId)) return;
    }

    private void OnDaemonSessionExited(string paneId, int exitCode)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        _daemonPanes.Remove(paneId);
    }

    private void OnDaemonBellReceived(string paneId)
    {
        if (!_daemonPanes.Contains(paneId)) return;
    }

    private void OnDaemonDisconnected()
    {
        // Daemon died — fall back all daemon sessions to local ConPTY
        var paneIds = _daemonPanes.ToList();
        if (paneIds.Count == 0) return;

        DaemonLog($"[DaemonDisconnected] Falling back {paneIds.Count} sessions to local ConPTY");

        foreach (var paneId in paneIds)
        {
            if (!_sessions.TryGetValue(paneId, out var session)) continue;

            var cwd = session.WorkingDirectory;
            session.DaemonWrite = null;
            session.DaemonResize = null;

            try
            {
                session.Start(workingDirectory: cwd);
                DaemonLog($"[DaemonDisconnected] {paneId} → local session started");
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonDisconnected] {paneId} → local start failed: {ex.Message}");
            }
        }

        _daemonPanes.Clear();
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

    public bool TryHandlePaneCommand(string paneId, string command)
    {
        if (!_sessions.TryGetValue(paneId, out var session))
            return false;

        return App.AgentRuntime.TryHandlePaneCommand(
            command,
            new Cmux.Services.AgentPaneContext
            {
                WorkspaceId = _workspaceId,
                SurfaceId = Surface.Id,
                PaneId = paneId,
                WorkingDirectory = session.WorkingDirectory,
                WriteToPane = text =>
                {
                    if (!string.IsNullOrEmpty(text))
                        session.Write(text);
                },
            });
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
        // Wait for daemon connect task (includes starting daemon if needed).
        // First pane blocks up to 5s; subsequent panes get the cached result instantly.
        lock (_daemonWaitLock)
        {
            if (!_daemonWaitDone)
            {
                DaemonLog($"[StartSession:{paneId}] Waiting for daemon connect task...");
                try { App.DaemonConnectTask.Wait(5000); }
                catch { /* timeout or connect failure — proceed with local */ }
                _daemonWaitDone = true;
            }
        }

        var daemonReady = App.DaemonConnectTask.IsCompletedSuccessfully
                          && App.DaemonConnectTask.Result;

        DaemonLog($"[StartSession:{paneId}] daemonReady={daemonReady}, IsConnected={App.DaemonClient.IsConnected}, TaskStatus={App.DaemonConnectTask.Status}");

        // Try daemon-backed session first
        if (daemonReady)
        {
            try
            {
                return StartDaemonSession(paneId, workingDirectory, restoredState);
            }
            catch (Exception ex)
            {
                DaemonLog($"[StartSession:{paneId}] Daemon session failed: {ex.Message}");
            }
        }

        DaemonLog($"[StartSession:{paneId}] Using LOCAL session");
        return StartLocalSession(paneId, workingDirectory, restoredState);
    }

    private static void DaemonLog(string message) => App.DaemonLog(message);

    private TerminalSession StartDaemonSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState)
    {
        // Use saved snapshot dimensions if available (avoids spurious resize on reconnect)
        var initCols = restoredState?.BufferSnapshot?.Cols ?? 120;
        var initRows = restoredState?.BufferSnapshot?.Rows ?? 30;
        var session = new TerminalSession(paneId, initCols, initRows);
        WireSessionEvents(session, paneId);

        // Set daemon delegates so Write/Resize route through daemon
        var daemon = App.DaemonClient;
        session.DaemonWrite = data => daemon.WriteAsync(paneId, data);
        session.DaemonResize = (cols, rows) => daemon.ResizeAsync(paneId, cols, rows);

        _sessions[paneId] = session;
        _daemonPanes.Add(paneId);

        var effectiveCwd = workingDirectory ?? restoredState?.WorkingDirectory;

        // Create/attach session on daemon asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                DaemonLog($"[DaemonSession:{paneId}] Calling CreateSessionAsync ({initCols}x{initRows})...");
                var result = await daemon.CreateSessionAsync(
                    paneId, initCols, initRows, effectiveCwd);

                if (result == null)
                {
                    DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync returned NULL — falling back to local");
                    _daemonPanes.Remove(paneId);
                    session.DaemonWrite = null;
                    session.DaemonResize = null;
                    session.Start(workingDirectory: effectiveCwd);
                    return;
                }

                DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync OK: IsExisting={result.IsExisting}, IsRunning={result.IsRunning}, Cwd={result.WorkingDirectory}");

                // Set working directory from daemon response
                if (!string.IsNullOrEmpty(result.WorkingDirectory))
                    session.WorkingDirectory = result.WorkingDirectory;

                // If reconnecting to an existing daemon session, get the live buffer snapshot
                if (result.IsExisting && result.IsRunning)
                {
                    DaemonLog($"[DaemonSession:{paneId}] Reconnecting — fetching daemon snapshot...");
                    var snapshotJson = await daemon.GetSnapshotAsync(paneId);
                    if (snapshotJson != null)
                    {
                        try
                        {
                            var snapshot = System.Text.Json.JsonSerializer.Deserialize<TerminalBufferSnapshot>(snapshotJson);
                            if (snapshot != null)
                            {
                                session.RestoreBufferSnapshot(snapshot);
                                DaemonLog($"[DaemonSession:{paneId}] Snapshot restored ({snapshotJson.Length} chars)");
                            }
                        }
                        catch (Exception ex)
                        {
                            DaemonLog($"[DaemonSession:{paneId}] Snapshot restore error: {ex.Message}");
                        }
                    }
                    else
                    {
                        DaemonLog($"[DaemonSession:{paneId}] GetSnapshotAsync returned null");
                    }

                    // Send Enter after a brief delay to force the shell to print a fresh prompt.
                    // The snapshot restores scrollback but the prompt line may be missing
                    // because the shell already printed it before disconnect.
                    await Task.Delay(300);
                    await daemon.WriteAsync(paneId, [0x0d]); // CR = Enter
                }
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonSession:{paneId}] Exception — falling back to local: {ex.Message}");
                _daemonPanes.Remove(paneId);
                session.DaemonWrite = null;
                session.DaemonResize = null;
                session.Start(workingDirectory: effectiveCwd);
            }
        });

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        return session;
    }

    private TerminalSession StartLocalSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState)
    {
        var session = new TerminalSession(paneId);
        WireSessionEvents(session, paneId);

        _sessions[paneId] = session;
        session.Start(workingDirectory: workingDirectory ?? restoredState?.WorkingDirectory);

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        return session;
    }

    private void WireSessionEvents(TerminalSession session, string paneId)
    {
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
            if (_daemonPanes.Remove(paneId))
                _ = App.DaemonClient.CloseSessionAsync(paneId);
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

        // Unwire daemon events
        var daemon = App.DaemonClient;
        daemon.RawOutputReceived -= OnDaemonRawOutput;
        daemon.CwdChanged -= OnDaemonCwdChanged;
        daemon.TitleChanged -= OnDaemonTitleChanged;
        daemon.SessionExited -= OnDaemonSessionExited;
        daemon.BellReceived -= OnDaemonBellReceived;
        daemon.Disconnected -= OnDaemonDisconnected;

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _daemonPanes.Clear();
    }
}
