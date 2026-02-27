namespace Cmux.Core.Models;

public class TerminalTranscriptEntry
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime CapturedAt { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string SurfaceId { get; init; } = string.Empty;
    public string PaneId { get; init; } = string.Empty;
    public string? WorkingDirectory { get; init; }
    public string Reason { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}
