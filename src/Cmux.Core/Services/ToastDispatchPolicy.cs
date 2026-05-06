namespace Cmux.Core.Services;

/// <summary>
/// Decides whether a <see cref="Cmux.Core.Models.TerminalNotification"/> should
/// fire a Windows toast given the current settings and window-focus state.
///
/// Extracted so the gating rules can be unit-tested without standing up the
/// WPF app — and so the behavior surface is explicit instead of buried in
/// <c>App.xaml.cs</c>.
/// </summary>
public static class ToastDispatchPolicy
{
    /// <param name="toastsEnabled">User has the system-toast feature on.</param>
    /// <param name="showWhileFocused">User has opted in to toasts even when cmux is in front.</param>
    /// <param name="mainWindowFocused">Cmux's main window is the foreground window right now.</param>
    public static bool ShouldDispatch(bool toastsEnabled, bool showWhileFocused, bool mainWindowFocused)
    {
        if (!toastsEnabled) return false;
        if (showWhileFocused) return true;
        return !mainWindowFocused;
    }
}
