using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class VtParserTests
{
    [Fact]
    public void Feed_PrintableCharacters_RaisesOnPrint()
    {
        var parser = new VtParser();
        var printed = new List<char>();
        parser.OnPrint = c => printed.Add(c);

        parser.Feed("Hello");

        printed.Should().Equal('H', 'e', 'l', 'l', 'o');
    }

    [Fact]
    public void Feed_C0Controls_RaisesOnExecute()
    {
        var parser = new VtParser();
        var executed = new List<byte>();
        parser.OnExecute = b => executed.Add(b);

        parser.Feed("\r\n");

        executed.Should().Contain(0x0D); // CR
        executed.Should().Contain(0x0A); // LF
    }

    [Fact]
    public void Feed_CsiSequence_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        List<int>? receivedParams = null;
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedFinal = final;
        };

        // CSI 10;20H = cursor position (row 10, col 20)
        parser.Feed("\x1b[10;20H");

        receivedFinal.Should().Be('H');
        receivedParams.Should().NotBeNull();
        receivedParams.Should().Equal(10, 20);
    }

    [Fact]
    public void Feed_SgrReset_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedFinal = final;
        };

        parser.Feed("\x1b[0m");

        receivedFinal.Should().Be('m');
    }

    [Fact]
    public void Feed_OscString_RaisesOnOscDispatch()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        // OSC 0 ; My Title BEL
        parser.Feed("\x1b]0;My Title\x07");

        receivedOsc.Should().Be("0;My Title");
    }

    [Fact]
    public void Feed_Osc9Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]9;Agent needs input\x07");

        receivedOsc.Should().Be("9;Agent needs input");
    }

    [Fact]
    public void Feed_Osc777Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]777;notify;Claude;Waiting for input\x07");

        receivedOsc.Should().Be("777;notify;Claude;Waiting for input");
    }

    [Fact]
    public void Feed_EscSequence_RaisesOnEscDispatch()
    {
        var parser = new VtParser();
        byte? dispatched = null;
        parser.OnEscDispatch = b => dispatched = b;

        // ESC 7 = DECSC (save cursor)
        parser.Feed("\u001b7");

        dispatched.Should().Be((byte)'7');
    }

    [Fact]
    public void Feed_PrivateModeSet_ParsesCorrectly()
    {
        var parser = new VtParser();
        string? receivedQualifier = null;
        List<int>? receivedParams = null;
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedQualifier = qualifier;
        };

        // CSI ? 25 h = show cursor (DECTCEM)
        parser.Feed("\x1b[?25h");

        receivedParams.Should().Equal(25);
        receivedQualifier.Should().Contain("?");
    }
}

public class TerminalBufferTests
{
    [Fact]
    public void WriteChar_AdvancesCursor()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');

        buffer.CursorCol.Should().Be(1);
        buffer.CellAt(0, 0).Character.Should().Be("A");
    }

    [Fact]
    public void LineFeed_AtBottom_ScrollsUp()
    {
        var buffer = new TerminalBuffer(80, 3);

        buffer.WriteString("Line1");
        buffer.NewLine();
        buffer.WriteString("Line2");
        buffer.NewLine();
        buffer.WriteString("Line3");
        buffer.NewLine(); // Should scroll

        buffer.ScrollbackCount.Should().Be(1);
    }

    [Fact]
    public void EraseInDisplay_Mode2_ClearsAll()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        buffer.EraseInDisplay(2);

        buffer.CellAt(0, 0).Character.Should().Be(" ");
    }

    [Fact]
    public void Resize_PreservesContent()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("ABC");

        buffer.Resize(40, 12);

        buffer.CellAt(0, 0).Character.Should().Be("A");
        buffer.CellAt(0, 1).Character.Should().Be("B");
        buffer.CellAt(0, 2).Character.Should().Be("C");
        buffer.Cols.Should().Be(40);
        buffer.Rows.Should().Be(12);
    }

    [Fact]
    public void ScrollRegion_ScrollsOnlyWithinRegion()
    {
        var buffer = new TerminalBuffer(10, 5);
        buffer.SetScrollRegion(1, 3);
        buffer.MoveCursorTo(3, 0); // Bottom of scroll region
        buffer.WriteString("X");
        buffer.LineFeed(); // Should scroll only lines 1-3

        buffer.CellAt(0, 0).Character.Should().Be(" "); // Line 0 untouched
    }

    [Fact]
    public void SaveRestore_CursorPosition()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MoveCursorTo(5, 10);
        buffer.SaveCursor();

        buffer.MoveCursorTo(0, 0);
        buffer.RestoreCursor();

        buffer.CursorRow.Should().Be(5);
        buffer.CursorCol.Should().Be(10);
    }
}

public class OscHandlerTests
{
    [Fact]
    public void Handle_Osc0_ChangesTitleEvent()
    {
        var handler = new OscHandler();
        string? title = null;
        handler.TitleChanged += t => title = t;

        handler.Handle("0;My Terminal Title");

        title.Should().Be("My Terminal Title");
    }

    [Fact]
    public void Handle_Osc7_ChangesWorkingDirectory()
    {
        var handler = new OscHandler();
        string? dir = null;
        handler.WorkingDirectoryChanged += d => dir = d;

        handler.Handle("7;file://localhost/C:/Users/test/project");

        dir.Should().NotBeNull();
    }

    [Fact]
    public void Handle_Osc9_FiresNotification()
    {
        var handler = new OscHandler();
        string? body = null;
        handler.NotificationReceived += (t, s, b) => body = b;

        handler.Handle("9;Agent is waiting for your input");

        body.Should().Be("Agent is waiting for your input");
    }

    [Fact]
    public void Handle_Osc99_KeyValue_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b) => { title = t; body = b; };

        handler.Handle("99;t=Claude Code;b=Waiting for input");

        title.Should().Be("Claude Code");
        body.Should().Be("Waiting for input");
    }

    [Fact]
    public void Handle_Osc777_Notify_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b) => { title = t; body = b; };

        handler.Handle("777;notify;Claude;Task completed");

        title.Should().Be("Claude");
        body.Should().Be("Task completed");
    }

    [Fact]
    public void Handle_Osc133_FiresPromptMarker()
    {
        var handler = new OscHandler();
        char? marker = null;
        handler.ShellPromptMarker += m => marker = m;

        handler.Handle("133;A");

        marker.Should().Be('A');
    }
}

public class SplitNodeTests
{
    [Fact]
    public void CreateLeaf_IsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Split_TurnsLeafIntoContainer()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");

        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        node.IsLeaf.Should().BeFalse();
        node.First.Should().NotBeNull();
        node.Second.Should().NotBeNull();
        node.First!.PaneId.Should().Be("pane-1");
        newChild.PaneId.Should().NotBeNull();
    }

    [Fact]
    public void Split_NonLeaf_ThrowsInvalidOperation()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var act = () => node.Split(Cmux.Core.Models.SplitDirection.Horizontal);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FindNode_FindsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var found = node.FindNode("pane-1");

        found.Should().NotBeNull();
        found!.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetLeaves_ReturnsAllLeaves()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var leaves = node.GetLeaves().ToList();

        leaves.Should().HaveCount(2);
        leaves[0].PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Remove_CollapsesParent()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);
        var newPaneId = newChild.PaneId!;

        bool removed = node.Remove(newPaneId);

        removed.Should().BeTrue();
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetNextLeaf_CyclesCorrectly()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var child2 = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var next = node.GetNextLeaf("pane-1");
        next.Should().NotBeNull();
        next!.PaneId.Should().Be(child2.PaneId);

        // Wraps around
        var wrap = node.GetNextLeaf(child2.PaneId!);
        wrap.Should().NotBeNull();
        wrap!.PaneId.Should().Be("pane-1");
    }
}

public class TerminalColorTests
{
    [Fact]
    public void FromIndex_BasicColors_ReturnsExpected()
    {
        var black = TerminalColor.FromIndex(0);
        black.R.Should().Be(0);
        black.G.Should().Be(0);
        black.B.Should().Be(0);

        var white = TerminalColor.FromIndex(15);
        white.R.Should().Be(0xFF);
        white.G.Should().Be(0xFF);
        white.B.Should().Be(0xFF);
    }

    [Fact]
    public void FromIndex_256Colors_DoesNotThrow()
    {
        for (int i = 0; i < 256; i++)
        {
            var act = () => TerminalColor.FromIndex(i);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void FromRgb_StoresCorrectValues()
    {
        var color = TerminalColor.FromRgb(0x12, 0x34, 0x56);
        color.R.Should().Be(0x12);
        color.G.Should().Be(0x34);
        color.B.Should().Be(0x56);
        color.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Default_IsMarkedAsDefault()
    {
        var def = TerminalColor.Default;
        def.IsDefault.Should().BeTrue();
    }
}

public class TerminalSelectionTests
{
    [Fact]
    public void StartAndExtend_CreatesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(0, 10);

        selection.HasSelection.Should().BeTrue();
        selection.IsSelected(0, 7).Should().BeTrue();
        selection.IsSelected(0, 12).Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 10);

        selection.ClearSelection();

        selection.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void GetSelectedText_ExtractsCorrectly()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 4);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("Hello");
    }

    [Fact]
    public void IsSelected_MultiLine_Works()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(2, 10);

        selection.IsSelected(0, 6).Should().BeTrue();
        selection.IsSelected(1, 0).Should().BeTrue(); // Middle line, full
        selection.IsSelected(2, 5).Should().BeTrue();
        selection.IsSelected(2, 11).Should().BeFalse();
    }
}


public class AlternateScreenBufferTests
{
    [Fact]
    public void SwitchToAlternateScreen_ClearsAndSavesMainBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');
        buffer.CursorCol.Should().Be(1);

        buffer.SwitchToAlternateScreen();

        buffer.IsAlternateScreen.Should().BeTrue();
        buffer.CursorRow.Should().Be(0);
        buffer.CursorCol.Should().Be(0);
        buffer.CellAt(0, 0).Character.Should().Be(" ");
    }

    [Fact]
    public void SwitchToMainScreen_RestoresPreviousState()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');
        buffer.WriteChar('B');
        int savedCol = buffer.CursorCol;

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Z');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CursorCol.Should().Be(savedCol);
        buffer.CellAt(0, 0).Character.Should().Be("A");
        buffer.CellAt(0, 1).Character.Should().Be("B");
    }

    [Fact]
    public void SwitchToAlternateScreen_DoubleSwitchIsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Y');

        buffer.SwitchToAlternateScreen();

        buffer.CellAt(0, 0).Character.Should().Be("Y");
    }

    [Fact]
    public void SwitchToMainScreen_WhenNotAlternate_IsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CellAt(0, 0).Character.Should().Be("X");
    }
}

public class TerminalModeTests
{
    [Fact]
    public void ApplicationCursorKeys_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys.Should().BeFalse();
    }

    [Fact]
    public void BracketedPasteMode_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode.Should().BeFalse();
    }

    [Fact]
    public void ApplicationCursorKeys_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys = true;
        buffer.ApplicationCursorKeys.Should().BeTrue();
    }

    [Fact]
    public void BracketedPasteMode_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode = true;
        buffer.BracketedPasteMode.Should().BeTrue();
    }
}