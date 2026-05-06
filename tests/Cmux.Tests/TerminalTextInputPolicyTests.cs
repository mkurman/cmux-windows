using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class TerminalTextInputPolicyTests
{
    [Fact]
    public void Decide_PlainText_Forwards()
    {
        TerminalTextInputPolicy.Decide("a", ctrlHeld: false)
            .Should().Be(TextInputDecision.Forward);
    }

    [Fact]
    public void Decide_EmptyText_Suppresses()
    {
        TerminalTextInputPolicy.Decide("", ctrlHeld: false)
            .Should().Be(TextInputDecision.Suppress);
        TerminalTextInputPolicy.Decide(null, ctrlHeld: false)
            .Should().Be(TextInputDecision.Suppress);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("hello\r")]
    public void Decide_TextContainingNewline_Suppresses(string text)
    {
        // KeyDown owns Enter; without this guard TextInput would write a
        // duplicate CR/LF and Claude Code would submit twice.
        TerminalTextInputPolicy.Decide(text, ctrlHeld: false)
            .Should().Be(TextInputDecision.Suppress);
    }

    [Theory]
    [InlineData("\x16")] // Ctrl+V
    [InlineData("\x03")] // Ctrl+C
    [InlineData("\x0c")] // Ctrl+L
    [InlineData("\x12")] // Ctrl+R
    [InlineData("a")]    // Ctrl+A
    public void Decide_CtrlModified_Suppresses(string text)
    {
        // Regression: WPF dispatches KeyDown and TextInput as independent routed
        // events. Without this guard, every Ctrl+letter wrote to the session
        // twice — Ctrl+V pasted twice, Ctrl+C interrupted twice.
        TerminalTextInputPolicy.Decide(text, ctrlHeld: true)
            .Should().Be(TextInputDecision.Suppress);
    }

    [Fact]
    public void Decide_AltModifiedTextWithoutCtrl_StillForwards()
    {
        // AltGr-typed characters on European keyboards should pass through.
        TerminalTextInputPolicy.Decide("@", ctrlHeld: false)
            .Should().Be(TextInputDecision.Forward);
    }
}
