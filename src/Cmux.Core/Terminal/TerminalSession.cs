using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cmux.Core.Terminal;

/// <summary>
/// A complete terminal session: ConPTY process + VT parser + buffer + OSC handling.
/// This is the main class that WPF controls interact with.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private PseudoConsole? _console;
    private TerminalProcess? _process;
    private readonly VtParser _parser;
    private readonly OscHandler _oscHandler;
    private FileStream? _readStream;
    private FileStream? _writeStream;
    private Thread? _readThread;
    private volatile bool _disposed;
    private readonly object _lock = new();

    public TerminalBuffer Buffer { get; }
    public string PaneId { get; }
    public string? Title { get; private set; }
    public string? WorkingDirectory { get; private set; }
    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ProcessId => _process?.ProcessId;

    // Events
    public event Action? OutputReceived;
    public event Action? ProcessExited;
    public event Action<string>? TitleChanged;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action<string, string?, string>? NotificationReceived;
    public event Action<char, string?>? ShellPromptMarker;
    public event Action? Redraw;
    public event Action? BellReceived;

    public TerminalSession(string paneId, int cols = 120, int rows = 30)
    {
        PaneId = paneId;
        Buffer = new TerminalBuffer(cols, rows);
        _parser = new VtParser();
        _oscHandler = new OscHandler();
        WireParser();
    }

    private void WireParser()
    {
        _parser.OnPrint = c => Buffer.WriteChar(c);

        _parser.OnExecute = b =>
        {
            switch (b)
            {
                case 0x07: // BEL
                    BellReceived?.Invoke();
                    break;
                case 0x08: // BS (Backspace)
                    Buffer.Backspace();
                    break;
                case 0x09: // HT (Tab)
                    Buffer.Tab();
                    break;
                case 0x0A: // LF (Line Feed)
                case 0x0B: // VT (Vertical Tab)
                case 0x0C: // FF (Form Feed)
                    Buffer.LineFeed();
                    break;
                case 0x0D: // CR (Carriage Return)
                    Buffer.CarriageReturn();
                    break;
            }
        };

        _parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            HandleCsi(parameters, final, qualifier);
        };

        _parser.OnEscDispatch = b =>
        {
            switch ((char)b)
            {
                case '7': // DECSC — Save Cursor
                    Buffer.SaveCursor();
                    break;
                case '8': // DECRC — Restore Cursor
                    Buffer.RestoreCursor();
                    break;
                case 'M': // RI — Reverse Index
                    Buffer.ReverseLineFeed();
                    break;
                case 'D': // IND — Index (line feed)
                    Buffer.LineFeed();
                    break;
                case 'E': // NEL — Next Line
                    Buffer.NewLine();
                    break;
                case 'c': // RIS — Full Reset
                    Buffer.Clear();
                    Buffer.CurrentAttribute = TerminalAttribute.Default;
                    Buffer.ResetScrollRegion();
                    Buffer.MoveCursorTo(0, 0);
                    break;
            }
        };

        _parser.OnOscDispatch = osc =>
        {
            _oscHandler.Handle(osc);
        };

        _oscHandler.TitleChanged += title =>
        {
            Title = title;
            TitleChanged?.Invoke(title);
        };

        _oscHandler.WorkingDirectoryChanged += dir =>
        {
            WorkingDirectory = dir;
            WorkingDirectoryChanged?.Invoke(dir);
        };

        _oscHandler.NotificationReceived += (title, subtitle, body) =>
        {
            NotificationReceived?.Invoke(title, subtitle, body);
        };

        _oscHandler.ShellPromptMarker += (marker, payload) =>
        {
            ShellPromptMarker?.Invoke(marker, payload);
        };
    }

    /// <summary>
    /// Starts the terminal process.
    /// </summary>
    public void Start(string? command = null, string? workingDirectory = null)
    {
        var fallbackDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(fallbackDirectory))
            fallbackDirectory = Environment.CurrentDirectory;

        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? fallbackDirectory
            : workingDirectory;

        WorkingDirectory = effectiveWorkingDirectory;

        lock (_lock)
        {
            _console = PseudoConsole.Create((short)Buffer.Cols, (short)Buffer.Rows);
            _process = new TerminalProcess(_console, command, effectiveWorkingDirectory);

            _readStream = new FileStream(_console.ReadPipe, FileAccess.Read);
            _writeStream = new FileStream(_console.WritePipe, FileAccess.Write);

            _process.Exited += () =>
            {
                ProcessExited?.Invoke();
            };

            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"Terminal-Read-{PaneId}",
            };
            _readThread.Start();
        }

        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
            WorkingDirectoryChanged?.Invoke(WorkingDirectory);
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed && _readStream != null)
            {
                int bytesRead = _readStream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                lock (_lock)
                {
                    _parser.Feed(buffer.AsSpan(0, bytesRead));
                }

                OutputReceived?.Invoke();
                Redraw?.Invoke();
            }
        }
        catch (IOException) when (_disposed)
        {
            // Expected on shutdown
        }
        catch (ObjectDisposedException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// Writes raw input (keyboard data) to the terminal process.
    /// </summary>
    public void Write(string text)
    {
        if (_disposed || _writeStream == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        Write(bytes);
    }

    /// <summary>
    /// Writes raw bytes to the terminal process.
    /// </summary>
    public void Write(byte[] data)
    {
        if (_disposed || _writeStream == null) return;
        try
        {
            _writeStream.Write(data, 0, data.Length);
            _writeStream.Flush();
        }
        catch (IOException) when (_disposed)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Resizes the terminal.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (_disposed) return;

        lock (_lock)
        {
            Buffer.Resize(cols, rows);
            _console?.Resize((short)cols, (short)rows);
        }

        Redraw?.Invoke();
    }

    public TerminalBufferSnapshot CreateBufferSnapshot(int maxScrollbackLines = 3000)
    {
        lock (_lock)
        {
            return Buffer.CreateSnapshot(maxScrollbackLines);
        }
    }

    public void RestoreBufferSnapshot(TerminalBufferSnapshot snapshot)
    {
        if (_disposed) return;

        lock (_lock)
        {
            Buffer.RestoreSnapshot(snapshot);
        }

        Redraw?.Invoke();
    }

    private void HandleCsi(List<int> parameters, char final, string qualifier)
    {
        int Param(int index, int defaultValue = 0) =>
            index < parameters.Count && parameters[index] != 0 ? parameters[index] : defaultValue;

        bool isPrivate = qualifier.Contains('?');

        switch (final)
        {
            // Cursor movement
            case 'A': // CUU — Cursor Up
                Buffer.MoveCursorUp(Param(0, 1));
                break;
            case 'B': // CUD — Cursor Down
                Buffer.MoveCursorDown(Param(0, 1));
                break;
            case 'C': // CUF — Cursor Forward
                Buffer.MoveCursorForward(Param(0, 1));
                break;
            case 'D': // CUB — Cursor Backward
                Buffer.MoveCursorBackward(Param(0, 1));
                break;
            case 'E': // CNL — Cursor Next Line
                Buffer.CarriageReturn();
                Buffer.MoveCursorDown(Param(0, 1));
                break;
            case 'F': // CPL — Cursor Previous Line
                Buffer.CarriageReturn();
                Buffer.MoveCursorUp(Param(0, 1));
                break;
            case 'G': // CHA — Cursor Horizontal Absolute
                Buffer.MoveCursorTo(Buffer.CursorRow, Param(0, 1) - 1);
                break;
            case 'H': // CUP — Cursor Position
            case 'f': // HVP — Horizontal Vertical Position
                Buffer.MoveCursorTo(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'd': // VPA — Vertical Position Absolute
                Buffer.MoveCursorTo(Param(0, 1) - 1, Buffer.CursorCol);
                break;

            // Erase
            case 'J': // ED — Erase in Display
                Buffer.EraseInDisplay(Param(0));
                break;
            case 'K': // EL — Erase in Line
                Buffer.EraseInLine(Param(0));
                break;
            case 'X': // ECH — Erase Characters
                Buffer.EraseChars(Param(0, 1));
                break;

            // Insert/Delete
            case 'L': // IL — Insert Lines
                Buffer.InsertLines(Param(0, 1));
                break;
            case 'M': // DL — Delete Lines
                Buffer.DeleteLines(Param(0, 1));
                break;
            case '@': // ICH — Insert Characters
                Buffer.InsertChars(Param(0, 1));
                break;
            case 'P': // DCH — Delete Characters
                Buffer.DeleteChars(Param(0, 1));
                break;

            // Scroll
            case 'S': // SU — Scroll Up
                Buffer.ScrollUp(Param(0, 1));
                break;
            case 'T': // SD — Scroll Down
                Buffer.ScrollDown(Param(0, 1));
                break;

            // Scroll region
            case 'r': // DECSTBM — Set Top and Bottom Margins
                if (parameters.Count == 0)
                {
                    Buffer.ResetScrollRegion();
                }
                else
                {
                    Buffer.SetScrollRegion(Param(0, 1) - 1, Param(1, Buffer.Rows) - 1);
                }
                Buffer.MoveCursorTo(0, 0);
                break;

            // SGR — Select Graphic Rendition
            case 'm':
                HandleSgr(parameters);
                break;

            // Mode set/reset
            case 'h': // SM / DECSET
                HandleMode(parameters, true, isPrivate);
                break;
            case 'l': // RM / DECRST
                HandleMode(parameters, false, isPrivate);
                break;

            // Cursor save/restore
            case 's': // SCOSC — Save Cursor Position
                if (!isPrivate)
                    Buffer.SaveCursor();
                break;
            case 'u': // SCORC — Restore Cursor Position
                if (!isPrivate)
                    Buffer.RestoreCursor();
                break;

            // Device status
            case 'n': // DSR — Device Status Report
                if (Param(0) == 6)
                {
                    // CPR — Cursor Position Report
                    Write($"\x1b[{Buffer.CursorRow + 1};{Buffer.CursorCol + 1}R");
                }
                break;

            case 'c': // DA — Device Attributes
                if (!isPrivate)
                    Write("\x1b[?1;0c");
                break;
        }
    }

    private void HandleSgr(List<int> parameters)
    {
        if (parameters.Count == 0)
        {
            Buffer.CurrentAttribute = TerminalAttribute.Default;
            return;
        }

        var attr = Buffer.CurrentAttribute;
        int i = 0;
        while (i < parameters.Count)
        {
            int code = parameters[i];
            switch (code)
            {
                case 0: attr = TerminalAttribute.Default; break;
                case 1: attr.Flags |= CellFlags.Bold; break;
                case 2: attr.Flags |= CellFlags.Dim; break;
                case 3: attr.Flags |= CellFlags.Italic; break;
                case 4: attr.Flags |= CellFlags.Underline; break;
                case 5: attr.Flags |= CellFlags.Blink; break;
                case 7: attr.Flags |= CellFlags.Inverse; break;
                case 8: attr.Flags |= CellFlags.Hidden; break;
                case 9: attr.Flags |= CellFlags.Strikethrough; break;
                case 21: attr.Flags &= ~CellFlags.Bold; break;
                case 22: attr.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: attr.Flags &= ~CellFlags.Italic; break;
                case 24: attr.Flags &= ~CellFlags.Underline; break;
                case 25: attr.Flags &= ~CellFlags.Blink; break;
                case 27: attr.Flags &= ~CellFlags.Inverse; break;
                case 28: attr.Flags &= ~CellFlags.Hidden; break;
                case 29: attr.Flags &= ~CellFlags.Strikethrough; break;

                // Foreground colors
                case >= 30 and <= 37:
                    attr.Foreground = TerminalColor.FromIndex(code - 30);
                    break;
                case 38: // Extended foreground
                    i = ParseExtendedColor(parameters, i, out var fg);
                    attr.Foreground = fg;
                    continue;
                case 39: attr.Foreground = TerminalColor.Default; break;

                // Background colors
                case >= 40 and <= 47:
                    attr.Background = TerminalColor.FromIndex(code - 40);
                    break;
                case 48: // Extended background
                    i = ParseExtendedColor(parameters, i, out var bg);
                    attr.Background = bg;
                    continue;
                case 49: attr.Background = TerminalColor.Default; break;

                // Bright foreground
                case >= 90 and <= 97:
                    attr.Foreground = TerminalColor.FromIndex(code - 90 + 8);
                    break;

                // Bright background
                case >= 100 and <= 107:
                    attr.Background = TerminalColor.FromIndex(code - 100 + 8);
                    break;
            }
            i++;
        }
        Buffer.CurrentAttribute = attr;
    }

    private static int ParseExtendedColor(List<int> parameters, int index, out TerminalColor color)
    {
        color = TerminalColor.Default;
        if (index + 1 >= parameters.Count)
            return index + 1;

        int type = parameters[index + 1];
        switch (type)
        {
            case 5: // 256-color: 38;5;N
                if (index + 2 < parameters.Count)
                {
                    color = TerminalColor.FromIndex(parameters[index + 2]);
                    return index + 3;
                }
                return index + 2;

            case 2: // Truecolor: 38;2;R;G;B
                if (index + 4 < parameters.Count)
                {
                    color = TerminalColor.FromRgb(
                        (byte)Math.Clamp(parameters[index + 2], 0, 255),
                        (byte)Math.Clamp(parameters[index + 3], 0, 255),
                        (byte)Math.Clamp(parameters[index + 4], 0, 255));
                    return index + 5;
                }
                return index + 2;

            default:
                return index + 1;
        }
    }

    private void HandleMode(List<int> parameters, bool set, bool isPrivate)
    {
        foreach (int param in parameters)
        {
            if (isPrivate)
            {
                switch (param)
                {
                    case 1: // DECCKM -- Cursor Keys Mode
                        Buffer.ApplicationCursorKeys = set;
                        break;
                    case 6: // DECOM — Origin Mode
                        Buffer.OriginMode = set;
                        break;
                    case 7: // DECAWM — Auto-wrap Mode
                        Buffer.AutoWrapMode = set;
                        break;
                    case 25: // DECTCEM — Text Cursor Enable Mode
                        Buffer.CursorVisible = set;
                        break;
                    case 1049: // Alternate screen buffer
                        if (set)
                        {
                            Buffer.SwitchToAlternateScreen();
                        }
                        else
                        {
                            Buffer.SwitchToMainScreen();
                        }
                        break;
                    case 47: // Alternate screen (older)
                    case 1047: // Alternate screen (xterm)
                        if (set)
                            Buffer.SwitchToAlternateScreen();
                        else
                            Buffer.SwitchToMainScreen();
                        break;
                    case 2004: // Bracketed paste mode
                        Buffer.BracketedPasteMode = set;
                        break;
                    case 1000: // Normal mouse tracking
                        Buffer.MouseTrackingNormal = set;
                        break;
                    case 1002: // Button-event mouse tracking
                        Buffer.MouseTrackingButton = set;
                        break;
                    case 1003: // Any-event mouse tracking
                        Buffer.MouseTrackingAny = set;
                        break;
                    case 1006: // SGR extended mouse reporting
                        Buffer.MouseSgrExtended = set;
                        break;
                }
            }
            else
            {
                switch (param)
                {
                    case 4: // IRM — Insert/Replace Mode
                        Buffer.InsertMode = set;
                        break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _readStream?.Dispose();
        _writeStream?.Dispose();
        _process?.Dispose();
        _console?.Dispose();
    }
}
