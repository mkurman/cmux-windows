using Cmux.Core.Terminal;

namespace Cmux.Core.Config;

public class TerminalTheme
{
    public required string Name { get; init; }
    public string Author { get; init; } = string.Empty;
    public TerminalColor Background { get; init; }
    public TerminalColor Foreground { get; init; }
    public TerminalColor CursorColor { get; init; }
    public TerminalColor SelectionBg { get; init; }
    public required TerminalColor[] Palette { get; init; }
}

public static class TerminalThemes
{
    private const string DefaultDarkName = "Default Dark";

    public static TerminalTheme GetEffective(CmuxSettings settings)
    {
        var baseTheme = Get(settings.ThemeName);
        if (settings == null || !settings.UseCustomTerminalColors)
            return baseTheme;

        var background = ParseHexOrDefault(settings.CustomTerminalBackground, baseTheme.Background);
        var foreground = ParseHexOrDefault(settings.CustomTerminalForeground, baseTheme.Foreground);
        var cursor = ParseHexOrDefault(settings.CustomTerminalCursor, baseTheme.CursorColor);
        var selection = ParseHexOrDefault(settings.CustomTerminalSelection, baseTheme.SelectionBg);

        return new TerminalTheme
        {
            Name = $"{baseTheme.Name} (custom)",
            Author = baseTheme.Author,
            Background = background,
            Foreground = foreground,
            CursorColor = cursor,
            SelectionBg = selection,
            Palette = baseTheme.Palette.ToArray(),
        };
    }

    public static TerminalColor ParseHexOrDefault(string? value, TerminalColor fallback)
    {
        if (TryParseHexColor(value, out var color))
            return color;

        return fallback;
    }

    public static bool TryParseHexColor(string? value, out TerminalColor color)
    {
        color = TerminalColor.Default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;

        if (text.Length == 7)
        {
            if (byte.TryParse(text[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(text[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(text[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                color = new TerminalColor(r, g, b);
                return true;
            }

            return false;
        }

        if (text.Length == 9)
        {
            if (byte.TryParse(text[3..5], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(text[5..7], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(text[7..9], System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                color = new TerminalColor(r, g, b);
                return true;
            }
        }

        return false;
    }

    public static string ToHex(TerminalColor color) => $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Dictionary<string, TerminalTheme> BuiltIn { get; } = new()
    {
        ["Default Dark"] = new TerminalTheme
        {
            Name = "Default Dark",
            Author = "cmux",
            Background = new TerminalColor(0x0F, 0x0F, 0x0F),
            Foreground = new TerminalColor(0xE0, 0xE0, 0xE0),
            CursorColor = new TerminalColor(0x81, 0x8C, 0xF8),
            SelectionBg = new TerminalColor(0x33, 0x33, 0x55),
            Palette =
            [
                new(0x1E, 0x1E, 0x1E), new(0xEF, 0x44, 0x44), new(0x10, 0xB9, 0x81), new(0xFB, 0xBF, 0x24),
                new(0x63, 0x66, 0xF1), new(0xA7, 0x8B, 0xFA), new(0x2D, 0xD4, 0xBF), new(0xE0, 0xE0, 0xE0),
                new(0x6B, 0x72, 0x80), new(0xFB, 0x92, 0x3C), new(0x34, 0xD3, 0x99), new(0xFB, 0xBF, 0x24),
                new(0x81, 0x8C, 0xF8), new(0xC4, 0xB5, 0xFD), new(0x5E, 0xEA, 0xD4), new(0xFF, 0xFF, 0xFF),
            ],
        },
        ["Dracula"] = new TerminalTheme
        {
            Name = "Dracula",
            Author = "Zeno Rocha",
            Background = new TerminalColor(0x28, 0x2A, 0x36),
            Foreground = new TerminalColor(0xF8, 0xF8, 0xF2),
            CursorColor = new TerminalColor(0xF8, 0xF8, 0xF2),
            SelectionBg = new TerminalColor(0x44, 0x47, 0x5A),
            Palette =
            [
                new(0x21, 0x22, 0x2C), new(0xFF, 0x55, 0x55), new(0x50, 0xFA, 0x7B), new(0xF1, 0xFA, 0x8C),
                new(0xBD, 0x93, 0xF9), new(0xFF, 0x79, 0xC6), new(0x8B, 0xE9, 0xFD), new(0xF8, 0xF8, 0xF2),
                new(0x62, 0x72, 0xA4), new(0xFF, 0x6E, 0x6E), new(0x69, 0xFF, 0x94), new(0xFF, 0xFF, 0xA5),
                new(0xD6, 0xAC, 0xFF), new(0xFF, 0x92, 0xDF), new(0xA4, 0xFF, 0xFF), new(0xFF, 0xFF, 0xFF),
            ],
        },
        ["Nord"] = new TerminalTheme
        {
            Name = "Nord",
            Author = "Arctic Ice Studio",
            Background = new TerminalColor(0x2E, 0x34, 0x40),
            Foreground = new TerminalColor(0xD8, 0xDE, 0xE9),
            CursorColor = new TerminalColor(0xD8, 0xDE, 0xE9),
            SelectionBg = new TerminalColor(0x43, 0x4C, 0x5E),
            Palette =
            [
                new(0x3B, 0x42, 0x52), new(0xBF, 0x61, 0x6A), new(0xA3, 0xBE, 0x8C), new(0xEB, 0xCB, 0x8B),
                new(0x81, 0xA1, 0xC1), new(0xB4, 0x8E, 0xAD), new(0x88, 0xC0, 0xD0), new(0xE5, 0xE9, 0xF0),
                new(0x4C, 0x56, 0x6A), new(0xBF, 0x61, 0x6A), new(0xA3, 0xBE, 0x8C), new(0xEB, 0xCB, 0x8B),
                new(0x81, 0xA1, 0xC1), new(0xB4, 0x8E, 0xAD), new(0x8F, 0xBC, 0xBB), new(0xEC, 0xEF, 0xF4),
            ],
        },
        ["Solarized Dark"] = new TerminalTheme
        {
            Name = "Solarized Dark",
            Author = "Ethan Schoonover",
            Background = new TerminalColor(0x00, 0x2B, 0x36),
            Foreground = new TerminalColor(0x83, 0x94, 0x96),
            CursorColor = new TerminalColor(0x83, 0x94, 0x96),
            SelectionBg = new TerminalColor(0x07, 0x36, 0x42),
            Palette =
            [
                new(0x07, 0x36, 0x42), new(0xDC, 0x32, 0x2F), new(0x85, 0x99, 0x00), new(0xB5, 0x89, 0x00),
                new(0x26, 0x8B, 0xD2), new(0xD3, 0x36, 0x82), new(0x2A, 0xA1, 0x98), new(0xEE, 0xE8, 0xD5),
                new(0x00, 0x2B, 0x36), new(0xCB, 0x4B, 0x16), new(0x58, 0x6E, 0x75), new(0x65, 0x7B, 0x83),
                new(0x83, 0x94, 0x96), new(0x6C, 0x71, 0xC4), new(0x93, 0xA1, 0xA1), new(0xFD, 0xF6, 0xE3),
            ],
        },
        ["One Dark"] = new TerminalTheme
        {
            Name = "One Dark",
            Author = "Atom",
            Background = new TerminalColor(0x28, 0x2C, 0x34),
            Foreground = new TerminalColor(0xAB, 0xB2, 0xBF),
            CursorColor = new TerminalColor(0x52, 0x8B, 0xFF),
            SelectionBg = new TerminalColor(0x3E, 0x44, 0x51),
            Palette =
            [
                new(0x28, 0x2C, 0x34), new(0xE0, 0x6C, 0x75), new(0x98, 0xC3, 0x79), new(0xE5, 0xC0, 0x7B),
                new(0x61, 0xAF, 0xEF), new(0xC6, 0x78, 0xDD), new(0x56, 0xB6, 0xC2), new(0xAB, 0xB2, 0xBF),
                new(0x54, 0x58, 0x62), new(0xE0, 0x6C, 0x75), new(0x98, 0xC3, 0x79), new(0xE5, 0xC0, 0x7B),
                new(0x61, 0xAF, 0xEF), new(0xC6, 0x78, 0xDD), new(0x56, 0xB6, 0xC2), new(0xFF, 0xFF, 0xFF),
            ],
        },
        ["Monokai"] = new TerminalTheme
        {
            Name = "Monokai",
            Author = "Wimer Hazenberg",
            Background = new TerminalColor(0x27, 0x28, 0x22),
            Foreground = new TerminalColor(0xF8, 0xF8, 0xF2),
            CursorColor = new TerminalColor(0xF8, 0xF8, 0xF0),
            SelectionBg = new TerminalColor(0x49, 0x48, 0x3E),
            Palette =
            [
                new(0x27, 0x28, 0x22), new(0xF9, 0x26, 0x72), new(0xA6, 0xE2, 0x2E), new(0xF4, 0xBF, 0x75),
                new(0x66, 0xD9, 0xEF), new(0xAE, 0x81, 0xFF), new(0xA1, 0xEF, 0xE4), new(0xF8, 0xF8, 0xF2),
                new(0x75, 0x71, 0x5E), new(0xF9, 0x26, 0x72), new(0xA6, 0xE2, 0x2E), new(0xF4, 0xBF, 0x75),
                new(0x66, 0xD9, 0xEF), new(0xAE, 0x81, 0xFF), new(0xA1, 0xEF, 0xE4), new(0xF9, 0xF8, 0xF5),
            ],
        },
        ["Tokyo Night"] = new TerminalTheme
        {
            Name = "Tokyo Night",
            Author = "enkia",
            Background = new TerminalColor(0x1A, 0x1B, 0x26),
            Foreground = new TerminalColor(0xA9, 0xB1, 0xD6),
            CursorColor = new TerminalColor(0xC0, 0xCA, 0xF5),
            SelectionBg = new TerminalColor(0x28, 0x2E, 0x44),
            Palette =
            [
                new(0x1A, 0x1B, 0x26), new(0xF7, 0x76, 0x8E), new(0x9E, 0xCE, 0x6A), new(0xE0, 0xAF, 0x68),
                new(0x7A, 0xA2, 0xF7), new(0xBB, 0x9A, 0xF7), new(0x7D, 0xCF, 0xFF), new(0xA9, 0xB1, 0xD6),
                new(0x41, 0x48, 0x68), new(0xF7, 0x76, 0x8E), new(0x9E, 0xCE, 0x6A), new(0xE0, 0xAF, 0x68),
                new(0x7A, 0xA2, 0xF7), new(0xBB, 0x9A, 0xF7), new(0x7D, 0xCF, 0xFF), new(0xC0, 0xCA, 0xF5),
            ],
        },
        ["Catppuccin Mocha"] = new TerminalTheme
        {
            Name = "Catppuccin Mocha",
            Author = "Catppuccin",
            Background = new TerminalColor(0x1E, 0x1E, 0x2E),
            Foreground = new TerminalColor(0xCD, 0xD6, 0xF4),
            CursorColor = new TerminalColor(0xF5, 0xE0, 0xDC),
            SelectionBg = new TerminalColor(0x31, 0x32, 0x44),
            Palette =
            [
                new(0x45, 0x47, 0x5A), new(0xF3, 0x8B, 0xA8), new(0xA6, 0xE3, 0xA1), new(0xF9, 0xE2, 0xAF),
                new(0x89, 0xB4, 0xFA), new(0xCB, 0xA6, 0xF7), new(0x94, 0xE2, 0xD5), new(0xBA, 0xC2, 0xDE),
                new(0x58, 0x5B, 0x70), new(0xF3, 0x8B, 0xA8), new(0xA6, 0xE3, 0xA1), new(0xF9, 0xE2, 0xAF),
                new(0x89, 0xB4, 0xFA), new(0xCB, 0xA6, 0xF7), new(0x94, 0xE2, 0xD5), new(0xA6, 0xAD, 0xC8),
            ],
        },
    };

    public static IReadOnlyList<string> Names { get; } = BuiltIn.Keys.ToList().AsReadOnly();

    public static TerminalTheme Get(string name)
    {
        return BuiltIn.TryGetValue(name, out var theme) ? theme : BuiltIn[DefaultDarkName];
    }
}
