namespace Cmux.Core.Models;

public enum NotificationSource
{
    Osc9,
    Osc99,
    Osc777,
    Cli,
}

public record TerminalNotification
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string WorkspaceId { get; init; }
    public required string SurfaceId { get; init; }
    public string? PaneId { get; init; }
    public bool IsRead { get; set; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public required string Body { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public NotificationSource Source { get; init; }
}

public record AppNotification
{
    public required string WorkspaceName { get; init; }
    public required string SurfaceName { get; init; }
    public required TerminalNotification Notification { get; init; }
}
