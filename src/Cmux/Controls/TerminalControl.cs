using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cmux.Core.Config;
using Cmux.Core.Models;
using Cmux.Core.Terminal;

namespace Cmux.Controls;

/// <summary>
/// WPF control that renders a TerminalBuffer and handles keyboard/mouse input.
/// Uses DrawingVisual for efficient rendering of the terminal cell grid.
/// Features: scrollback, URL detection, search highlights, mouse reporting, visual bell.
/// </summary>
public class TerminalControl : FrameworkElement
{
    private TerminalSession? _session;
    private readonly TerminalSelection _selection = new();
    private GhosttyTheme _theme;
    private DrawingVisual _visual;
    private Typeface _typeface;
    private double _cellWidth;
    private double _cellHeight;
    private double _fontSize;
    private int _cols;
    private int _rows;
    private bool _mouseDown;
    private int _scrollOffset; // Negative = scrolled into history, 0 = at bottom
    private bool _followOutput = true;
    private int _lastScrollbackCount;
    private int _renderQueued;
    private string _cursorStyle = "bar";
    private bool _cursorBlink = true;

    // Cursor blink timer
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _cursorVisible = true;

    // Visual bell
    private DateTime _bellFlashUntil;
    private System.Windows.Threading.DispatcherTimer? _bellTimer;

    // URL detection
    private (int row, int startCol, int endCol, string url)? _hoveredUrl;
    private int _lastUrlRow = -1;
    private List<(int startCol, int endCol, string url)>? _cachedRowUrls;

    // Search highlights
    private List<(int row, int col, int length)> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private HashSet<(int row, int col)>? _searchMatchSetCache;
    private HashSet<(int row, int col)>? _currentMatchSetCache;
    private static readonly HashSet<(int row, int col)> EmptyMatchSet = [];
    private readonly StringBuilder _inputLineBuffer = new();
    private bool _suppressNextEnterToShell;

    // Rendering caches to avoid per-frame allocations
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    private Typeface? _typefaceBold;
    private Typeface? _typefaceItalic;
    private Typeface? _typefaceBoldItalic;
    private readonly StringBuilder _textRunBuffer = new();
    private bool _suppressNextEnterTextInput;

    /// <summary>Fired when the pane wants focus.</summary>
    public event Action? FocusRequested;
    public event Action<string>? CommandSubmitted;
    public event Func<string, bool>? CommandInterceptRequested;
    public event Action? ClearRequested;
    public event Action<SplitDirection>? SplitRequested;
    public event Action? ZoomRequested;
    public event Action? ClosePaneRequested;
    public event Action? SearchRequested;

    /// <summary>Clears all event handlers (called before re-attaching to visual tree).</summary>
    public void ClearEventHandlers()
    {
        FocusRequested = null;
        CommandSubmitted = null;
        CommandInterceptRequested = null;
        ClearRequested = null;
        SplitRequested = null;
        ZoomRequested = null;
        ClosePaneRequested = null;
        SearchRequested = null;
    }

    /// <summary>Whether this pane has notification state (blue ring).</summary>
    public static readonly DependencyProperty HasNotificationProperty =
        DependencyProperty.Register(nameof(HasNotification), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnHasNotificationChanged));

    public bool HasNotification
    {
        get => (bool)GetValue(HasNotificationProperty);
        set => SetValue(HasNotificationProperty, value);
    }

    /// <summary>Whether this pane is focused.</summary>
    public static readonly DependencyProperty IsPaneFocusedProperty =
        DependencyProperty.Register(nameof(IsPaneFocused), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnIsPaneFocusedChanged));

    public bool IsPaneFocused
    {
        get => (bool)GetValue(IsPaneFocusedProperty);
        set => SetValue(IsPaneFocusedProperty, value);
    }

    /// <summary>Whether the parent surface is currently zoomed.</summary>
    public bool IsSurfaceZoomed { get; set; }

    public TerminalControl()
    {
        _theme = GhosttyConfigReader.ReadConfig();
        _visual = new DrawingVisual();
        AddVisualChild(_visual);
        AddLogicalChild(_visual);

        _fontSize = _theme.FontSize;
        _typeface = new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var settings = SettingsService.Current;
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        CalculateCellSize();

        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        AllowDrop = true;

        _selection.SelectionChanged += () => RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        // Cursor blink
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _cursorTimer.Tick += (_, _) =>
        {
            bool wasVisible = _cursorVisible;
            if (!_cursorBlink)
                _cursorVisible = true;
            else
                _cursorVisible = !_cursorVisible;

            if (_cursorVisible != wasVisible)
                RequestRender();
        };
        _cursorTimer.Start();
    }

    public void AttachSession(TerminalSession session)
    {
        if (_session != null)
        {
            _session.Redraw -= OnRedraw;
            _session.BellReceived -= OnBell;
        }

        _session = session;
        _inputLineBuffer.Clear();
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        _session.Redraw += OnRedraw;
        _session.BellReceived += OnBell;
        CalculateTerminalSize();
        Render();
    }

    private void OnRedraw()
    {
        if (_session == null)
            return;

        var currentScrollback = _session.Buffer.ScrollbackCount;
        var scrollbackDelta = currentScrollback - _lastScrollbackCount;

        if (_followOutput || _scrollOffset == 0)
        {
            // Live mode: always stick to bottom.
            _scrollOffset = 0;
            _followOutput = true;
        }
        else if (_scrollOffset < 0 && scrollbackDelta > 0)
        {
            // Freeze viewport while output is streaming.
            _scrollOffset -= scrollbackDelta;
        }

        _scrollOffset = Math.Clamp(_scrollOffset, -currentScrollback, 0);
        if (_scrollOffset == 0)
            _followOutput = true;

        _lastScrollbackCount = currentScrollback;
        RequestRender();
    }

    private void OnBell()
    {
        _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(150);
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        _bellTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170),
        };
        // Restart the timer (handles rapid bell sequences)
        _bellTimer.Stop();
        _bellTimer.Tick -= OnBellTimerTick;
        _bellTimer.Tick += OnBellTimerTick;
        _bellTimer.Start();
    }

    private void OnBellTimerTick(object? sender, EventArgs e)
    {
        _bellTimer?.Stop();
        RequestRender();
    }

    // --- Search support ---

    public void SetSearchHighlights(List<(int row, int col, int length)> matches, int currentIndex)
    {
        _searchMatches = matches;
        _currentSearchMatch = currentIndex;
        RebuildSearchMatchCache();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void ClearSearchHighlights()
    {
        _searchMatches = [];
        _currentSearchMatch = -1;
        _searchMatchSetCache = null;
        _currentMatchSetCache = null;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RebuildSearchMatchCache()
    {
        var matchSet = new HashSet<(int row, int col)>();
        foreach (var (mRow, mCol, mLen) in _searchMatches)
        {
            for (int i = 0; i < mLen; i++)
                matchSet.Add((mRow, mCol + i));
        }
        _searchMatchSetCache = matchSet;

        if (_currentSearchMatch >= 0 && _currentSearchMatch < _searchMatches.Count)
        {
            var curSet = new HashSet<(int row, int col)>();
            var (cmRow, cmCol, cmLen) = _searchMatches[_currentSearchMatch];
            for (int i = 0; i < cmLen; i++)
                curSet.Add((cmRow, cmCol + i));
            _currentMatchSetCache = curSet;
        }
        else
        {
            _currentMatchSetCache = null;
        }
    }

    private void RequestRender(System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Background)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Render();
        }, priority);
    }

    // --- Layout ---

    private void CalculateCellSize()
    {
        var formattedText = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _cellWidth = formattedText.WidthIncludingTrailingWhitespace;
        _cellHeight = formattedText.Height;
    }

    private void CalculateTerminalSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _cellWidth <= 0 || _cellHeight <= 0) return;

        int cols = Math.Max(1, (int)(ActualWidth / _cellWidth));
        int rows = Math.Max(1, (int)(ActualHeight / _cellHeight));

        if (cols != _cols || rows != _rows)
        {
            _cols = cols;
            _rows = rows;
            _session?.Resize(cols, rows);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    // --- Rendering ---

    private SolidColorBrush GetCachedBrush(Color color)
    {
        if (_brushCache.TryGetValue(color, out var brush))
            return brush;

        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushCache[color] = brush;
        return brush;
    }

    private void InvalidateRenderCaches()
    {
        _brushCache.Clear();
        _typefaceBold = null;
        _typefaceItalic = null;
        _typefaceBoldItalic = null;
    }

    private Typeface GetTypeface(bool bold, bool italic)
    {
        if (!bold && !italic) return _typeface;
        if (bold && !italic) return _typefaceBold ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        if (!bold && italic) return _typefaceItalic ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        return _typefaceBoldItalic ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    }

    private void Render()
    {
        if (_session == null) return;

        try
        {
            var buffer = _session.Buffer;
            using var dc = _visual.RenderOpen();
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Background
            var bgColor = ToWpfColor(_theme.Background);
            dc.DrawRectangle(GetCachedBrush(bgColor), null, new Rect(0, 0, ActualWidth, ActualHeight));

            // Visual bell flash
            if (DateTime.UtcNow < _bellFlashUntil)
            {
                dc.DrawRectangle(GetCachedBrush(Color.FromArgb(25, 255, 255, 255)), null,
                    new Rect(0, 0, ActualWidth, ActualHeight));
            }

            // Notification ring
            if (HasNotification)
            {
                var notifPen = new Pen(GetCachedBrush(Color.FromArgb(180, 0x63, 0x66, 0xF1)), 2);
                notifPen.Freeze();
                dc.DrawRoundedRectangle(null, notifPen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 4, 4);
            }

            // Focused pane indicator
            if (IsPaneFocused)
            {
                var focusPen = new Pen(GetCachedBrush(Color.FromArgb(50, 0x81, 0x8C, 0xF8)), 1);
                focusPen.Freeze();
                dc.DrawRectangle(null, focusPen, new Rect(0, 0, ActualWidth, ActualHeight));
            }

            // Calculate scrollback offset
            int scrollbackCount = buffer.ScrollbackCount;
            bool isScrolledBack = _scrollOffset < 0;
            int viewStartLine = scrollbackCount + _scrollOffset;

            // Use cached search match sets (built once in SetSearchHighlights)
            var searchMatchSet = _searchMatchSetCache ?? EmptyMatchSet;
            var currentMatchSet = _currentMatchSetCache ?? EmptyMatchSet;
            var searchMatchBrush = searchMatchSet.Count > 0 ? GetCachedBrush(Color.FromArgb(100, 0xFB, 0xBF, 0x24)) : null;
            var currentMatchBrush = currentMatchSet.Count > 0 ? GetCachedBrush(Color.FromArgb(180, 0xFB, 0x92, 0x3C)) : null;

            // Render visible rows with batched text
            for (int visRow = 0; visRow < _rows; visRow++)
            {
                int virtualLine = viewStartLine + visRow;
                bool isScrollback = virtualLine < scrollbackCount;
                int bufferRow = virtualLine - scrollbackCount;

                TerminalCell[]? scrollbackLine = null;
                if (isScrollback)
                    scrollbackLine = buffer.GetScrollbackLine(virtualLine);

                double y = visRow * _cellHeight;

                // Text run state for batching
                int runStartCol = -1;
                Color runFgColor = default;
                bool runBold = false, runItalic = false, runDim = false;
                bool runUnderline = false, runStrikethrough = false;
                _textRunBuffer.Clear();

                for (int c = 0; c < _cols; c++)
                {
                    TerminalCell cell;
                    if (isScrollback)
                    {
                        cell = (scrollbackLine != null && c < scrollbackLine.Length)
                            ? scrollbackLine[c]
                            : TerminalCell.Empty;
                    }
                    else if (bufferRow >= 0 && bufferRow < buffer.Rows && c < buffer.Cols)
                    {
                        cell = buffer.CellAt(bufferRow, c);
                    }
                    else
                    {
                        cell = TerminalCell.Empty;
                    }

                    double x = c * _cellWidth;
                    var attr = cell.Attribute;
                    bool isSelected = _selection.IsSelected(visRow, c);
                    bool isInverse = attr.Flags.HasFlag(CellFlags.Inverse) != isSelected;

                    // Cell colors
                    TerminalColor cellBg, cellFg;
                    if (isInverse)
                    {
                        cellBg = attr.Foreground.IsDefault ? _theme.Foreground : attr.Foreground;
                        cellFg = attr.Background.IsDefault ? _theme.Background : attr.Background;
                    }
                    else
                    {
                        cellBg = attr.Background;
                        cellFg = attr.Foreground;
                    }

                    if (isSelected && _theme.SelectionBackground.HasValue)
                        cellBg = _theme.SelectionBackground.Value;

                    // Draw cell background
                    if (!cellBg.IsDefault)
                    {
                        dc.DrawRectangle(GetCachedBrush(ToWpfColor(cellBg)), null,
                            new Rect(x, y, _cellWidth, _cellHeight));
                    }

                    // Search match highlight (behind text)
                    bool isSearchMatch = searchMatchSet.Contains((visRow, c));
                    bool isCurrentMatch = currentMatchSet.Contains((visRow, c));
                    if (isCurrentMatch)
                        dc.DrawRectangle(currentMatchBrush, null, new Rect(x, y, _cellWidth, _cellHeight));
                    else if (isSearchMatch)
                        dc.DrawRectangle(searchMatchBrush, null, new Rect(x, y, _cellWidth, _cellHeight));

                    // URL hover highlight
                    if (_hoveredUrl is { } url && visRow == url.row && c >= url.startCol && c <= url.endCol)
                    {
                        var urlPen = new Pen(GetCachedBrush(Color.FromRgb(0x81, 0x8C, 0xF8)), 1);
                        urlPen.Freeze();
                        dc.DrawLine(urlPen, new Point(x, y + _cellHeight - 1), new Point(x + _cellWidth, y + _cellHeight - 1));
                    }

                    // Text batching: group consecutive characters with same visual style
                    bool hasChar = cell.Character != '\0' && cell.Character != ' ';
                    if (hasChar)
                    {
                        var fgColor = cellFg.IsDefault ? ToWpfColor(_theme.Foreground) : ToWpfColor(cellFg);
                        bool bold = attr.Flags.HasFlag(CellFlags.Bold);
                        bool italic = attr.Flags.HasFlag(CellFlags.Italic);
                        bool dim = attr.Flags.HasFlag(CellFlags.Dim);
                        bool underline = attr.Flags.HasFlag(CellFlags.Underline);
                        bool strikethrough = attr.Flags.HasFlag(CellFlags.Strikethrough);

                        // Style changed? Flush the current run first
                        if (runStartCol >= 0 && (fgColor != runFgColor || bold != runBold ||
                            italic != runItalic || dim != runDim ||
                            underline != runUnderline || strikethrough != runStrikethrough))
                        {
                            FlushTextRun(dc, dpi, y, runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough);
                            runStartCol = -1;
                        }

                        // Start new run or continue existing
                        if (runStartCol < 0)
                        {
                            runStartCol = c;
                            runFgColor = fgColor;
                            runBold = bold;
                            runItalic = italic;
                            runDim = dim;
                            runUnderline = underline;
                            runStrikethrough = strikethrough;
                            _textRunBuffer.Clear();
                        }

                        _textRunBuffer.Append(cell.Character);
                    }
                    else if (runStartCol >= 0)
                    {
                        // Empty cell — flush the current run
                        FlushTextRun(dc, dpi, y, runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough);
                        runStartCol = -1;
                    }
                }

                // Flush final run for this row
                if (runStartCol >= 0)
                    FlushTextRun(dc, dpi, y, runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough);
            }

            // Cursor (only when viewing live buffer)
            if (!isScrolledBack && buffer.CursorVisible && IsPaneFocused && (_cursorVisible || !_cursorBlink))
            {
                double cx = buffer.CursorCol * _cellWidth;
                double cy = buffer.CursorRow * _cellHeight;
                var cursorColor = _theme.CursorColor.HasValue
                    ? ToWpfColor(_theme.CursorColor.Value)
                    : ToWpfColor(_theme.Foreground);
                var cursorBrush = GetCachedBrush(Color.FromArgb(200, cursorColor.R, cursorColor.G, cursorColor.B));

                switch ((_cursorStyle ?? "bar").ToLowerInvariant())
                {
                    case "block":
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, _cellWidth, _cellHeight));
                        break;
                    case "underline":
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy + _cellHeight - 2, _cellWidth, 2));
                        break;
                    default:
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, 2, _cellHeight));
                        break;
                }
            }

            // Scrollback indicator
            if (isScrolledBack)
            {
                int linesBack = -_scrollOffset;
                string indicator = $"[{linesBack}/{scrollbackCount}]";
                var indicatorText = new FormattedText(
                    indicator,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    10,
                    GetCachedBrush(Color.FromArgb(160, 0x81, 0x8C, 0xF8)),
                    dpi);
                double iw = indicatorText.WidthIncludingTrailingWhitespace + 12;
                double ih = indicatorText.Height + 4;
                double ix = ActualWidth - iw - 8;
                dc.DrawRoundedRectangle(
                    GetCachedBrush(Color.FromArgb(200, 0x14, 0x14, 0x14)), null,
                    new Rect(ix, 6, iw, ih), 4, 4);
                dc.DrawText(indicatorText, new Point(ix + 6, 8));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalControl] Render failed: {ex}");
        }
    }

    /// <summary>
    /// Draws a batched text run and its decorations (underline/strikethrough).
    /// </summary>
    private void FlushTextRun(DrawingContext dc, double dpi, double y, int startCol,
        Color fgColor, bool bold, bool italic, bool dim, bool underline, bool strikethrough)
    {
        if (_textRunBuffer.Length == 0) return;

        var brush = dim
            ? GetCachedBrush(Color.FromArgb(128, fgColor.R, fgColor.G, fgColor.B))
            : GetCachedBrush(fgColor);
        var tf = GetTypeface(bold, italic);
        var text = new FormattedText(
            _textRunBuffer.ToString(),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            tf,
            _fontSize,
            brush,
            dpi);

        double x = startCol * _cellWidth;
        dc.DrawText(text, new Point(x, y));

        double runWidth = _textRunBuffer.Length * _cellWidth;

        if (underline)
        {
            var pen = new Pen(brush, 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + runWidth, y + _cellHeight - 1));
        }

        if (strikethrough)
        {
            var pen = new Pen(brush, 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, y + _cellHeight / 2), new Point(x + runWidth, y + _cellHeight / 2));
        }
    }

    private static Color ToWpfColor(TerminalColor c) =>
        c.IsDefault ? Colors.Transparent : Color.FromRgb(c.R, c.G, c.B);

    // --- Mouse reporting ---

    private bool IsMouseTrackingActive =>
        _session?.Buffer.MouseEnabled == true;

    private void SendMouseReport(int button, int col, int row, bool press)
    {
        if (_session == null) return;
        var buf = _session.Buffer;
        if (!buf.MouseEnabled) return;

        col = Math.Clamp(col, 0, buf.Cols - 1);
        row = Math.Clamp(row, 0, buf.Rows - 1);

        if (buf.MouseSgrExtended)
        {
            char suffix = press ? 'M' : 'm';
            _session.Write($"\x1b[<{button};{col + 1};{row + 1}{suffix}");
        }
        else if (press)
        {
            char cb = (char)(button + 32);
            char cx = (char)(col + 33);
            char cy = (char)(row + 33);
            _session.Write($"\x1b[M{cb}{cx}{cy}");
        }
    }

    // --- Keyboard input ---

    private void EnsureLiveView()
    {
        if (_session == null)
            return;

        if (_scrollOffset == 0 && _followOutput)
            return;

        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void TrackInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\b':
                    if (_inputLineBuffer.Length > 0)
                        _inputLineBuffer.Length--;
                    break;

                case '\r':
                case '\n':
                    SubmitBufferedCommand(allowInterception: false);
                    break;

                default:
                    if (!char.IsControl(ch))
                    {
                        _inputLineBuffer.Append(ch);

                        if (_inputLineBuffer.Length > 4096)
                            _inputLineBuffer.Remove(0, _inputLineBuffer.Length - 4096);
                    }
                    break;
            }
        }
    }

    private void SubmitBufferedCommand(bool allowInterception)
    {
        var rawCommand = _inputLineBuffer.ToString();
        var command = rawCommand.Trim();
        _inputLineBuffer.Clear();

        if (string.IsNullOrWhiteSpace(command))
            return;

        if (allowInterception && TryInterceptCommand(command))
        {
            _suppressNextEnterToShell = true;
            _suppressNextEnterTextInput = true;

            // The command text has already been sent character-by-character to the shell.
            // Cancel the current input line so a subsequent newline from agent output
            // cannot execute the intercepted handler command.
            if (_session != null)
                _session.Write("\x03");
            return;
        }

        CommandSubmitted?.Invoke(command);
    }

    private bool TryInterceptCommand(string command)
    {
        var handlers = CommandInterceptRequested;
        if (handlers == null)
            return false;

        foreach (var callback in handlers.GetInvocationList().OfType<Func<string, bool>>())
        {
            try
            {
                if (callback(command))
                    return true;
            }
            catch
            {
                // Ignore handler failures to avoid breaking terminal input.
            }
        }

        return false;
    }

    private bool CopySelectionToClipboard()
    {
        if (_session == null || !_selection.HasSelection)
            return false;

        var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
        if (string.IsNullOrEmpty(text))
            return false;

        Clipboard.SetText(text);
        _selection.ClearSelection();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);

        // Let application-level shortcuts bubble to MainWindow.
        // Ctrl+Alt combos (pane focus), Ctrl+Tab (surface cycling),
        // and Ctrl+Shift combos (split, zoom, search, etc.) are app-level.
        if (ctrl && alt) return;
        if (ctrl && shift) return;
        if (ctrl && e.Key == Key.Tab) return;

        // Ctrl+Backspace: delete previous word (send Ctrl+W / unix-word-rubout)
        if (ctrl && e.Key == Key.Back)
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write("\x17");
            e.Handled = true;
            return;
        }

        // Terminal shortcuts
        if (ctrl && e.Key == Key.C)
        {
            if (!CopySelectionToClipboard())
            {
                // Forward Ctrl+C to shell as interrupt when no selection is active.
                _inputLineBuffer.Clear();
                EnsureLiveView();
                _session.Write("\x03");
            }

            e.Handled = true;
            return;
        }

        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Insert)
        {
            _ = CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        // Forward Ctrl+letter as control bytes (e.g. Ctrl+X => 0x18) for TUI apps like nano.
        if (ctrl && !modifiers.HasFlag(ModifierKeys.Alt) && TryGetCtrlLetterSequence(e.Key, out var ctrlSequence))
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write(ctrlSequence);
            e.Handled = true;
            return;
        }

        bool appCursor = _session.Buffer.ApplicationCursorKeys;
        string? sequence = KeyToVtSequence(e.Key, modifiers, appCursor);
        if (sequence != null)
        {
            if (e.Key == Key.Back)
                TrackInputText("\b");
            else if (e.Key == Key.Enter)
            {
                SubmitBufferedCommand(allowInterception: true);
                if (_suppressNextEnterToShell)
                {
                    _suppressNextEnterToShell = false;
                    e.Handled = true;
                    return;
                }
            }

            EnsureLiveView();
            _session.Write(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        // KeyDown handles Enter; suppress the trailing TextInput CR/LF when
        // an intercepted command consumed the shell submission.
        if (_suppressNextEnterTextInput && (e.Text.Contains('\r') || e.Text.Contains('\n')))
        {
            _suppressNextEnterTextInput = false;
            e.Handled = true;
            return;
        }

        // Prevent duplicate newline writes from TextInput path.
        if (e.Text.Contains('\r') || e.Text.Contains('\n'))
        {
            e.Handled = true;
            return;
        }

        // Handle Ctrl+C (copy when selection exists)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x03")
        {
            if (_selection.HasSelection)
            {
                var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
                _selection.ClearSelection();
                return;
            }
        }

        // Handle Ctrl+V (paste)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x16")
        {
            PasteFromClipboard();
            return;
        }

        EnsureLiveView();
        TrackInputText(e.Text);
        _session.Write(e.Text);
        _selection.ClearSelection();
    }

    private void PasteFromClipboard()
    {
        if (_session == null) return;
        if (!TryGetClipboardPasteText(out var text)) return;

        PasteText(text);
    }

    private void PasteText(string text)
    {
        if (_session == null || string.IsNullOrEmpty(text)) return;

        EnsureLiveView();
        TrackInputText(text);

        if (_session.Buffer.BracketedPasteMode)
            _session.Write("\x1b[200~" + text + "\x1b[201~");
        else
            _session.Write(text);
    }

    private static bool HasClipboardPasteContent()
    {
        try
        {
            return Clipboard.ContainsText()
                || Clipboard.ContainsFileDropList()
                || Clipboard.ContainsImage();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClipboardPasteText(out string text)
    {
        text = string.Empty;

        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
                return !string.IsNullOrEmpty(text);
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var paths = files.Cast<string>()
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();

                if (paths.Length > 0)
                {
                    text = string.Join(" ", paths.Select(QuotePathForShell));
                    return true;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    var tempPath = SaveBitmapSourceToTempFile(image);
                    if (!string.IsNullOrWhiteSpace(tempPath))
                    {
                        text = QuotePathForShell(tempPath);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore clipboard race/format exceptions and treat as unavailable.
        }

        return false;
    }

    private static string? SaveBitmapSourceToTempFile(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "cmux", "clipboard-images");
            Directory.CreateDirectory(dir);

            var fileName = $"cmux-clipboard-{DateTime.Now:yyyyMMdd-HHmmssfff}.png";
            var fullPath = Path.Combine(dir, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var stream = File.Create(fullPath);
            encoder.Save(stream);

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    private static string QuotePathForShell(string path)
    {
        if (path.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0)
            return path;

        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static bool HasDropContent(IDataObject? data)
    {
        if (data == null)
            return false;

        try
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent("PNG");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDropPasteText(IDataObject? data, out string text)
    {
        text = string.Empty;
        if (data == null)
            return false;

        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                text = string.Join(" ", files
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(QuotePathForShell));
                return !string.IsNullOrWhiteSpace(text);
            }

            if (data.GetDataPresent(DataFormats.UnicodeText) &&
                data.GetData(DataFormats.UnicodeText) is string unicodeText &&
                !string.IsNullOrEmpty(unicodeText))
            {
                text = unicodeText;
                return true;
            }

            if (data.GetDataPresent(DataFormats.Text) &&
                data.GetData(DataFormats.Text) is string plainText &&
                !string.IsNullOrEmpty(plainText))
            {
                text = plainText;
                return true;
            }

            if (TryGetDropBitmapSource(data, out var bitmap))
            {
                var tempPath = SaveBitmapSourceToTempFile(bitmap);
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    text = QuotePathForShell(tempPath);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore drag-data conversion failures.
        }

        return false;
    }

    private static bool TryGetDropBitmapSource(IDataObject data, out BitmapSource bitmap)
    {
        bitmap = null!;

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var value = data.GetData(DataFormats.Bitmap);
            if (value is BitmapSource bitmapSource)
            {
                bitmap = bitmapSource;
                return true;
            }
        }

        if (data.GetDataPresent("PNG"))
        {
            var value = data.GetData("PNG");
            if (value is MemoryStream memoryStream)
            {
                memoryStream.Position = 0;
                var frame = BitmapFrame.Create(memoryStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }

            if (value is byte[] bytes && bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }
        }

        return false;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        Focus();
        FocusRequested?.Invoke();

        if (_session != null && TryGetDropPasteText(e.Data, out var text))
        {
            PasteText(text);
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    // --- Mouse input ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        FocusRequested?.Invoke();

        if (_cols <= 0 || _rows <= 0) return;

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);

        // Ctrl+Click for URL opening
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _hoveredUrl.HasValue)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_hoveredUrl.Value.url) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
            return;
        }

        // Mouse reporting (bypass selection when app requests mouse)
        if (IsMouseTrackingActive)
        {
            SendMouseReport(0, col, row, true);
            _mouseDown = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && _session != null)
        {
            _selection.SelectWord(_session.Buffer, row, col, _scrollOffset);
        }
        else if (e.ClickCount == 3 && _session != null)
        {
            _selection.SelectLine(row, _session.Buffer.Cols);
        }
        else
        {
            _selection.StartSelection(row, col);
            _mouseDown = true;
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_cols <= 0 || _rows <= 0) return;

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);

        // URL detection (Ctrl held) — cache scanned URLs per row
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _session != null && row < _session.Buffer.Rows)
        {
            // Only re-scan when the row changes
            if (row != _lastUrlRow)
            {
                _lastUrlRow = row;
                var lineText = UrlDetector.GetRowText(_session.Buffer, row);
                _cachedRowUrls = UrlDetector.FindUrls(lineText);
            }

            // Check cached URLs for hit at current column
            var oldHover = _hoveredUrl;
            _hoveredUrl = null;
            if (_cachedRowUrls != null)
            {
                foreach (var (startCol, endCol, url) in _cachedRowUrls)
                {
                    if (col >= startCol && col <= endCol)
                    {
                        _hoveredUrl = (row, startCol, endCol, url);
                        break;
                    }
                }
            }

            Cursor = _hoveredUrl.HasValue ? Cursors.Hand : Cursors.Arrow;
            if (_hoveredUrl != oldHover)
                RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }
        else if (_hoveredUrl.HasValue)
        {
            _hoveredUrl = null;
            _lastUrlRow = -1;
            _cachedRowUrls = null;
            Cursor = Cursors.Arrow;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }

        // Mouse reporting (motion events)
        if (IsMouseTrackingActive && _mouseDown)
        {
            var buf = _session!.Buffer;
            if (buf.MouseTrackingButton || buf.MouseTrackingAny)
            {
                SendMouseReport(32, col, row, true); // 32 = motion flag
            }
            return;
        }
        if (IsMouseTrackingActive && _session!.Buffer.MouseTrackingAny)
        {
            SendMouseReport(35, col, row, true); // 35 = no-button motion
            return;
        }

        // Selection drag
        if (_mouseDown && !IsMouseTrackingActive)
        {
            _selection.ExtendSelection(row, col);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (IsMouseTrackingActive && _mouseDown && _cols > 0 && _rows > 0)
        {
            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(0, col, row, false);
        }

        if (_mouseDown)
        {
            _mouseDown = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(2, col, row, true);
            return;
        }

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };

        var menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE9))));
        menuItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        menuItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        menuItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));

        var separatorStyle = new Style(typeof(Separator));
        separatorStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C))));
        separatorStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2)));

        menu.Resources.Add(typeof(MenuItem), menuItemStyle);
        menu.Resources.Add(typeof(Separator), separatorStyle);

        // Copy
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copyItem.Icon = MakeIcon("\uE8C8");
        copyItem.IsEnabled = _selection.HasSelection;
        copyItem.Click += (_, _) =>
        {
            if (_session != null)
            {
                var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                _selection.ClearSelection();
            }
        };
        menu.Items.Add(copyItem);

        // Paste
        var pasteItem = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V" };
        pasteItem.Icon = MakeIcon("\uE77F");
        pasteItem.IsEnabled = HasClipboardPasteContent();
        pasteItem.Click += (_, _) => PasteFromClipboard();
        menu.Items.Add(pasteItem);

        // Select All
        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Icon = MakeIcon("\uE8B3");
        selectAllItem.Click += (_, _) =>
        {
            if (_session != null)
                _selection.SelectAll(_session.Buffer.Rows, _session.Buffer.Cols);
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new Separator());

        // Split Right
        var splitRight = new MenuItem { Header = "Split Right", InputGestureText = "Ctrl+D" };
        splitRight.Icon = MakeIcon("\uE745");
        splitRight.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Vertical);
        menu.Items.Add(splitRight);

        // Split Down
        var splitDown = new MenuItem { Header = "Split Down", InputGestureText = "Ctrl+Shift+D" };
        splitDown.Icon = MakeIcon("\uE74B");
        splitDown.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Horizontal);
        menu.Items.Add(splitDown);

        menu.Items.Add(new Separator());

        // Zoom
        var isZoomed = IsSurfaceZoomed;
        var zoom = new MenuItem
        {
            Header = isZoomed ? "Unzoom Pane" : "Zoom Pane",
            InputGestureText = "Ctrl+Shift+Z",
            IsCheckable = true,
            IsChecked = isZoomed,
        };
        zoom.Icon = MakeIcon(isZoomed ? "\uE73F" : "\uE740");
        zoom.Click += (_, _) => ZoomRequested?.Invoke();
        menu.Items.Add(zoom);

        // Close Pane
        var closePane = new MenuItem { Header = "Close Pane" };
        closePane.Icon = MakeIcon("\uE711");
        closePane.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        closePane.Click += (_, _) => ClosePaneRequested?.Invoke();
        menu.Items.Add(closePane);

        menu.Items.Add(new Separator());

        // Clear Terminal
        var clear = new MenuItem { Header = "Clear Terminal" };
        clear.Icon = MakeIcon("\uE894");
        clear.Click += (_, _) =>
        {
            ClearRequested?.Invoke();
            ClearTerminalView();
        };
        menu.Items.Add(clear);

        // Search
        var search = new MenuItem { Header = "Search", InputGestureText = "Ctrl+Shift+F" };
        search.Icon = MakeIcon("\uE721");
        search.Click += (_, _) => SearchRequested?.Invoke();
        menu.Items.Add(search);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static TextBlock MakeIcon(string glyph) =>
        new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 };

    private void ClearTerminalView()
    {
        if (_session == null) return;

        _session.Buffer.EraseInDisplay(3);
        _session.Buffer.MoveCursorTo(0, 0);
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        // Ask shell to repaint prompt where supported.
        _session.Write("\x0c");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;

        // Mouse wheel reporting
        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            int button = e.Delta > 0 ? 64 : 65; // 64 = scroll up, 65 = scroll down
            SendMouseReport(button, col, row, true);
            e.Handled = true;
            return;
        }

        // Scrollback navigation
        int lines = e.Delta > 0 ? -3 : 3;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, -_session.Buffer.ScrollbackCount, 0);
        _followOutput = _scrollOffset == 0;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        e.Handled = true;
    }

    // --- Visual tree ---

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    private static bool TryGetCtrlLetterSequence(Key key, out string sequence)
    {
        sequence = "";
        if (key < Key.A || key > Key.Z)
            return false;

        var controlCode = (char)(key - Key.A + 1);
        sequence = controlCode.ToString();
        return true;
    }

    private static string? KeyToVtSequence(Key key, ModifierKeys modifiers, bool appCursor)
    {
        if (appCursor)
        {
            var appSeq = key switch
            {
                Key.Up => "\x1bOA",
                Key.Down => "\x1bOB",
                Key.Right => "\x1bOC",
                Key.Left => "\x1bOD",
                Key.Home => "\x1bOH",
                Key.End => "\x1bOF",
                _ => (string?)null,
            };
            if (appSeq != null) return appSeq;
        }

        return key switch
        {
            Key.Enter => "\r",
            Key.Escape => "\x1b",
            Key.Back => "\x7f",
            Key.Tab => modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
    }

    public void UpdateTheme(GhosttyTheme theme)
    {
        _theme = theme;
        _typeface = new Typeface(new FontFamily(theme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = theme.FontSize;
        InvalidateRenderCaches();
        CalculateCellSize();
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void UpdateSettings(TerminalTheme theme, string fontFamily, int fontSize)
    {
        // Convert TerminalTheme to GhosttyTheme
        var ghosttyTheme = new GhosttyTheme
        {
            Background = theme.Background,
            Foreground = theme.Foreground,
            Palette = theme.Palette,
            SelectionBackground = theme.SelectionBg,
            CursorColor = theme.CursorColor,
            FontFamily = fontFamily,
            FontSize = fontSize
        };
        UpdateSettings(ghosttyTheme, fontFamily, fontSize);
    }

    public void UpdateSettings(GhosttyTheme theme, string fontFamily, int fontSize)
    {
        _theme = theme;
        _fontSize = fontSize;

        var settings = SettingsService.Current;
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        _typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        InvalidateRenderCaches();
        CalculateCellSize();
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnHasNotificationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TerminalControl)d).RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnIsPaneFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (TerminalControl)d;
        if ((bool)e.NewValue)
        {
            ctrl._cursorVisible = true;
            if (ctrl._cursorBlink)
                ctrl._cursorTimer?.Start();
        }
        ctrl.RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }
}
