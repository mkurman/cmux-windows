using System.Diagnostics;

namespace Cmux.Core.Services;

/// <summary>
/// Extracts git information (branch, remote, PR status) for a working directory.
/// </summary>
public static class GitService
{
    /// <summary>
    /// Gets the current git branch name for the given directory.
    /// Returns null if not a git repository.
    /// </summary>
    public static string? GetBranch(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return null;

        // Fast path: read .git/HEAD directly
        var gitHeadPath = FindGitHead(workingDirectory);
        if (gitHeadPath != null && File.Exists(gitHeadPath))
        {
            try
            {
                var content = File.ReadAllText(gitHeadPath).Trim();
                const string refPrefix = "ref: refs/heads/";
                if (content.StartsWith(refPrefix))
                    return content[refPrefix.Length..];

                // Detached HEAD — return short SHA
                if (content.Length >= 7)
                    return content[..7];
            }
            catch
            {
                // Fall through to git command
            }
        }

        return RunGit("rev-parse --abbrev-ref HEAD", workingDirectory);
    }

    /// <summary>
    /// Gets the git remote URL for the given directory.
    /// </summary>
    public static string? GetRemoteUrl(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return null;
        return RunGit("config --get remote.origin.url", workingDirectory);
    }

    private static string? FindGitHead(string directory)
    {
        var current = directory;
        while (current != null)
        {
            var gitDir = Path.Combine(current, ".git");
            if (Directory.Exists(gitDir))
                return Path.Combine(gitDir, "HEAD");

            // Handle .git file (worktrees)
            if (File.Exists(gitDir))
            {
                try
                {
                    var content = File.ReadAllText(gitDir).Trim();
                    if (content.StartsWith("gitdir: "))
                    {
                        var gitDirPath = content["gitdir: ".Length..];
                        if (!Path.IsPathRooted(gitDirPath))
                            gitDirPath = Path.GetFullPath(Path.Combine(current, gitDirPath));
                        return Path.Combine(gitDirPath, "HEAD");
                    }
                }
                catch
                {
                    // Continue
                }
            }

            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static string? RunGit(string arguments, string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);

            return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
