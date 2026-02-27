using Cmux.Core.Terminal;

namespace Cmux.Core.Models;

public class PaneStateSnapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public string? WorkingDirectory { get; set; }
    public List<string> CommandHistory { get; set; } = [];
    public TerminalBufferSnapshot? BufferSnapshot { get; set; }
}
