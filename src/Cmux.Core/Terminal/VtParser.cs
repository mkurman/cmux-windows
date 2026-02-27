using System.Text;

namespace Cmux.Core.Terminal;

/// <summary>
/// State machine VT100/xterm parser. Processes a stream of bytes and
/// dispatches events for printable characters, C0 controls, CSI sequences,
/// ESC sequences, and OSC strings.
///
/// Based on Paul Flo Williams' VT parser state machine:
/// https://vt100.net/emu/dec_ansi_parser
/// </summary>
public class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApc,
    }

    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _oscString = new();
    private readonly List<int> _csiParams = [];
    private byte _collectChar;

    // UTF-8 decoder state
    private int _utf8Remaining;
    private int _utf8Codepoint;

    // Callbacks
    public Action<char>? OnPrint { get; set; }
    public Action<byte>? OnExecute { get; set; }
    public Action<List<int>, char, string>? OnCsiDispatch { get; set; }
    public Action<byte>? OnEscDispatch { get; set; }
    public Action<string>? OnOscDispatch { get; set; }

    /// <summary>
    /// Feed raw bytes into the parser.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            ProcessByte(b);
    }

    /// <summary>
    /// Feed a string into the parser (convenience for UTF-16 text).
    /// </summary>
    public void Feed(string text)
    {
        Feed(Encoding.UTF8.GetBytes(text));
    }

    private void ProcessByte(byte b)
    {
        // Handle UTF-8 continuation bytes
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
                _utf8Remaining--;
                if (_utf8Remaining == 0)
                {
                    char c = (char)_utf8Codepoint;
                    HandlePrint(c);
                }
                return;
            }
            // Invalid continuation — reset and reprocess
            _utf8Remaining = 0;
        }

        // Start of multi-byte UTF-8
        if (_state == State.Ground && b >= 0xC0 && b <= 0xF7)
        {
            if (b < 0xE0)
            {
                _utf8Remaining = 1;
                _utf8Codepoint = b & 0x1F;
            }
            else if (b < 0xF0)
            {
                _utf8Remaining = 2;
                _utf8Codepoint = b & 0x0F;
            }
            else
            {
                _utf8Remaining = 3;
                _utf8Codepoint = b & 0x07;
            }
            return;
        }

        // Anywhere transitions (apply in any state)
        switch (b)
        {
            case 0x1B: // ESC
                _state = State.Escape;
                _intermediates.Clear();
                _params.Clear();
                _collectChar = 0;
                return;
            case 0x18 or 0x1A: // CAN, SUB — cancel current sequence
                _state = State.Ground;
                return;
        }

        switch (_state)
        {
            case State.Ground:
                ProcessGround(b);
                break;
            case State.Escape:
                ProcessEscape(b);
                break;
            case State.EscapeIntermediate:
                ProcessEscapeIntermediate(b);
                break;
            case State.CsiEntry:
                ProcessCsiEntry(b);
                break;
            case State.CsiParam:
                ProcessCsiParam(b);
                break;
            case State.CsiIntermediate:
                ProcessCsiIntermediate(b);
                break;
            case State.CsiIgnore:
                ProcessCsiIgnore(b);
                break;
            case State.OscString:
                ProcessOscString(b);
                break;
            case State.DcsEntry:
            case State.DcsParam:
            case State.DcsIntermediate:
            case State.DcsPassthrough:
            case State.DcsIgnore:
                ProcessDcs(b);
                break;
            case State.SosPmApc:
                ProcessSosPmApc(b);
                break;
        }
    }

    private void ProcessGround(byte b)
    {
        if (b < 0x20)
        {
            OnExecute?.Invoke(b);
        }
        else if (b == 0x7F)
        {
            // DEL — ignore in ground
        }
        else
        {
            HandlePrint((char)b);
        }
    }

    private void HandlePrint(char c)
    {
        if (_state == State.Ground || _state == State.Escape)
        {
            if (_state == State.Escape)
                _state = State.Ground;
            OnPrint?.Invoke(c);
        }
    }

    private void ProcessEscape(byte b)
    {
        if (b == (byte)'[')
        {
            _state = State.CsiEntry;
            _params.Clear();
            _intermediates.Clear();
            _csiParams.Clear();
            return;
        }

        if (b == (byte)']')
        {
            _state = State.OscString;
            _oscString.Clear();
            return;
        }

        if (b == (byte)'P')
        {
            _state = State.DcsEntry;
            return;
        }

        if (b is (byte)'X' or (byte)'^' or (byte)'_')
        {
            _state = State.SosPmApc;
            return;
        }

        if (b is >= 0x20 and <= 0x2F) // Intermediate
        {
            _intermediates.Append((char)b);
            _state = State.EscapeIntermediate;
            return;
        }

        if (b is >= 0x30 and <= 0x7E) // Final
        {
            OnEscDispatch?.Invoke(b);
            _state = State.Ground;
            return;
        }

        // Ignore anything else and return to ground
        _state = State.Ground;
    }

    private void ProcessEscapeIntermediate(byte b)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediates.Append((char)b);
            return;
        }

        if (b is >= 0x30 and <= 0x7E)
        {
            OnEscDispatch?.Invoke(b);
            _state = State.Ground;
            return;
        }

        _state = State.Ground;
    }

    private void ProcessCsiEntry(byte b)
    {
        if (b is >= 0x30 and <= 0x39 or (byte)';') // Param
        {
            _params.Append((char)b);
            _state = State.CsiParam;
            return;
        }

        if (b is (byte)'?' or (byte)'>' or (byte)'!' or (byte)'=') // Private modifier
        {
            _collectChar = b;
            _state = State.CsiParam;
            return;
        }

        if (b is >= 0x20 and <= 0x2F) // Intermediate
        {
            _intermediates.Append((char)b);
            _state = State.CsiIntermediate;
            return;
        }

        if (b is >= 0x40 and <= 0x7E) // Final — immediate dispatch
        {
            ParseCsiParams();
            DispatchCsi(b);
            return;
        }

        if (b < 0x20) // C0 control in CSI
        {
            OnExecute?.Invoke(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void ProcessCsiParam(byte b)
    {
        if (b is >= 0x30 and <= 0x39 or (byte)';' or (byte)':')
        {
            _params.Append((char)b);
            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediates.Append((char)b);
            _state = State.CsiIntermediate;
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            ParseCsiParams();
            DispatchCsi(b);
            return;
        }

        if (b < 0x20)
        {
            OnExecute?.Invoke(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void ProcessCsiIntermediate(byte b)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediates.Append((char)b);
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            ParseCsiParams();
            DispatchCsi(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void ProcessCsiIgnore(byte b)
    {
        if (b is >= 0x40 and <= 0x7E)
            _state = State.Ground;
    }

    private void ProcessOscString(byte b)
    {
        if (b == 0x07) // BEL terminates OSC
        {
            OnOscDispatch?.Invoke(_oscString.ToString());
            _state = State.Ground;
            return;
        }

        if (b == 0x9C) // ST (8-bit)
        {
            OnOscDispatch?.Invoke(_oscString.ToString());
            _state = State.Ground;
            return;
        }

        if (b == 0x1B) // Possible ST (ESC \)
        {
            // Will be handled on next byte — peek ahead not needed,
            // the ESC handler will fire. But we need to dispatch first.
            OnOscDispatch?.Invoke(_oscString.ToString());
            _state = State.Escape;
            return;
        }

        if (b >= 0x20 || b == 0x09) // Printable or tab
        {
            _oscString.Append((char)b);
        }
    }

    private void ProcessDcs(byte b)
    {
        // Simplified DCS handling — just consume until ST
        if (b == 0x9C || b == 0x1B)
            _state = b == 0x1B ? State.Escape : State.Ground;
    }

    private void ProcessSosPmApc(byte b)
    {
        // Consume until ST
        if (b == 0x9C || b == 0x1B)
            _state = b == 0x1B ? State.Escape : State.Ground;
    }

    private void ParseCsiParams()
    {
        _csiParams.Clear();
        if (_params.Length == 0) return;

        var paramStr = _params.ToString();
        foreach (var part in paramStr.Split(';'))
        {
            if (int.TryParse(part, out int val))
                _csiParams.Add(val);
            else
                _csiParams.Add(0);
        }
    }

    private void DispatchCsi(byte finalByte)
    {
        char prefix = _collectChar != 0 ? (char)_collectChar : '\0';
        string intermediates = _intermediates.ToString();

        // Build a qualifier string for the dispatch
        string qualifier = "";
        if (prefix != '\0') qualifier += prefix;
        qualifier += intermediates;

        OnCsiDispatch?.Invoke(_csiParams, (char)finalByte, qualifier);
        _state = State.Ground;
    }

    /// <summary>
    /// Resets the parser to its initial state.
    /// </summary>
    public void Reset()
    {
        _state = State.Ground;
        _params.Clear();
        _intermediates.Clear();
        _oscString.Clear();
        _csiParams.Clear();
        _collectChar = 0;
        _utf8Remaining = 0;
        _utf8Codepoint = 0;
    }
}
