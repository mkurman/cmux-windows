namespace Cmux.Core.Terminal;

/// <summary>
/// Pure scrollback offset math, shared by mouse-wheel and PageUp/PageDown
/// scrolling. Keeps the clamping rules in one tested place.
/// </summary>
public static class ScrollbackNavigator
{
    /// <summary>
    /// Returns the new scroll offset after scrolling by <paramref name="lineDelta"/>.
    /// Negative offset = scrolled into history; 0 = at the bottom (live view).
    /// Positive <paramref name="lineDelta"/> means "scroll down toward the
    /// live view"; negative means "scroll up into history".
    /// </summary>
    public static int Scroll(int currentOffset, int lineDelta, int scrollbackCount)
    {
        int next = currentOffset + lineDelta;
        if (scrollbackCount < 0) scrollbackCount = 0;
        if (next > 0) next = 0;
        if (next < -scrollbackCount) next = -scrollbackCount;
        return next;
    }

    /// <summary>
    /// Convenience: a "page" worth of scroll for a viewport of
    /// <paramref name="visibleRows"/>, leaving one row of overlap so the user
    /// doesn't lose context.
    /// </summary>
    public static int PageStep(int visibleRows)
    {
        if (visibleRows <= 1) return 1;
        return visibleRows - 1;
    }
}
