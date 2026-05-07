using System.ComponentModel;
using System.Runtime.InteropServices;
using static Cmux.Core.Terminal.ConPtyInterop;

namespace Cmux.Core.Terminal;

/// <summary>
/// Manages a shell process attached to a ConPTY pseudo console.
/// </summary>
public sealed class TerminalProcess : IDisposable
{
    private readonly PROCESS_INFORMATION _processInfo;
    private IntPtr _attributeList;
    private bool _disposed;
    private readonly Thread _waitThread;

    public int ProcessId => _processInfo.dwProcessId;
    public IntPtr ProcessHandle => _processInfo.hProcess;

    public event Action? Exited;

    public TerminalProcess(PseudoConsole console, string? command = null, string? workingDirectory = null)
    {
        var shellCommand = command ?? DetectShell();
        shellCommand = MaybeInjectPwshOsc7(shellCommand);
        IPC.DaemonClient.LogDaemon($"[TerminalProcess] Creating: shell=\"{shellCommand}\" cwd=\"{workingDirectory}\"");

        // Initialize thread attribute list for ConPTY
        _attributeList = CreateAttributeList(console.Handle);

        // Create process with ConPTY
        var startupInfo = new STARTUPINFOEX
        {
            lpAttributeList = _attributeList,
        };
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        bool success = CreateProcess(
            null,
            shellCommand,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            IntPtr.Zero,
            workingDirectory,
            ref startupInfo,
            out _processInfo);

        if (!success)
        {
            var err = Marshal.GetLastWin32Error();
            IPC.DaemonClient.LogDaemon($"[TerminalProcess] CreateProcess FAILED: error={err}");
            throw new Win32Exception(err, "Failed to create process with ConPTY.");
        }

        IPC.DaemonClient.LogDaemon($"[TerminalProcess] Created: PID={_processInfo.dwProcessId}");

        // Start a background thread to wait for process exit
        _waitThread = new Thread(WaitForExitThread)
        {
            IsBackground = true,
            Name = $"ConPTY-Wait-{_processInfo.dwProcessId}",
        };
        _waitThread.Start();
    }

    /// <summary>
    /// Detects the best available shell on the system.
    /// Priority: pwsh.exe > powershell.exe > cmd.exe
    /// </summary>
    private static string DetectShell()
    {
        // Check for PowerShell 7+ (pwsh)
        var pwshPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            "pwsh.exe",
        };

        foreach (var path in pwshPaths)
        {
            if (path == "pwsh.exe")
            {
                // Check if it's in PATH
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
            else if (File.Exists(path))
            {
                return path;
            }
        }

        // Fall back to Windows PowerShell
        var winPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPowerShell))
            return winPowerShell;

        // Last resort: cmd.exe from COMSPEC
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrEmpty(comspec) && File.Exists(comspec))
            return comspec;

        return "cmd.exe";
    }

    /// <summary>
    /// PowerShell does not propagate `cd` to the OS-level current directory
    /// (its <c>$PWD</c> is a provider-aware location stack, not <c>SetCurrentDirectory</c>),
    /// so reading the shell's PEB always returns the launch dir. To make cwd
    /// tracking actually work for pwsh/powershell, we launch them with a small
    /// startup script that wraps the user's prompt to emit OSC 7 each redraw.
    /// <see cref="OscHandler"/> already understands OSC 7 and feeds it back into
    /// <see cref="TerminalSession.WorkingDirectory"/>, so persistence and "open
    /// new tab in current dir" both start working without any other changes.
    /// </summary>
    private static string MaybeInjectPwshOsc7(string shellCommand)
    {
        // Only wrap when the entire command is a bare shell path. If the user
        // supplied their own arguments, respect them and skip injection.
        var trimmed = shellCommand.Trim();
        var unquoted = trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2
            ? trimmed[1..^1]
            : trimmed;

        if (unquoted.Contains(' ') && !File.Exists(unquoted))
            return shellCommand; // looks like it has args — leave it alone

        var leaf = Path.GetFileName(unquoted);
        bool isPwshFamily = string.Equals(leaf, "pwsh.exe", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(leaf, "powershell.exe", StringComparison.OrdinalIgnoreCase);
        if (!isPwshFamily) return shellCommand;

        // Wrap the user's existing prompt so their customizations still run.
        // Filesystem-only OSC 7: skip when CurrentLocation is on a non-FileSystem
        // provider (Registry, Cert, etc.) where a path wouldn't make sense.
        const string injection = """
$global:__cmuxOldPrompt = $function:prompt
function global:prompt {
    try {
        $loc = $ExecutionContext.SessionState.Path.CurrentLocation
        if ($loc.Provider.Name -eq 'FileSystem') {
            $cwd = $loc.ProviderPath
            [Console]::Out.Write([char]27 + ']7;file:///' + $cwd.Replace('\','/') + [char]7)
        }
    } catch { }
    if ($global:__cmuxOldPrompt) { & $global:__cmuxOldPrompt } else { "PS $($PWD.Path)> " }
}
""";

        var bytes = System.Text.Encoding.Unicode.GetBytes(injection);
        var b64 = Convert.ToBase64String(bytes);

        // Quote the shell path if it contains spaces. -NoExit so the user's profile
        // still loads after our -EncodedCommand runs. -EncodedCommand sidesteps all
        // shell-quoting hazards.
        var quotedShell = unquoted.Contains(' ') ? $"\"{unquoted}\"" : unquoted;
        return $"{quotedShell} -NoExit -EncodedCommand {b64}";
    }

    private static IntPtr CreateAttributeList(IntPtr conPtyHandle)
    {
        // Query the required size
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var attributeList = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(
            attributeList,
            0,
            (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            conPtyHandle,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        return attributeList;
    }

    public int ExitCode
    {
        get
        {
            if (GetExitCodeProcess(_processInfo.hProcess, out uint code) && code != STILL_ACTIVE)
                return (int)code;
            return -1;
        }
    }

    private void WaitForExitThread()
    {
        WaitForSingleObject(_processInfo.hProcess, INFINITE);
        IPC.DaemonClient.LogDaemon($"[TerminalProcess] PID={_processInfo.dwProcessId} exited with code {ExitCode}");
        Exited?.Invoke();
    }

    public void WaitForExit()
    {
        WaitForSingleObject(_processInfo.hProcess, INFINITE);
    }

    public bool HasExited
    {
        get
        {
            if (!GetExitCodeProcess(_processInfo.hProcess, out uint exitCode))
                return true;
            return exitCode != STILL_ACTIVE;
        }
    }

    public void Kill()
    {
        if (!_disposed && !HasExited)
        {
            TerminateProcess(_processInfo.hProcess, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Kill();

        if (_processInfo.hProcess != IntPtr.Zero)
            CloseHandle(_processInfo.hProcess);
        if (_processInfo.hThread != IntPtr.Zero)
            CloseHandle(_processInfo.hThread);

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }
}
