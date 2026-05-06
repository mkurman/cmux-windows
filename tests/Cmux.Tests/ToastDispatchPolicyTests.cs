using Cmux.Core.Services;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class ToastDispatchPolicyTests
{
    [Fact]
    public void Decide_ToastsDisabled_DropsEvenWhenUnfocused()
    {
        ToastDispatchPolicy.ShouldDispatch(
            toastsEnabled: false,
            showWhileFocused: false,
            mainWindowFocused: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Decide_DefaultBehavior_OnlyFiresWhenUnfocused()
    {
        // Default: toasts enabled, suppressed while focused. Matches the
        // original behavior so users aren't surprised by a stream of OS-level
        // popups while actively using cmux.
        ToastDispatchPolicy.ShouldDispatch(toastsEnabled: true, showWhileFocused: false, mainWindowFocused: true)
            .Should().BeFalse();
        ToastDispatchPolicy.ShouldDispatch(toastsEnabled: true, showWhileFocused: false, mainWindowFocused: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Decide_ShowWhileFocusedToggle_FiresEvenWhenFocused()
    {
        // The new toggle the user asked for: forward OSC 9 / notify_send-style
        // events to the OS toast surface even when cmux itself is focused, so
        // the user can rely on a single notification channel.
        ToastDispatchPolicy.ShouldDispatch(toastsEnabled: true, showWhileFocused: true, mainWindowFocused: true)
            .Should().BeTrue();
        ToastDispatchPolicy.ShouldDispatch(toastsEnabled: true, showWhileFocused: true, mainWindowFocused: false)
            .Should().BeTrue();
    }
}
