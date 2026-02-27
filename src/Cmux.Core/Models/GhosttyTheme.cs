namespace Cmux.Core.Models;

public class GhosttyTheme
{
    public TerminalColor Background { get; set; } = new(30, 30, 30);
    public TerminalColor Foreground { get; set; } = new(204, 204, 204);
    public TerminalColor[] Palette { get; set; } = CreateDefaultPalette();
    public TerminalColor? SelectionBackground { get; set; }
    public TerminalColor? SelectionForeground { get; set; }
    public TerminalColor? CursorColor { get; set; }
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 13.0;

    /// <summary>
    /// Default 16-color ANSI palette (matches typical dark terminal theme).
    /// </summary>
    private static TerminalColor[] CreateDefaultPalette()
    {
        return
        [
            // Normal colors
            new(0x1e, 0x1e, 0x1e), // 0  Black
            new(0xf4, 0x47, 0x47), // 1  Red
            new(0x4e, 0xc9, 0xb0), // 2  Green
            new(0xd7, 0xba, 0x7d), // 3  Yellow
            new(0x56, 0x9c, 0xd6), // 4  Blue
            new(0xc5, 0x86, 0xc0), // 5  Magenta
            new(0x4e, 0xc9, 0xb0), // 6  Cyan
            new(0xcc, 0xcc, 0xcc), // 7  White
            // Bright colors
            new(0x80, 0x80, 0x80), // 8  Bright Black
            new(0xf4, 0x47, 0x47), // 9  Bright Red
            new(0x4e, 0xc9, 0xb0), // 10 Bright Green
            new(0xd7, 0xba, 0x7d), // 11 Bright Yellow
            new(0x56, 0x9c, 0xd6), // 12 Bright Blue
            new(0xc5, 0x86, 0xc0), // 13 Bright Magenta
            new(0x4e, 0xc9, 0xb0), // 14 Bright Cyan
            new(0xff, 0xff, 0xff), // 15 Bright White
        ];
    }
}
