namespace Cmux.Core.Models;

public class CommandLogEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string PaneId { get; init; } = "";
    public string SurfaceId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? Command { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? WorkingDirectory { get; set; }

    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;

    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            if (d is null)
                return "running...";

            var ts = d.Value;
            if (ts.TotalSeconds < 1)
                return $"{(int)ts.TotalMilliseconds}ms";
            if (ts.TotalMinutes < 1)
                return $"{ts.TotalSeconds:F1}s";
            if (ts.TotalHours < 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }
    }

    /// <summary>
    /// Segoe MDL2 Assets glyph for command status.
    /// null exit code = clock, 0 = check, other = error.
    /// </summary>
    public string StatusIcon => ExitCode switch
    {
        null => "\uE916",
        0 => "\uE73E",
        _ => "\uE711",
    };
}
