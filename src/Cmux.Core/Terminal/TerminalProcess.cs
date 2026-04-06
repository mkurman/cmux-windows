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
    private readonly bool _processCreated;
    private IntPtr _attributeList;
    private IntPtr _cancelEvent;
    private bool _disposed;
    private readonly Thread _waitThread;

    public int ProcessId => _processCreated ? _processInfo.dwProcessId : 0;
    public IntPtr ProcessHandle => _processCreated ? _processInfo.hProcess : IntPtr.Zero;

    public event Action? Exited;

    public TerminalProcess(PseudoConsole console, string? command = null, string? workingDirectory = null)
    {
        var shellCommand = command ?? DetectShell();

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
            // Clean up attribute list before throwing — _processInfo is uninitialized
            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process with ConPTY.");
        }

        _processCreated = true;

        // Create a manual-reset event for signaling the wait thread to stop
        _cancelEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, IntPtr.Zero);
        if (_cancelEvent == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEventW failed.");

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

    private void WaitForExitThread()
    {
        var handles = new[] { _processInfo.hProcess, _cancelEvent };
        uint result = WaitForMultipleObjects(2, handles, bWaitAll: false, INFINITE);
        // WAIT_OBJECT_0 means process exited; WAIT_OBJECT_0+1 means cancel event signaled
        if (result == WAIT_OBJECT_0)
        {
            Exited?.Invoke();
        }
        else if (result == 0xFFFFFFFF) // WAIT_FAILED
        {
            // Invalid handle or other failure — treat as exited to avoid silent hang
            Exited?.Invoke();
        }
    }

    public void WaitForExit()
    {
        WaitForSingleObject(_processInfo.hProcess, INFINITE);
    }

    public bool HasExited
    {
        get
        {
            if (!_processCreated) return true;
            if (!GetExitCodeProcess(_processInfo.hProcess, out uint exitCode))
                return true;
            return exitCode != STILL_ACTIVE;
        }
    }

    public void Kill()
    {
        if (!_disposed && _processCreated && !HasExited)
        {
            TerminateProcess(_processInfo.hProcess, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Kill();

        // Signal the cancel event so the wait thread wakes up, then join it
        if (_cancelEvent != IntPtr.Zero)
            SetEvent(_cancelEvent);
        _waitThread.Join(TimeSpan.FromSeconds(3));

        if (_processInfo.hProcess != IntPtr.Zero)
            CloseHandle(_processInfo.hProcess);
        if (_processInfo.hThread != IntPtr.Zero)
            CloseHandle(_processInfo.hThread);
        if (_cancelEvent != IntPtr.Zero)
        {
            CloseHandle(_cancelEvent);
            _cancelEvent = IntPtr.Zero;
        }

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }
}
