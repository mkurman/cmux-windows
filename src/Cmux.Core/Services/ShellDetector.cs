using System.Diagnostics;

namespace Cmux.Core.Services;

public record ShellInfo(string Name, string Path);

public static class ShellDetector
{
    public static List<ShellInfo> DetectShells()
    {
        var shells = new List<ShellInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                if (!File.Exists(full)) return;
                if (!seen.Add(full)) return;
                shells.Add(new ShellInfo(name, full));
            }
            catch { /* ignore */ }
        }

        // PowerShell 7+ (pwsh) — preferred. Check every common install location.
        foreach (var (label, path) in EnumeratePwshCandidates())
            Add(label, path);

        // Windows PowerShell (legacy, ships with Windows)
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Add("Windows PowerShell", System.IO.Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"));
            Add("Command Prompt", System.IO.Path.Combine(system32, "cmd.exe"));
            Add("WSL", System.IO.Path.Combine(system32, "wsl.exe"));
        }
        catch { /* ignore */ }

        // Git Bash
        try
        {
            string[] gitBashPaths =
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            };
            foreach (var path in gitBashPaths)
            {
                if (File.Exists(path))
                {
                    Add("Git Bash", path);
                    break;
                }
            }
        }
        catch { /* ignore */ }

        return shells;
    }

    private static IEnumerable<(string Label, string Path)> EnumeratePwshCandidates()
    {
        // 1. Standard MSI installs under Program Files / Program Files (x86)
        //    (each version gets its own folder: 7, 7-preview, etc.)
        string[] roots =
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell"),
        };

        var found = new List<(string Label, string Path)>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var pwsh = System.IO.Path.Combine(dir, "pwsh.exe");
                    if (!File.Exists(pwsh)) continue;
                    var folder = System.IO.Path.GetFileName(dir); // e.g. "7", "7-preview"
                    var label = LabelFor(pwsh, folder);
                    found.Add((label, pwsh));
                }
            }
            catch { /* ignore */ }
        }

        // Sort: stable versions before previews, newer before older.
        found.Sort((a, b) =>
        {
            bool aPrev = a.Path.Contains("preview", StringComparison.OrdinalIgnoreCase);
            bool bPrev = b.Path.Contains("preview", StringComparison.OrdinalIgnoreCase);
            if (aPrev != bPrev) return aPrev ? 1 : -1;
            return string.Compare(b.Path, a.Path, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var item in found) yield return item;

        // 2. Microsoft Store install (App Execution Alias)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] aliasPaths =
        {
            System.IO.Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"),
            System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "pwsh.exe"),
        };
        foreach (var p in aliasPaths)
        {
            if (File.Exists(p))
                yield return ("PowerShell 7", p);
        }

        // 3. PATH lookup (covers dotnet-tool installs, custom locations)
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = System.IO.Path.Combine(dir, "pwsh.exe"); }
            catch { continue; }
            if (File.Exists(candidate))
                yield return ("PowerShell 7", candidate);
        }
    }

    private static string LabelFor(string pwshPath, string folderHint)
    {
        // Prefer FileVersionInfo when readable; fall back to the folder name.
        try
        {
            var v = FileVersionInfo.GetVersionInfo(pwshPath);
            if (!string.IsNullOrEmpty(v.ProductVersion))
            {
                var ver = v.ProductVersion!.Split('-', '+', ' ')[0]; // strip pre-release/build metadata
                var preview = v.ProductVersion.Contains("preview", StringComparison.OrdinalIgnoreCase)
                    || folderHint.Contains("preview", StringComparison.OrdinalIgnoreCase);
                return preview ? $"PowerShell {ver} (preview)" : $"PowerShell {ver}";
            }
        }
        catch { /* ignore */ }

        if (folderHint.Contains("preview", StringComparison.OrdinalIgnoreCase))
            return $"PowerShell {folderHint}";
        return string.IsNullOrEmpty(folderHint) ? "PowerShell 7" : $"PowerShell {folderHint}";
    }
}
