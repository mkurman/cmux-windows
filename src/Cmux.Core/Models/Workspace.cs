using System.Collections.ObjectModel;

namespace Cmux.Core.Models;

public class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Workspace";
    public string IconGlyph { get; set; } = "\uE8A5";
    public string AccentColor { get; set; } = "#FF818CF8";
    public ObservableCollection<Surface> Surfaces { get; set; } = [];
    public Surface? SelectedSurface { get; set; }
    public string? GitBranch { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? LinkedPrStatus { get; set; }
    public string? LinkedPrNumber { get; set; }
    public List<int> ListeningPorts { get; set; } = [];
    public string? LatestNotificationText { get; set; }
    public int UnreadNotificationCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
