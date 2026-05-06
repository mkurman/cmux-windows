using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class ScrollbackNavigatorTests
{
    [Fact]
    public void Scroll_FromLiveView_NegativeDeltaEntersHistory()
    {
        ScrollbackNavigator.Scroll(currentOffset: 0, lineDelta: -10, scrollbackCount: 100)
            .Should().Be(-10);
    }

    [Fact]
    public void Scroll_FromLiveView_PositiveDeltaIsClampedAtZero()
    {
        // Already at bottom — scrolling further down stays at bottom.
        ScrollbackNavigator.Scroll(currentOffset: 0, lineDelta: 5, scrollbackCount: 100)
            .Should().Be(0);
    }

    [Fact]
    public void Scroll_BeyondTopOfHistory_ClampsToScrollbackCount()
    {
        ScrollbackNavigator.Scroll(currentOffset: -90, lineDelta: -50, scrollbackCount: 100)
            .Should().Be(-100);
    }

    [Fact]
    public void Scroll_BackTowardLiveView_ClampsAtZero()
    {
        ScrollbackNavigator.Scroll(currentOffset: -10, lineDelta: 50, scrollbackCount: 100)
            .Should().Be(0);
    }

    [Fact]
    public void Scroll_NoScrollbackAvailable_StaysAtZero()
    {
        ScrollbackNavigator.Scroll(currentOffset: 0, lineDelta: -50, scrollbackCount: 0)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(30, 29)]   // typical viewport
    [InlineData(2, 1)]
    [InlineData(1, 1)]     // one-line viewport — page step at least 1
    [InlineData(0, 1)]     // degenerate — never zero
    public void PageStep_LeavesOneRowOverlap(int visibleRows, int expected)
    {
        ScrollbackNavigator.PageStep(visibleRows).Should().Be(expected);
    }
}
