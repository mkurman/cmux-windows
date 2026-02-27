using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Cmux.Core.Terminal.ConPtyInterop;

namespace Cmux.Core.Terminal;

/// <summary>
/// Wraps a Windows Pseudo Console (ConPTY) handle. Provides pipes for
/// reading output from and writing input to the pseudo console.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>Pipe the caller reads from to get terminal output.</summary>
    public SafeFileHandle ReadPipe { get; }

    /// <summary>Pipe the caller writes to in order to send input to the terminal.</summary>
    public SafeFileHandle WritePipe { get; }

    /// <summary>Raw ConPTY handle for use with process creation.</summary>
    public IntPtr Handle => _handle;

    private PseudoConsole(IntPtr handle, SafeFileHandle readPipe, SafeFileHandle writePipe)
    {
        _handle = handle;
        ReadPipe = readPipe;
        WritePipe = writePipe;
    }

    /// <summary>
    /// Creates a new pseudo console with the given dimensions.
    /// </summary>
    public static PseudoConsole Create(short cols, short rows)
    {
        // Create pipes for the pseudo console's input
        var inputSa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };

        if (!CreatePipe(out var inputReadPipe, out var inputWritePipe, ref inputSa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe.");

        // Create pipes for the pseudo console's output
        var outputSa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };

        if (!CreatePipe(out var outputReadPipe, out var outputWritePipe, ref outputSa, 0))
        {
            inputReadPipe.Dispose();
            inputWritePipe.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe.");
        }

        var size = new COORD(cols, rows);
        int hr = CreatePseudoConsole(size, inputReadPipe, outputWritePipe, 0, out var handle);

        if (hr != 0)
        {
            inputReadPipe.Dispose();
            inputWritePipe.Dispose();
            outputReadPipe.Dispose();
            outputWritePipe.Dispose();
            throw new Win32Exception(hr, $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}");
        }

        // Close the sides of the pipes that the ConPTY owns
        // The ConPTY now owns inputReadPipe and outputWritePipe
        inputReadPipe.Dispose();
        outputWritePipe.Dispose();

        // Caller writes to inputWritePipe (terminal input)
        // Caller reads from outputReadPipe (terminal output)
        return new PseudoConsole(handle, outputReadPipe, inputWritePipe);
    }

    /// <summary>
    /// Resizes the pseudo console.
    /// </summary>
    public void Resize(short cols, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = ResizePseudoConsole(_handle, new COORD(cols, rows));
        if (hr != 0)
            throw new Win32Exception(hr, $"ResizePseudoConsole failed with HRESULT 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClosePseudoConsole(_handle);
        _handle = IntPtr.Zero;

        ReadPipe.Dispose();
        WritePipe.Dispose();
    }
}
