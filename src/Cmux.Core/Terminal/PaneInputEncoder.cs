using System.Text;

namespace Cmux.Core.Terminal;

/// <summary>
/// Encodes CLI/pipe input requests (cmux send / cmux send-key) into the byte
/// sequences a terminal expects on its input stream. The key mapping mirrors
/// TerminalControl.KeyToVtSequence so injected keys behave like real keystrokes.
/// </summary>
public static class PaneInputEncoder
{
    public const string Enter = "\r";
    public const string BracketedPasteStart = "\x1b[200~";
    public const string BracketedPasteEnd = "\x1b[201~";

    public static string SupportedKeysDescription =>
        "enter, tab, shift-tab, escape, backspace, space, up, down, left, right, " +
        "home, end, insert, delete, pageup, pagedown, f1-f12, ctrl-a..ctrl-z, ctrl-space";

    /// <summary>
    /// Resolves a key name (e.g. "enter", "up", "ctrl-c") to its VT input sequence.
    /// Accepts both "ctrl-x" and "ctrl+x" spellings, case-insensitive.
    /// </summary>
    public static bool TryResolveKey(string keyName, out string sequence)
    {
        sequence = "";
        if (string.IsNullOrWhiteSpace(keyName))
            return false;

        var key = keyName.Trim().ToLowerInvariant().Replace('+', '-');

        if (key.StartsWith("ctrl-", StringComparison.Ordinal))
        {
            var rest = key["ctrl-".Length..];
            if (rest.Length == 1 && rest[0] >= 'a' && rest[0] <= 'z')
            {
                sequence = ((char)(rest[0] - 'a' + 1)).ToString();
                return true;
            }
            if (rest is "space")
            {
                sequence = "\0";
                return true;
            }
            return false;
        }

        sequence = key switch
        {
            "enter" or "return" or "cr" => "\r",
            "tab" => "\t",
            "shift-tab" or "btab" => "\x1b[Z",
            "escape" or "esc" => "\x1b",
            "backspace" or "bspace" or "bs" => "\x7f",
            "space" => " ",
            "up" => "\x1b[A",
            "down" => "\x1b[B",
            "right" => "\x1b[C",
            "left" => "\x1b[D",
            "home" => "\x1b[H",
            "end" => "\x1b[F",
            "insert" or "ins" => "\x1b[2~",
            "delete" or "del" => "\x1b[3~",
            "pageup" or "pgup" => "\x1b[5~",
            "pagedown" or "pgdn" => "\x1b[6~",
            "f1" => "\x1bOP",
            "f2" => "\x1bOQ",
            "f3" => "\x1bOR",
            "f4" => "\x1bOS",
            "f5" => "\x1b[15~",
            "f6" => "\x1b[17~",
            "f7" => "\x1b[18~",
            "f8" => "\x1b[19~",
            "f9" => "\x1b[20~",
            "f10" => "\x1b[21~",
            "f11" => "\x1b[23~",
            "f12" => "\x1b[24~",
            _ => "",
        };

        return sequence.Length > 0;
    }

    /// <summary>
    /// Normalizes newlines in a payload to the carriage return a keyboard produces:
    /// "\r\n" and lone "\n" both become "\r".
    /// </summary>
    public static string NormalizeNewlines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace("\n", "\r", StringComparison.Ordinal);
    }

    /// <summary>
    /// Wraps a payload in bracketed-paste markers so multiline text lands in
    /// TUI applications as a single block instead of submitting line-by-line.
    /// </summary>
    public static string WrapBracketedPaste(string text)
    {
        return BracketedPasteStart + text + BracketedPasteEnd;
    }

    /// <summary>
    /// Decodes a base64-encoded UTF-8 payload (used to keep arbitrary text safe
    /// inside the line-based pipe protocol).
    /// </summary>
    public static bool TryDecodeBase64Payload(string encoded, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(encoded))
            return true;

        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encodes a payload for transport over the line-based pipe protocol.
    /// </summary>
    public static string EncodeBase64Payload(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
    }
}
