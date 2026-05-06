using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class TerminalGridMetricsTests
{
    [Theory]
    [InlineData(9.6, 18.0)]   // typical Cascadia Mono @ 14pt
    [InlineData(9.4, 18.4)]
    [InlineData(10.0, 20.0)]
    [InlineData(10.49, 20.49)]
    public void RoundToPixelGrid_SnapsToIntegerPixels(double rawW, double rawH)
    {
        var (w, h) = TerminalGridMetrics.RoundToPixelGrid(rawW, rawH);
        w.Should().Be(Math.Round(rawW));
        h.Should().Be(Math.Round(rawH));
        (w % 1).Should().Be(0);
        (h % 1).Should().Be(0);
    }

    [Fact]
    public void RoundToPixelGrid_NeverReturnsZero()
    {
        var (w, h) = TerminalGridMetrics.RoundToPixelGrid(0.4, 0.3);
        w.Should().BeGreaterThanOrEqualTo(1);
        h.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void RoundToPixelGrid_HandlesNonFinite()
    {
        var (w, h) = TerminalGridMetrics.RoundToPixelGrid(double.NaN, double.PositiveInfinity);
        w.Should().BeGreaterThanOrEqualTo(1);
        h.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void RoundToPixelGrid_PreservesGridAlignmentAcrossManyColumns()
    {
        // Regression: with sub-pixel cellWidth, column 100 would land 100 *
        // fractional_pixels off-grid. After rounding, every column is exact.
        var (cw, _) = TerminalGridMetrics.RoundToPixelGrid(9.6, 18.0);
        for (int col = 0; col < 200; col++)
        {
            double x = col * cw;
            (x % 1).Should().Be(0, $"column {col} must land on an integer pixel");
        }
    }
}
