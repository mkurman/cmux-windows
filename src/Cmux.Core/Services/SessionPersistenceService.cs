using System.Text.Json;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Saves and restores the application session state (window layout, workspaces,
/// surfaces, split pane layout, working directories) to/from a JSON file.
/// </summary>
public class SessionPersistenceService
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");

    private static readonly string StatePath = Path.Combine(StateDir, "session.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            // Write to temp file then rename for atomicity
            var tempPath = StatePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StatePath, overwrite: true);
        }
        catch
        {
            // Best effort — don't crash on save failure
        }
    }

    public static SessionState BuildState(
        IReadOnlyList<Workspace> workspaces,
        int? selectedWorkspaceIndex,
        double windowX, double windowY, double windowWidth, double windowHeight,
        bool isMaximized, double sidebarWidth, bool sidebarVisible)
    {
        var state = new SessionState
        {
            Version = 1,
            SelectedWorkspaceIndex = selectedWorkspaceIndex,
            Window = new WindowState
            {
                X = windowX,
                Y = windowY,
                Width = windowWidth,
                Height = windowHeight,
                IsMaximized = isMaximized,
                SidebarWidth = sidebarWidth,
                SidebarVisible = sidebarVisible,
            },
        };

        foreach (var ws in workspaces)
        {
            var wsState = new WorkspaceState
            {
                Id = ws.Id,
                Name = ws.Name,
                WorkingDirectory = ws.WorkingDirectory,
                SelectedSurfaceIndex = ws.Surfaces.IndexOf(ws.SelectedSurface!),
            };

            foreach (var surface in ws.Surfaces)
            {
                var surfState = new SurfaceState
                {
                    Id = surface.Id,
                    Name = surface.Name,
                    FocusedPaneId = surface.FocusedPaneId,
                    RootNode = SerializeSplitNode(surface.RootSplitNode),
                };
                wsState.Surfaces.Add(surfState);
            }

            state.Workspaces.Add(wsState);
        }

        return state;
    }

    private static SplitNodeState SerializeSplitNode(SplitNode node)
    {
        return new SplitNodeState
        {
            IsLeaf = node.IsLeaf,
            Direction = node.Direction.ToString(),
            SplitRatio = node.SplitRatio,
            PaneId = node.PaneId,
            First = node.First != null ? SerializeSplitNode(node.First) : null,
            Second = node.Second != null ? SerializeSplitNode(node.Second) : null,
        };
    }

    public static SplitNode DeserializeSplitNode(SplitNodeState state)
    {
        var node = new SplitNode
        {
            IsLeaf = state.IsLeaf,
            Direction = Enum.TryParse<SplitDirection>(state.Direction, out var dir) ? dir : SplitDirection.Vertical,
            SplitRatio = state.SplitRatio,
            PaneId = state.PaneId,
            First = state.First != null ? DeserializeSplitNode(state.First) : null,
            Second = state.Second != null ? DeserializeSplitNode(state.Second) : null,
        };
        return node;
    }
}
