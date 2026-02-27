using System.Text.Json;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Manages command snippets with JSON persistence to <c>%LOCALAPPDATA%/cmux/snippets.json</c>.
/// Seeds useful defaults on first use.
/// </summary>
public class SnippetService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");

    private static readonly string FilePath = Path.Combine(DataDir, "snippets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<Snippet> _snippets = [];
    private readonly object _lock = new();

    public IReadOnlyList<Snippet> All
    {
        get { lock (_lock) return _snippets.ToList().AsReadOnly(); }
    }

    public event Action? SnippetsChanged;

    public SnippetService()
    {
        Load();
    }

    public void Add(Snippet snippet)
    {
        lock (_lock)
        {
            _snippets.Add(snippet);
        }
        Save();
        SnippetsChanged?.Invoke();
    }

    public void Update(Snippet snippet)
    {
        lock (_lock)
        {
            var index = _snippets.FindIndex(s => s.Id == snippet.Id);
            if (index < 0) return;

            snippet.UpdatedAt = DateTime.UtcNow;
            _snippets[index] = snippet;
        }
        Save();
        SnippetsChanged?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _snippets.RemoveAll(s => s.Id == id);
        }
        Save();
        SnippetsChanged?.Invoke();
    }

    public void IncrementUseCount(string id)
    {
        lock (_lock)
        {
            var snippet = _snippets.Find(s => s.Id == id);
            if (snippet is null) return;

            snippet.UseCount++;
            snippet.UpdatedAt = DateTime.UtcNow;
        }
        Save();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Searches snippets by name, content, category, tags, and description.
    /// Returns results sorted by favorite status (descending) then use count (descending).
    /// </summary>
    public List<Snippet> Search(string query)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _snippets
                    .OrderByDescending(s => s.IsFavorite)
                    .ThenByDescending(s => s.UseCount)
                    .ToList();
            }

            var q = query.Trim();
            return _snippets
                .Where(s =>
                    s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (s.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(s => s.IsFavorite)
                .ThenByDescending(s => s.UseCount)
                .ToList();
        }
    }

    /// <summary>
    /// Returns distinct category names, sorted alphabetically.
    /// </summary>
    public List<string> GetCategories()
    {
        lock (_lock)
        {
            return _snippets
                .Select(s => s.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                SeedDefaults();
                Save();
                return;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<Snippet>>(json, JsonOptions);
            if (loaded is { Count: > 0 })
            {
                _snippets.AddRange(loaded);
            }
            else
            {
                SeedDefaults();
                Save();
            }
        }
        catch
        {
            _snippets.Clear();
            SeedDefaults();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(_snippets, JsonOptions);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            // Best effort — don't crash on save failure
        }
    }

    private void SeedDefaults()
    {
        _snippets.AddRange(
        [
            new Snippet
            {
                Name = "Git Status",
                Content = "git status -sb",
                Category = "Git",
                Tags = ["git", "status"],
                Description = "Show short branch and tracking info",
                IsFavorite = true,
            },
            new Snippet
            {
                Name = "Git Log Pretty",
                Content = "git log --oneline --graph --decorate -20",
                Category = "Git",
                Tags = ["git", "log", "history"],
                Description = "Compact graph log of recent commits",
                IsFavorite = true,
            },
            new Snippet
            {
                Name = "Git Diff Staged",
                Content = "git diff --cached --stat",
                Category = "Git",
                Tags = ["git", "diff", "staged"],
                Description = "Show staged changes summary",
            },
            new Snippet
            {
                Name = "Docker PS",
                Content = "docker ps --format \"table {{.ID}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}\"",
                Category = "Docker",
                Tags = ["docker", "containers"],
                Description = "List running containers in a formatted table",
            },
            new Snippet
            {
                Name = "Find Large Files",
                Content = "find . -type f -size +{{size}} -exec ls -lh {} + | sort -k5 -hr",
                Category = "Files",
                Tags = ["find", "disk", "large"],
                Description = "Find files larger than the specified size (e.g. 100M)",
            },
            new Snippet
            {
                Name = "Kill Port",
                Content = "npx kill-port {{port}}",
                Category = "Network",
                Tags = ["port", "kill", "process"],
                Description = "Kill the process occupying a given port",
            },
            new Snippet
            {
                Name = "SSH Connect",
                Content = "ssh {{user}}@{{host}}",
                Category = "Network",
                Tags = ["ssh", "remote"],
                Description = "Connect to a remote host via SSH",
            },
            new Snippet
            {
                Name = "Disk Usage",
                Content = "Get-ChildItem -Recurse | Measure-Object -Property Length -Sum | Select-Object @{N='SizeMB';E={[math]::Round($_.Sum/1MB,2)}}",
                Category = "PowerShell",
                Tags = ["disk", "powershell", "size"],
                Description = "Calculate total size of files in current directory",
            },
            new Snippet
            {
                Name = "Process by Name",
                Content = "Get-Process -Name {{name}} | Format-Table Id, CPU, WorkingSet64, ProcessName -AutoSize",
                Category = "PowerShell",
                Tags = ["process", "powershell"],
                Description = "List processes matching a given name with CPU and memory info",
            },
            new Snippet
            {
                Name = "Watch Directory",
                Content = "watch -n 1 'ls -la {{path}}'",
                Category = "Files",
                Tags = ["watch", "monitor", "directory"],
                Description = "Continuously monitor directory contents",
            },
        ]);
    }
}
