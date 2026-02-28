namespace Cmux.Core.Models;

public class AgentConversationThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; set; } = "";
    public string SurfaceId { get; set; } = "";
    public string PaneId { get; set; } = "";
    public string AgentName { get; set; } = "assistant";
    public string Title { get; set; } = "Conversation";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int MessageCount { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public int CompactionCount { get; set; }
    public string LastMessagePreview { get; set; } = "";
}
