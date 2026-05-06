using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class TerminalSelectionAutoCopyTests
{
    [Fact]
    public void HasNonEmptySelection_AfterClickWithoutDrag_IsFalse()
    {
        // Regression: AutoCopyOnSelect was overwriting the clipboard with a
        // single character after every plain click because HasSelection
        // returned true even when start == end. Auto-copy must only fire when
        // the user actually selected a non-zero range.
        var sel = new TerminalSelection();
        sel.StartSelection(3, 5);

        sel.HasSelection.Should().BeTrue("start/end are both set after click");
        sel.HasNonEmptySelection.Should().BeFalse("a click with no drag selects nothing copyable");
    }

    [Fact]
    public void HasNonEmptySelection_AfterDrag_IsTrue()
    {
        var sel = new TerminalSelection();
        sel.StartSelection(3, 5);
        sel.ExtendSelection(3, 12);

        sel.HasNonEmptySelection.Should().BeTrue();
    }

    [Fact]
    public void HasNonEmptySelection_DragAcrossRows_IsTrue()
    {
        var sel = new TerminalSelection();
        sel.StartSelection(3, 10);
        sel.ExtendSelection(5, 0);

        sel.HasNonEmptySelection.Should().BeTrue();
    }

    [Fact]
    public void HasNonEmptySelection_AfterClear_IsFalse()
    {
        var sel = new TerminalSelection();
        sel.StartSelection(0, 0);
        sel.ExtendSelection(0, 5);
        sel.ClearSelection();

        sel.HasNonEmptySelection.Should().BeFalse();
    }

    [Fact]
    public void GetSelectedText_AfterDragSelectsMultipleChars()
    {
        // Sanity check that selection across columns returns the full string,
        // not just the first char (the symptom the user reported).
        var buffer = new TerminalBuffer(80, 24);
        var attr = TerminalAttribute.Default;
        var text = "hello world";
        for (int i = 0; i < text.Length; i++)
            buffer.SetChar(0, i, text[i], attr);

        var sel = new TerminalSelection();
        sel.StartSelection(0, 0);
        sel.ExtendSelection(0, text.Length - 1);

        sel.GetSelectedText(buffer).Should().Be("hello world");
    }
}
