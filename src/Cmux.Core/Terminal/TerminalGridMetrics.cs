namespace Cmux.Core.Terminal;

/// <summary>
/// Pure helpers for computing terminal grid metrics from raw font measurements.
/// Extracted so the rounding behavior can be unit-tested without standing up a
/// WPF visual tree.
/// </summary>
public static class TerminalGridMetrics
{
    /// <summary>
    /// Snaps raw font cell width/height to integer pixels. Sub-pixel cell
    /// widths cause column N to drift by N * fractional_pixels across the row,
    /// which breaks box-drawing alignment in TUIs like Claude Code.
    /// </summary>
    public static (double CellWidth, double CellHeight) RoundToPixelGrid(double rawWidth, double rawHeight)
    {
        double w = double.IsFinite(rawWidth) ? Math.Round(rawWidth) : 0;
        double h = double.IsFinite(rawHeight) ? Math.Round(rawHeight) : 0;
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        return (w, h);
    }
}
