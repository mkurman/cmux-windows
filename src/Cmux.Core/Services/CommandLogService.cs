using System.Text.Json;
using System.Text.RegularExpressions;
using Cmux.Core.Config;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Tracks shell commands via OSC 133 prompt markers and maintains a searchable log.
/// Completed commands are persisted to daily JSONL files under %LOCALAPPDATA%/cmux/logs.
/// </summary>
public class CommandLogService
{
    private const int MaxEntries = 5000;

    private static readonly string LogsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "logs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly List<CommandLogEntry> _log = [];
    private readonly object _lock = new();

    /// <summary>
    /// The currently active (unfinished) command per pane.
    /// </summary>
    private readonly Dictionary<string, CommandLogEntry> _activeCommands = [];

    private DateOnly? _lastRetentionSweepDate;
    private DateOnly? _lastTranscriptRetentionSweepDate;

    private static readonly Regex SecretEnvAssignmentRegex = new(
        @"(\b[A-Za-z0-9_]*(?:PASSWORD|PASSWD|TOKEN|SECRET|API_KEY|ACCESS_KEY)[A-Za-z0-9_]*\s*=\s*)(\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecretFlagRegex = new(
        @"(\-\-(?:password|passwd|pwd|token|secret|api[-_]?key|access[-_]?key)(?:\s+|=)|\-(?:password|passwd|pwd|token|secret)(?:\s+|=))(\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UriCredentialsRegex = new(
        @"([a-z][a-z0-9+\-.]*://[^\s/@:]+:)([^@\s]+)(@)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<CommandLogEntry> Log
    {
        get
        {
            lock (_lock)
                return _log.ToList().AsReadOnly();
        }
    }

    public event Action? LogChanged;

    public CommandLogService()
    {
        ApplyRetentionPolicy();
        ApplyTranscriptRetentionPolicy();
        ScrubSensitiveDataInLogFiles();
        ScrubSensitiveDataInTranscriptFiles();
        LoadRecentFromDisk(days: GetLoadWindowDays());

        SettingsService.SettingsChanged += () =>
        {
            ApplyRetentionPolicy();
            ApplyTranscriptRetentionPolicy();
        };
    }

    public string GetLogsDirectoryPath()
    {
        Directory.CreateDirectory(LogsDir);
        return LogsDir;
    }

    public string GetTerminalTranscriptsDirectoryPath()
    {
        var dir = Path.Combine(GetLogsDirectoryPath(), "terminal");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string? SaveTerminalTranscript(string workspaceId, string surfaceId, string paneId, string? workingDirectory, string? text, string reason)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            MaybeApplyTranscriptRetentionPolicy();

            var sanitizedText = SanitizeTranscriptText(text);
            if (string.IsNullOrWhiteSpace(sanitizedText))
                return null;

            var root = GetTerminalTranscriptsDirectoryPath();
            var dayDir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayDir);

            var ts = DateTime.Now;
            var safeReason = SanitizeFileNameSegment(reason);
            var fileName = $"{ts:HHmmss}_{safeReason}_{ShortId(workspaceId)}_{ShortId(surfaceId)}_{ShortId(paneId)}.log";
            var filePath = Path.Combine(dayDir, fileName);

            var header = string.Join(Environment.NewLine,
                "# cmux terminal transcript",
                $"# captured-at: {ts:O}",
                $"# workspace-id: {workspaceId}",
                $"# surface-id: {surfaceId}",
                $"# pane-id: {paneId}",
                $"# reason: {reason}",
                $"# working-directory: {(string.IsNullOrWhiteSpace(workingDirectory) ? "-" : workingDirectory)}",
                "");

            File.WriteAllText(filePath, header + Environment.NewLine + sanitizedText);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<TerminalTranscriptEntry> GetTerminalTranscripts(int maxEntries = 2000)
    {
        try
        {
            MaybeApplyTranscriptRetentionPolicy();
            var root = GetTerminalTranscriptsDirectoryPath();
            var files = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                .Select(path => ParseTranscriptMetadata(path))
                .Where(entry => entry != null)
                .Cast<TerminalTranscriptEntry>()
                .OrderByDescending(entry => entry.CapturedAt)
                .Take(Math.Max(1, maxEntries))
                .ToList();

            return files.AsReadOnly();
        }
        catch
        {
            return [];
        }
    }

    public string LoadTerminalTranscriptContent(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return string.Empty;

        try
        {
            var lines = File.ReadAllLines(filePath);
            int startIndex = 0;

            while (startIndex < lines.Length && lines[startIndex].StartsWith("#", StringComparison.Ordinal))
                startIndex++;

            while (startIndex < lines.Length && string.IsNullOrWhiteSpace(lines[startIndex]))
                startIndex++;

            var content = string.Join(Environment.NewLine, lines.Skip(startIndex));
            return SanitizeTranscriptText(content);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void ScrubSensitiveDataInTranscriptFiles()
    {
        try
        {
            var root = GetTerminalTranscriptsDirectoryPath();
            foreach (var filePath in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
            {
                var original = File.ReadAllText(filePath);
                var sanitized = SanitizeTranscriptText(original);

                if (!string.Equals(original, sanitized, StringComparison.Ordinal))
                    File.WriteAllText(filePath, sanitized);
            }
        }
        catch
        {
            // Best effort scrub.
        }
    }

    public void ScrubSensitiveDataInLogFiles()
    {
        try
        {
            Directory.CreateDirectory(LogsDir);

            foreach (var filePath in Directory.EnumerateFiles(LogsDir, "*.jsonl"))
            {
                bool changed = false;
                var cleaned = new List<string>();

                foreach (var line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<CommandLogEntry>(line, JsonOptions);
                        if (entry == null)
                            continue;

                        var original = entry.Command;
                        entry.Command = SanitizeCommandForStorage(entry.Command);

                        if (string.IsNullOrWhiteSpace(entry.Command))
                        {
                            changed = true;
                            continue;
                        }

                        if (!string.Equals(original, entry.Command, StringComparison.Ordinal))
                            changed = true;

                        cleaned.Add(JsonSerializer.Serialize(entry, JsonOptions));
                    }
                    catch
                    {
                        changed = true;
                    }
                }

                if (changed)
                {
                    var tempPath = filePath + ".tmp";
                    File.WriteAllLines(tempPath, cleaned);
                    File.Move(tempPath, filePath, overwrite: true);
                }
            }
        }
        catch
        {
            // Best effort scrub.
        }
    }

    public void ApplyRetentionPolicy()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogsDir);

                var days = GetRetentionDaysOrNull();
                if (days is null)
                {
                    _lastRetentionSweepDate = DateOnly.FromDateTime(DateTime.Today);
                    return;
                }

                var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-(days.Value - 1)));

                foreach (var filePath in Directory.EnumerateFiles(LogsDir, "*.jsonl"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    if (!DateOnly.TryParse(fileName, out var date))
                        continue;

                    if (date < cutoff)
                        File.Delete(filePath);
                }

                _log.RemoveAll(e => DateOnly.FromDateTime(e.StartedAt.ToLocalTime()) < cutoff);
                _lastRetentionSweepDate = DateOnly.FromDateTime(DateTime.Today);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void ApplyTranscriptRetentionPolicy()
    {
        try
        {
            var days = GetTranscriptRetentionDaysOrNull();
            if (days is null)
            {
                _lastTranscriptRetentionSweepDate = DateOnly.FromDateTime(DateTime.Today);
                return;
            }

            var root = GetTerminalTranscriptsDirectoryPath();
            var cutoffDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-(days.Value - 1)));

            foreach (var filePath in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
            {
                var fileDate = DateOnly.FromDateTime(File.GetLastWriteTime(filePath));
                if (fileDate < cutoffDate)
                    File.Delete(filePath);
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }

            _lastTranscriptRetentionSweepDate = DateOnly.FromDateTime(DateTime.Today);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public IReadOnlyList<DateOnly> GetAvailableDates()
    {
        MaybeApplyRetentionPolicy();
        Directory.CreateDirectory(LogsDir);

        var dates = Directory.EnumerateFiles(LogsDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name) && DateOnly.TryParse(name, out _))
            .Select(name => DateOnly.Parse(name!))
            .OrderByDescending(d => d)
            .ToList();

        return dates.AsReadOnly();
    }

    public IReadOnlyList<CommandLogEntry> GetForDate(DateOnly date)
    {
        MaybeApplyRetentionPolicy();

        var entriesById = new Dictionary<string, CommandLogEntry>(StringComparer.Ordinal);
        var filePath = Path.Combine(GetLogsDirectoryPath(), $"{date:yyyy-MM-dd}.jsonl");

        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<CommandLogEntry>(line, JsonOptions);
                    if (entry == null)
                        continue;

                    entry.Command = SanitizeCommandForStorage(entry.Command);
                    if (string.IsNullOrWhiteSpace(entry.Command))
                        continue;

                    entriesById[entry.Id] = entry;
                }
                catch
                {
                    // Ignore malformed lines
                }
            }
        }

        lock (_lock)
        {
            foreach (var entry in _log.Where(e => DateOnly.FromDateTime(e.StartedAt.ToLocalTime()) == date))
                entriesById[entry.Id] = entry;
        }

        return entriesById.Values
            .OrderByDescending(e => e.StartedAt)
            .ToList()
            .AsReadOnly();
    }

    public string? SanitizeCommandForStorage(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var sanitized = command.Trim();

        // Safety net for manual fallback: password prompts often produce a lone token.
        if (LooksLikeSecretInput(sanitized))
            return null;

        sanitized = SecretEnvAssignmentRegex.Replace(sanitized, "$1[REDACTED]");
        sanitized = SecretFlagRegex.Replace(sanitized, "$1[REDACTED]");
        sanitized = UriCredentialsRegex.Replace(sanitized, "$1[REDACTED]$3");

        if (sanitized.Length > 4096)
            sanitized = sanitized[..4096];

        return sanitized;
    }

    public void RecordManualCommandSubmission(string paneId, string workspaceId, string surfaceId, string command, string? workingDirectory)
    {
        var trimmed = SanitizeCommandForStorage(command);
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        lock (_lock)
        {
            if (_activeCommands.TryGetValue(paneId, out var existingActive))
            {
                if (string.Equals(existingActive.Command, trimmed, StringComparison.Ordinal))
                    return;

                CompleteActiveCommand(paneId, exitCode: null);
            }

            var entry = new CommandLogEntry
            {
                PaneId = paneId,
                SurfaceId = surfaceId,
                WorkspaceId = workspaceId,
                Command = trimmed,
                WorkingDirectory = workingDirectory,
            };

            _log.Insert(0, entry);
            _activeCommands[paneId] = entry;
            TrimLog();
        }

        LogChanged?.Invoke();
    }

    /// <summary>
    /// Handles an OSC 133 prompt marker.
    /// </summary>
    /// <param name="paneId">The pane that emitted the marker.</param>
    /// <param name="workspaceId">The workspace the pane belongs to.</param>
    /// <param name="surfaceId">The surface the pane belongs to.</param>
    /// <param name="marker">One of 'A' (prompt start), 'B' (command start), 'C' (output start), 'D' (command finished).</param>
    /// <param name="payload">Optional payload; for 'B' usually command text, for 'D' exit code.</param>
    /// <param name="workingDirectory">Current working directory at time of marker, if known.</param>
    public void HandlePromptMarker(string paneId, string workspaceId, string surfaceId, char marker, string? payload, string? workingDirectory)
    {
        lock (_lock)
        {
            switch (marker)
            {
                case 'A':
                    CompleteActiveCommand(paneId, exitCode: null);
                    break;

                case 'B':
                    var command = SanitizeCommandForStorage(payload);
                    if (string.IsNullOrWhiteSpace(command))
                        break;

                    if (_activeCommands.TryGetValue(paneId, out var existing) &&
                        string.Equals(existing.Command, command, StringComparison.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(existing.WorkingDirectory))
                            existing.WorkingDirectory = workingDirectory;
                        break;
                    }

                    CompleteActiveCommand(paneId, exitCode: null);
                    var entry = new CommandLogEntry
                    {
                        PaneId = paneId,
                        SurfaceId = surfaceId,
                        WorkspaceId = workspaceId,
                        Command = command,
                        WorkingDirectory = workingDirectory,
                    };
                    _log.Insert(0, entry);
                    _activeCommands[paneId] = entry;
                    TrimLog();
                    break;

                case 'C':
                    // Informational — output has started; no state change needed.
                    break;

                case 'D':
                    int? exitCode = ParseExitCode(payload);
                    CompleteActiveCommand(paneId, exitCode);
                    break;
            }
        }

        LogChanged?.Invoke();
    }

    /// <summary>
    /// Searches log entries where Command or WorkingDirectory contains the query (case-insensitive).
    /// </summary>
    public IReadOnlyList<CommandLogEntry> Search(string query)
    {
        lock (_lock)
        {
            return _log.Where(e =>
                (e.Command is not null && e.Command.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (e.WorkingDirectory is not null && e.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase))
            ).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Returns all log entries for the given pane.
    /// </summary>
    public IReadOnlyList<CommandLogEntry> GetForPane(string paneId)
    {
        lock (_lock)
        {
            return _log.Where(e => e.PaneId == paneId).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Clears only in-memory log and active command tracking.
    /// Files on disk are left intact.
    /// </summary>
    public void ClearInMemory()
    {
        lock (_lock)
        {
            _log.Clear();
            _activeCommands.Clear();
        }

        LogChanged?.Invoke();
    }

    private void CompleteActiveCommand(string paneId, int? exitCode)
    {
        if (_activeCommands.TryGetValue(paneId, out var active))
        {
            active.CompletedAt = DateTime.UtcNow;
            if (exitCode.HasValue)
                active.ExitCode = exitCode.Value;

            _activeCommands.Remove(paneId);
            PersistCompletedEntry(active);
        }
    }

    private void PersistCompletedEntry(CommandLogEntry entry)
    {
        try
        {
            MaybeApplyRetentionPolicy();

            Directory.CreateDirectory(LogsDir);
            var date = DateOnly.FromDateTime(entry.StartedAt.ToLocalTime());
            var filePath = Path.Combine(LogsDir, $"{date:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
        catch
        {
            // Best effort persistence
        }
    }

    private void LoadRecentFromDisk(int days)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var minDate = DateOnly.FromDateTime(DateTime.Now.AddDays(-Math.Abs(days)));

            var files = Directory.EnumerateFiles(LogsDir, "*.jsonl")
                .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
                .Select(x => new { x.Path, Date = DateOnly.TryParse(x.Name, out var d) ? d : (DateOnly?)null })
                .Where(x => x.Date.HasValue && x.Date.Value >= minDate)
                .OrderByDescending(x => x.Date)
                .Select(x => x.Path)
                .ToList();

            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CommandLogEntry>(line, JsonOptions);
                        if (entry == null)
                            continue;

                        entry.Command = SanitizeCommandForStorage(entry.Command);
                        if (string.IsNullOrWhiteSpace(entry.Command))
                            continue;

                        _log.Add(entry);
                    }
                    catch
                    {
                        // Ignore malformed line
                    }
                }
            }

            _log.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            TrimLog();
        }
        catch
        {
            // Best effort load
        }
    }

    private void TrimLog()
    {
        while (_log.Count > MaxEntries)
            _log.RemoveAt(_log.Count - 1);
    }

    private void MaybeApplyRetentionPolicy()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_lastRetentionSweepDate == today)
            return;

        ApplyRetentionPolicy();
    }

    private void MaybeApplyTranscriptRetentionPolicy()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_lastTranscriptRetentionSweepDate == today)
            return;

        ApplyTranscriptRetentionPolicy();
    }

    private static int? GetRetentionDaysOrNull()
    {
        var configured = SettingsService.Current.CommandLogRetentionDays;

        if (configured < 0)
            configured = 90;

        if (configured == 0)
            return null;

        return Math.Clamp(configured, 1, 3650);
    }

    private static int? GetTranscriptRetentionDaysOrNull()
    {
        var configured = SettingsService.Current.TranscriptRetentionDays;

        if (configured < 0)
            configured = 90;

        if (configured == 0)
            return null;

        return Math.Clamp(configured, 1, 3650);
    }

    private static int GetLoadWindowDays()
    {
        return GetRetentionDaysOrNull() ?? 3650;
    }

    private static TerminalTranscriptEntry? ParseTranscriptMetadata(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var capturedAt = fileInfo.LastWriteTime;
            string workspaceId = string.Empty;
            string surfaceId = string.Empty;
            string paneId = string.Empty;
            string reason = string.Empty;
            string? workingDirectory = null;

            foreach (var line in File.ReadLines(filePath).Take(16))
            {
                if (!line.StartsWith("#", StringComparison.Ordinal))
                    break;

                var payload = line.TrimStart('#').Trim();
                int colon = payload.IndexOf(':');
                if (colon <= 0)
                    continue;

                var key = payload[..colon].Trim();
                var value = payload[(colon + 1)..].Trim();

                switch (key)
                {
                    case "captured-at":
                        if (DateTime.TryParse(value, out var parsedAt))
                            capturedAt = parsedAt;
                        break;
                    case "workspace-id":
                        workspaceId = value;
                        break;
                    case "surface-id":
                        surfaceId = value;
                        break;
                    case "pane-id":
                        paneId = value;
                        break;
                    case "reason":
                        reason = value;
                        break;
                    case "working-directory":
                        workingDirectory = value == "-" ? null : value;
                        break;
                }
            }

            return new TerminalTranscriptEntry
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                CapturedAt = capturedAt,
                WorkspaceId = workspaceId,
                SurfaceId = surfaceId,
                PaneId = paneId,
                WorkingDirectory = workingDirectory,
                Reason = string.IsNullOrWhiteSpace(reason) ? "snapshot" : reason,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            };
        }
        catch
        {
            return null;
        }
    }

    private string SanitizeTranscriptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sanitized = SecretEnvAssignmentRegex.Replace(text, "$1[REDACTED]");
        sanitized = SecretFlagRegex.Replace(sanitized, "$1[REDACTED]");
        sanitized = UriCredentialsRegex.Replace(sanitized, "$1[REDACTED]$3");

        return sanitized;
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "na";

        var cleaned = value.Trim();
        return cleaned.Length <= 8 ? cleaned : cleaned[..8];
    }

    private static string SanitizeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "snapshot";

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var normalized = new string(chars).Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            return "snapshot";

        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

        return normalized;
    }

    private static bool LooksLikeSecretInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Contains(' '))
            return false;

        if (value.Contains('/') || value.Contains('\\') || value.StartsWith(".", StringComparison.Ordinal))
            return false;

        var lower = value.ToLowerInvariant();
        if (lower is "ls" or "cd" or "pwd" or "git" or "npm" or "pnpm" or "yarn" or "dotnet" or "python" or "python3" or "node" or "bash" or "zsh" or "fish" or "vi" or "vim" or "nano" or "code" or "cargo" or "go" or "java" or "kubectl" or "docker")
            return false;

        if (lower.Contains("password") || lower.Contains("passwd") || lower.Contains("token") || lower.Contains("secret"))
            return true;

        bool hasLetter = value.Any(char.IsLetter);
        bool hasDigit = value.Any(char.IsDigit);
        bool hasSpecial = value.Any(ch => !char.IsLetterOrDigit(ch));

        return value.Length >= 6 && hasLetter && (hasDigit || hasSpecial);
    }

    private static int? ParseExitCode(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        var text = payload.Trim();

        if (int.TryParse(text, out var directCode))
            return directCode;

        var semicolonIndex = text.LastIndexOf(';');
        if (semicolonIndex >= 0)
        {
            var codeStr = text[(semicolonIndex + 1)..].Trim();
            if (int.TryParse(codeStr, out var code))
                return code;
        }

        return null;
    }
}
