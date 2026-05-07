using System.Runtime.InteropServices;

namespace Cmux.Core.Terminal;

/// <summary>
/// Reads another process's current working directory by walking its PEB.
/// Used as a fallback when the shell does not emit OSC 7 (cmd, default
/// PowerShell prompt) so we can still capture the live cwd at snapshot time
/// and reopen panes in the same place.
///
/// Only supports 64-bit target processes from a 64-bit caller. cmux ships
/// as win-x64, so this covers every shell we launch.
/// </summary>
internal static class ProcessCwdReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    // Offsets into the 64-bit PEB / RTL_USER_PROCESS_PARAMETERS layout.
    // These are stable across modern Windows 10/11 — Microsoft only tweaks
    // them at the tail of the structures.
    private const int PebOffset_ProcessParameters = 0x20;
    private const int RtlUpp_CurrentDirectory_DosPath_Length = 0x38;
    private const int RtlUpp_CurrentDirectory_DosPath_Buffer = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    /// <summary>
    /// Returns the live current directory of <paramref name="processId"/>, or
    /// null if the call fails for any reason (process exited, access denied,
    /// 32-bit target, layout drift, etc.).
    /// </summary>
    public static string? TryRead(int processId)
    {
        if (processId <= 0) return null;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, processId);
        if (handle == IntPtr.Zero)
        {
            IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] OpenProcess failed: err={Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            var pbi = default(ProcessBasicInformation);
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            {
                IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] NtQueryInformationProcess status=0x{status:X} peb={pbi.PebBaseAddress:X}");
                return null;
            }

            if (!TryReadPointer(handle, pbi.PebBaseAddress + PebOffset_ProcessParameters, out var paramsAddr))
            {
                IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] read PEB.ProcessParameters failed err={Marshal.GetLastWin32Error()}");
                return null;
            }

            if (!TryReadUshort(handle, paramsAddr + RtlUpp_CurrentDirectory_DosPath_Length, out var byteLen))
            {
                IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] read DosPath.Length failed");
                return null;
            }
            if (byteLen == 0 || byteLen > 0x8000)
            {
                IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] DosPath.Length out of range: {byteLen}");
                return null;
            }

            if (!TryReadPointer(handle, paramsAddr + RtlUpp_CurrentDirectory_DosPath_Buffer, out var bufferPtr))
            {
                IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] read DosPath.Buffer failed");
                return null;
            }
            if (bufferPtr == IntPtr.Zero) return null;

            var bytes = new byte[byteLen];
            unsafe
            {
                fixed (byte* p = bytes)
                {
                    if (!ReadProcessMemory(handle, bufferPtr, (IntPtr)p, (IntPtr)byteLen, out var read)
                        || (int)read != byteLen)
                    {
                        IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] ReadProcessMemory(buffer) failed err={Marshal.GetLastWin32Error()} read={(int)read}/{byteLen}");
                        return null;
                    }
                }
            }

            var path = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\\', '\0');
            IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] OK cwd='{path}'");
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            IPC.DaemonClient.LogDaemon($"[ProcessCwdReader:{processId}] Exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadPointer(IntPtr handle, IntPtr address, out IntPtr value)
    {
        Span<byte> buf = stackalloc byte[IntPtr.Size];
        if (!TryReadBytes(handle, address, buf))
        {
            value = IntPtr.Zero;
            return false;
        }
        value = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(buf)
            : (IntPtr)BitConverter.ToInt32(buf);
        return true;
    }

    private static bool TryReadUshort(IntPtr handle, IntPtr address, out ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        if (!TryReadBytes(handle, address, buf))
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToUInt16(buf);
        return true;
    }

    private static bool TryReadBytes(IntPtr handle, IntPtr address, Span<byte> buffer)
    {
        unsafe
        {
            fixed (byte* p = buffer)
            {
                return ReadProcessMemory(handle, address, (IntPtr)p, (IntPtr)buffer.Length, out var read)
                       && (int)read == buffer.Length;
            }
        }
    }
}
