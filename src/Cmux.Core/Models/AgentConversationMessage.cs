namespace Cmux.Core.Models;

public class AgentConversationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ThreadId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ToolName { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public bool IsCompactionSummary { get; set; }
}
