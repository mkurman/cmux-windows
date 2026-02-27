using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.IPC;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<WorkspaceViewModel> _workspaces = [];

    [ObservableProperty]
    private WorkspaceViewModel? _selectedWorkspace;

    [ObservableProperty]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private double _sidebarWidth = 280;

    [ObservableProperty]
    private bool _notificationPanelVisible;

    [ObservableProperty]
    private int _totalUnreadCount;

    private readonly NotificationService _notificationService;

    public NotificationService NotificationService => _notificationService;

    public MainViewModel()
    {
        _notificationService = App.NotificationService;
        _notificationService.UnreadCountChanged += () =>
        {
            TotalUnreadCount = _notificationService.UnreadCount;
            UpdateWorkspaceNotificationCounts();
        };

        // Wire up the named pipe command handler
        if (App.PipeServer != null)
        {
            App.PipeServer.OnCommand = HandlePipeCommand;
        }

        // Restore session or create default workspace
        var session = SessionPersistenceService.Load();
        if (session != null && session.Workspaces.Count > 0)
        {
            RestoreSession(session);
        }
        else
        {
            CreateNewWorkspace();
        }
    }

    [RelayCommand]
    public void CreateNewWorkspace()
    {
        var workspace = new Workspace { Name = $"Workspace {Workspaces.Count + 1}" };
        var surface = new Surface { Name = "Terminal 1" };
        workspace.Surfaces.Add(surface);
        workspace.SelectedSurface = surface;

        var vm = new WorkspaceViewModel(workspace, _notificationService);
        Workspaces.Add(vm);
        SelectedWorkspace = vm;
    }

    [RelayCommand]
    public void CloseWorkspace(WorkspaceViewModel? workspace)
    {
        if (workspace == null) return;
        if (Workspaces.Count <= 1) return; // Keep at least one

        int index = Workspaces.IndexOf(workspace);
        workspace.Dispose();
        Workspaces.Remove(workspace);

        if (SelectedWorkspace == workspace)
        {
            SelectedWorkspace = Workspaces[Math.Min(index, Workspaces.Count - 1)];
        }
    }

    [RelayCommand]
    public void SelectWorkspace(int index)
    {
        if (index >= 0 && index < Workspaces.Count)
        {
            SelectedWorkspace = Workspaces[index];
        }
    }

    [RelayCommand]
    public void NextWorkspace()
    {
        if (Workspaces.Count == 0) return;
        int index = SelectedWorkspace != null ? Workspaces.IndexOf(SelectedWorkspace) : -1;
        SelectedWorkspace = Workspaces[(index + 1) % Workspaces.Count];
    }

    [RelayCommand]
    public void PreviousWorkspace()
    {
        if (Workspaces.Count == 0) return;
        int index = SelectedWorkspace != null ? Workspaces.IndexOf(SelectedWorkspace) : 0;
        SelectedWorkspace = Workspaces[(index - 1 + Workspaces.Count) % Workspaces.Count];
    }

    [RelayCommand]
    public void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    public void ToggleNotificationPanel() => NotificationPanelVisible = !NotificationPanelVisible;

    [RelayCommand]
    public void JumpToLatestUnread()
    {
        var latest = _notificationService.GetLatestUnread();
        if (latest == null) return;

        // Find the workspace and surface
        var workspace = Workspaces.FirstOrDefault(w => w.Workspace.Id == latest.WorkspaceId);
        if (workspace != null)
        {
            SelectedWorkspace = workspace;
            var surface = workspace.Surfaces.FirstOrDefault(s => s.Surface.Id == latest.SurfaceId);
            if (surface != null)
            {
                workspace.SelectedSurface = surface;
                if (latest.PaneId != null)
                {
                    surface.FocusPane(latest.PaneId);
                }
            }
            _notificationService.MarkAsRead(latest.Id);
        }
    }

    [RelayCommand]
    public void MarkAllNotificationsRead()
    {
        _notificationService.MarkAllAsRead();
    }

    private void UpdateWorkspaceNotificationCounts()
    {
        foreach (var ws in Workspaces)
        {
            ws.UnreadNotificationCount = _notificationService.GetUnreadCount(ws.Workspace.Id);
            ws.LatestNotificationText = _notificationService.GetLatestText(ws.Workspace.Id);
        }
    }

    public void SaveSession(double windowX, double windowY, double windowWidth, double windowHeight, bool isMaximized)
    {
        var workspaces = Workspaces.Select(w => w.Workspace).ToList();
        var state = SessionPersistenceService.BuildState(
            workspaces,
            SelectedWorkspace != null ? Workspaces.IndexOf(SelectedWorkspace) : null,
            windowX, windowY, windowWidth, windowHeight,
            isMaximized, SidebarWidth, SidebarVisible);
        SessionPersistenceService.Save(state);
    }

    private void RestoreSession(SessionState session)
    {
        foreach (var wsState in session.Workspaces)
        {
            var workspace = new Workspace
            {
                Id = wsState.Id,
                Name = wsState.Name,
                WorkingDirectory = wsState.WorkingDirectory,
            };

            foreach (var surfState in wsState.Surfaces)
            {
                var surface = new Surface
                {
                    Id = surfState.Id,
                    Name = surfState.Name,
                    FocusedPaneId = surfState.FocusedPaneId,
                };

                if (surfState.RootNode != null)
                {
                    surface.RootSplitNode = SessionPersistenceService.DeserializeSplitNode(surfState.RootNode);
                }

                workspace.Surfaces.Add(surface);
            }

            if (wsState.SelectedSurfaceIndex.HasValue &&
                wsState.SelectedSurfaceIndex.Value >= 0 &&
                wsState.SelectedSurfaceIndex.Value < workspace.Surfaces.Count)
            {
                workspace.SelectedSurface = workspace.Surfaces[wsState.SelectedSurfaceIndex.Value];
            }
            else if (workspace.Surfaces.Count > 0)
            {
                workspace.SelectedSurface = workspace.Surfaces[0];
            }

            var vm = new WorkspaceViewModel(workspace, _notificationService);
            Workspaces.Add(vm);
        }

        if (session.SelectedWorkspaceIndex.HasValue &&
            session.SelectedWorkspaceIndex.Value >= 0 &&
            session.SelectedWorkspaceIndex.Value < Workspaces.Count)
        {
            SelectedWorkspace = Workspaces[session.SelectedWorkspaceIndex.Value];
        }
        else if (Workspaces.Count > 0)
        {
            SelectedWorkspace = Workspaces[0];
        }

        if (session.Window != null)
        {
            SidebarWidth = session.Window.SidebarWidth;
            SidebarVisible = session.Window.SidebarVisible;
        }
    }

    private async Task<string> HandlePipeCommand(string command, Dictionary<string, string> args)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            return command switch
            {
                "NOTIFY" => HandleNotifyCommand(args),
                "WORKSPACE.LIST" => HandleWorkspaceList(),
                "WORKSPACE.CREATE" => HandleWorkspaceCreate(args),
                "WORKSPACE.SELECT" => HandleWorkspaceSelect(args),
                "SURFACE.CREATE" => HandleSurfaceCreate(args),
                "SPLIT.RIGHT" => HandleSplit(SplitDirection.Vertical),
                "SPLIT.DOWN" => HandleSplit(SplitDirection.Horizontal),
                "STATUS" => HandleStatus(),
                _ => JsonSerializer.Serialize(new { error = $"Unknown command: {command}" }),
            };
        });
    }

    private string HandleNotifyCommand(Dictionary<string, string> args)
    {
        var title = args.GetValueOrDefault("title", "Terminal");
        var body = args.GetValueOrDefault("body", "");
        var subtitle = args.GetValueOrDefault("subtitle");
        var workspaceId = SelectedWorkspace?.Workspace.Id ?? "";
        var surfaceId = SelectedWorkspace?.SelectedSurface?.Surface.Id ?? "";

        _notificationService.AddNotification(
            workspaceId, surfaceId, null,
            title, subtitle, body,
            NotificationSource.Cli);

        return JsonSerializer.Serialize(new { ok = true });
    }

    private string HandleWorkspaceList()
    {
        var list = Workspaces.Select(w => new
        {
            id = w.Workspace.Id,
            name = w.Workspace.Name,
            selected = w == SelectedWorkspace,
            surfaces = w.Surfaces.Count,
        });
        return JsonSerializer.Serialize(list);
    }

    private string HandleWorkspaceCreate(Dictionary<string, string> args)
    {
        CreateNewWorkspace();
        var ws = Workspaces[^1];
        if (args.TryGetValue("name", out var name))
            ws.Name = name;
        return JsonSerializer.Serialize(new { id = ws.Workspace.Id, name = ws.Name });
    }

    private string HandleWorkspaceSelect(Dictionary<string, string> args)
    {
        if (args.TryGetValue("index", out var indexStr) && int.TryParse(indexStr, out int index))
        {
            SelectWorkspace(index);
            return JsonSerializer.Serialize(new { ok = true });
        }
        if (args.TryGetValue("id", out var id))
        {
            var ws = Workspaces.FirstOrDefault(w => w.Workspace.Id == id);
            if (ws != null)
            {
                SelectedWorkspace = ws;
                return JsonSerializer.Serialize(new { ok = true });
            }
        }
        return JsonSerializer.Serialize(new { error = "Workspace not found" });
    }

    private string HandleSurfaceCreate(Dictionary<string, string> args)
    {
        SelectedWorkspace?.CreateNewSurface();
        return JsonSerializer.Serialize(new { ok = true });
    }

    private string HandleSplit(SplitDirection direction)
    {
        SelectedWorkspace?.SelectedSurface?.SplitFocused(direction);
        return JsonSerializer.Serialize(new { ok = true });
    }

    private string HandleStatus()
    {
        return JsonSerializer.Serialize(new
        {
            version = "0.1.0",
            workspaces = Workspaces.Count,
            selectedWorkspace = SelectedWorkspace?.Workspace.Id,
            unreadNotifications = TotalUnreadCount,
        });
    }
}
