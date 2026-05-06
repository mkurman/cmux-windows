namespace Cmux.Core.Terminal;

/// <summary>
/// Decision returned by <see cref="TerminalTextInputPolicy"/> for a TextInput event.
/// </summary>
public enum TextInputDecision
{
    /// <summary>Forward the text to the terminal session.</summary>
    Forward,

    /// <summary>Drop the event — already handled (or will be) by the KeyDown path.</summary>
    Suppress,
}

/// <summary>
/// Pure decision logic for <c>OnTextInput</c> in the terminal control.
///
/// WPF dispatches <c>KeyDown</c> and <c>TextInput</c> as independent routed events.
/// Setting <c>e.Handled = true</c> on the KeyEventArgs does NOT suppress the
/// matching TextInput. Without a guard, every Ctrl-modified key press writes
/// twice — Ctrl+V pastes twice, Ctrl+C interrupts twice, Ctrl+L clears twice.
///
/// This policy centralizes the rule: any text that originates from Enter or a
/// Ctrl-modified keystroke is owned by the KeyDown handler and must be dropped
/// from the TextInput pipeline.
/// </summary>
public static class TerminalTextInputPolicy
{
    /// <param name="text">Text emitted by the WPF TextInput event (may be empty).</param>
    /// <param name="ctrlHeld">Whether the Control modifier is currently held.</param>
    public static TextInputDecision Decide(string? text, bool ctrlHeld)
    {
        if (string.IsNullOrEmpty(text))
            return TextInputDecision.Suppress;

        // Enter is forwarded by KeyDown via the VT sequence "\r"; the trailing
        // CR/LF that TextInput would write would duplicate the submission.
        if (text.Contains('\r') || text.Contains('\n'))
            return TextInputDecision.Suppress;

        // Ctrl-modified text input (Ctrl+V → 0x16, Ctrl+C → 0x03, Ctrl+L → 0x0C, etc.)
        // is fully handled by KeyDown. Forwarding here would write the byte twice.
        if (ctrlHeld)
            return TextInputDecision.Suppress;

        return TextInputDecision.Forward;
    }
}
