using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    // Cursor blink timer
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _cursorVisible = true;

    // Visual bell
    private DateTime _bellFlashUntil;

    // URL detection
    private (int row, int startCol, int endCol, string url)? _hoveredUrl;

    // Search highlights
    private List<(int row, int col, int length)> _searchMatches = [];
    private int _currentSearchMatch = -1;

    /// <summary>Fired when the pane wants focus.</summary>
    public event Action? FocusRequested;

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

    public TerminalControl()
    {
        _theme = GhosttyConfigReader.ReadConfig();
        _visual = new DrawingVisual();
        AddVisualChild(_visual);
        AddLogicalChild(_visual);

        _fontSize = _theme.FontSize;
        _typeface = new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        CalculateCellSize();

        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;

        _selection.SelectionChanged += () => Dispatcher.Invoke(Render);

        // Cursor blink
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            Render();
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
        _session.Redraw += OnRedraw;
        _session.BellReceived += OnBell;
        CalculateTerminalSize();
        Render();
    }

    private void OnRedraw()
    {
        _scrollOffset = 0; // Auto-scroll to bottom on new output
        Dispatcher.BeginInvoke(Render);
    }

    private void OnBell()
    {
        _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(150);
        Dispatcher.BeginInvoke(() =>
        {
            Render();
            // Schedule cleanup render after flash expires
            Dispatcher.BeginInvoke(() =>
            {
                if (DateTime.UtcNow >= _bellFlashUntil) Render();
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    // --- Search support ---

    public void SetSearchHighlights(List<(int row, int col, int length)> matches, int currentIndex)
    {
        _searchMatches = matches;
        _currentSearchMatch = currentIndex;
        Render();
    }

    public void ClearSearchHighlights()
    {
        _searchMatches = [];
        _currentSearchMatch = -1;
        Render();
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
        Render();
    }

    // --- Rendering ---

    private void Render()
    {
        if (_session == null) return;

        var buffer = _session.Buffer;
        using var dc = _visual.RenderOpen();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Background
        var bgColor = ToWpfColor(_theme.Background);
        dc.DrawRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Visual bell flash
        if (DateTime.UtcNow < _bellFlashUntil)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));
        }

        // Notification ring
        if (HasNotification)
        {
            var notifPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0x63, 0x66, 0xF1)), 2);
            dc.DrawRoundedRectangle(null, notifPen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 4, 4);
        }

        // Focused pane indicator
        if (IsPaneFocused)
        {
            var focusPen = new Pen(new SolidColorBrush(Color.FromArgb(50, 0x81, 0x8C, 0xF8)), 1);
            dc.DrawRectangle(null, focusPen, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        // Calculate scrollback offset
        int scrollbackCount = buffer.ScrollbackCount;
        bool isScrolledBack = _scrollOffset < 0;
        int viewStartLine = scrollbackCount + _scrollOffset; // index into virtual line space

        // Build search match set for fast lookup
        var searchMatchSet = new HashSet<(int row, int col)>();
        var currentMatchSet = new HashSet<(int row, int col)>();
        foreach (var (mRow, mCol, mLen) in _searchMatches)
        {
            for (int i = 0; i < mLen; i++)
                searchMatchSet.Add((mRow, mCol + i));
        }
        if (_currentSearchMatch >= 0 && _currentSearchMatch < _searchMatches.Count)
        {
            var (cmRow, cmCol, cmLen) = _searchMatches[_currentSearchMatch];
            for (int i = 0; i < cmLen; i++)
                currentMatchSet.Add((cmRow, cmCol + i));
        }

        // Render visible rows
        for (int visRow = 0; visRow < _rows; visRow++)
        {
            int virtualLine = viewStartLine + visRow;
            bool isScrollback = virtualLine < scrollbackCount;
            int bufferRow = virtualLine - scrollbackCount;

            TerminalCell[]? scrollbackLine = null;
            if (isScrollback)
                scrollbackLine = buffer.GetScrollbackLine(virtualLine);

            for (int c = 0; c < _cols; c++)
            {
                TerminalCell cell;
                if (isScrollback)
                {
                    if (scrollbackLine != null && c < scrollbackLine.Length)
                        cell = scrollbackLine[c];
                    else
                        cell = TerminalCell.Empty;
                }
                else if (bufferRow >= 0 && bufferRow < buffer.Rows)
                {
                    cell = buffer.CellAt(bufferRow, c);
                }
                else
                {
                    cell = TerminalCell.Empty;
                }

                double x = c * _cellWidth;
                double y = visRow * _cellHeight;

                var attr = cell.Attribute;
                bool isSelected = _selection.IsSelected(visRow, c);
                bool isInverse = attr.Flags.HasFlag(CellFlags.Inverse) != isSelected;

                // Search highlights
                bool isSearchMatch = searchMatchSet.Contains((visRow, c));
                bool isCurrentMatch = currentMatchSet.Contains((visRow, c));

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
                    dc.DrawRectangle(new SolidColorBrush(ToWpfColor(cellBg)), null,
                        new Rect(x, y, _cellWidth, _cellHeight));
                }

                // Search match highlight (behind text)
                if (isCurrentMatch)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0xFB, 0x92, 0x3C)), null,
                        new Rect(x, y, _cellWidth, _cellHeight));
                }
                else if (isSearchMatch)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 0xFB, 0xBF, 0x24)), null,
                        new Rect(x, y, _cellWidth, _cellHeight));
                }

                // URL hover highlight
                if (_hoveredUrl is { } url && visRow == url.row && c >= url.startCol && c <= url.endCol)
                {
                    // Blue underline for hovered URL
                    var urlPen = new Pen(new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)), 1);
                    dc.DrawLine(urlPen, new Point(x, y + _cellHeight - 1), new Point(x + _cellWidth, y + _cellHeight - 1));
                }

                // Cell character
                if (!string.IsNullOrEmpty(cell.Character) && cell.Character != " ")
                {
                    var fgColor = cellFg.IsDefault ? ToWpfColor(_theme.Foreground) : ToWpfColor(cellFg);

                    var weight = attr.Flags.HasFlag(CellFlags.Bold) ? FontWeights.Bold : FontWeights.Normal;
                    var style = attr.Flags.HasFlag(CellFlags.Italic) ? FontStyles.Italic : FontStyles.Normal;
                    var tf = new Typeface(new FontFamily(_theme.FontFamily), style, weight, FontStretches.Normal);

                    var brush = new SolidColorBrush(fgColor);
                    if (attr.Flags.HasFlag(CellFlags.Dim))
                        brush.Opacity = 0.5;

                    var text = new FormattedText(
                        cell.Character,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        tf,
                        _fontSize,
                        brush,
                        dpi);

                    dc.DrawText(text, new Point(x, y));

                    // Underline
                    if (attr.Flags.HasFlag(CellFlags.Underline))
                    {
                        var pen = new Pen(brush, 1);
                        dc.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + _cellWidth, y + _cellHeight - 1));
                    }

                    // Strikethrough
                    if (attr.Flags.HasFlag(CellFlags.Strikethrough))
                    {
                        var pen = new Pen(brush, 1);
                        dc.DrawLine(pen, new Point(x, y + _cellHeight / 2), new Point(x + _cellWidth, y + _cellHeight / 2));
                    }
                }
            }
        }

        // Cursor (only when viewing live buffer)
        if (!isScrolledBack && buffer.CursorVisible && _cursorVisible && IsPaneFocused)
        {
            double cx = buffer.CursorCol * _cellWidth;
            double cy = buffer.CursorRow * _cellHeight;
            var cursorColor = _theme.CursorColor.HasValue
                ? ToWpfColor(_theme.CursorColor.Value)
                : ToWpfColor(_theme.Foreground);
            var cursorBrush = new SolidColorBrush(Color.FromArgb(180, cursorColor.R, cursorColor.G, cursorColor.B));
            // Bar cursor (thin vertical line) when focused
            dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, 2, _cellHeight));
        }
        else if (!isScrolledBack && buffer.CursorVisible && IsPaneFocused)
        {
            // Hollow block when not blinking visible
            double cx = buffer.CursorCol * _cellWidth;
            double cy = buffer.CursorRow * _cellHeight;
            var cursorColor = _theme.CursorColor.HasValue
                ? ToWpfColor(_theme.CursorColor.Value)
                : ToWpfColor(_theme.Foreground);
            var cursorPen = new Pen(new SolidColorBrush(Color.FromArgb(120, cursorColor.R, cursorColor.G, cursorColor.B)), 1);
            dc.DrawRectangle(null, cursorPen, new Rect(cx, cy, _cellWidth, _cellHeight));
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
                new SolidColorBrush(Color.FromArgb(160, 0x81, 0x8C, 0xF8)),
                dpi);
            // Background pill for indicator
            double iw = indicatorText.WidthIncludingTrailingWhitespace + 12;
            double ih = indicatorText.Height + 4;
            double ix = ActualWidth - iw - 8;
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(200, 0x14, 0x14, 0x14)), null,
                new Rect(ix, 6, iw, ih), 4, 4);
            dc.DrawText(indicatorText, new Point(ix + 6, 8));
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        // Don't intercept system shortcuts
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            (e.Key is Key.N or Key.T or Key.W or Key.B or Key.D or Key.I))
            return;

        bool appCursor = _session.Buffer.ApplicationCursorKeys;
        string? sequence = KeyToVtSequence(e.Key, Keyboard.Modifiers, appCursor);
        if (sequence != null)
        {
            _session.Write(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        // Handle Ctrl+C (copy when selection exists)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x03")
        {
            if (_selection.HasSelection)
            {
                var text = _selection.GetSelectedText(_session.Buffer);
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

        _session.Write(e.Text);
        _selection.ClearSelection();
    }

    private void PasteFromClipboard()
    {
        if (_session == null || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        if (_session.Buffer.BracketedPasteMode)
            _session.Write("\x1b[200~" + text + "\x1b[201~");
        else
            _session.Write(text);
    }

    // --- Mouse input ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        FocusRequested?.Invoke();

        var pos = e.GetPosition(this);
        int col = (int)(pos.X / _cellWidth);
        int row = (int)(pos.Y / _cellHeight);

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
            _selection.SelectWord(_session.Buffer, row, col);
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

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);

        // URL detection (Ctrl held)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _session != null && row < _session.Buffer.Rows)
        {
            var lineText = UrlDetector.GetRowText(_session.Buffer, row);
            var urls = UrlDetector.FindUrls(lineText);
            _hoveredUrl = null;
            foreach (var (startCol, endCol, url) in urls)
            {
                if (col >= startCol && col <= endCol)
                {
                    _hoveredUrl = (row, startCol, endCol, url);
                    break;
                }
            }
            Cursor = _hoveredUrl.HasValue ? Cursors.Hand : Cursors.IBeam;
            Render(); // Redraw for URL underline
        }
        else if (_hoveredUrl.HasValue)
        {
            _hoveredUrl = null;
            Cursor = Cursors.IBeam;
            Render();
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

        if (IsMouseTrackingActive && _mouseDown)
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
            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(2, col, row, true);
            return;
        }

        // Right-click paste
        PasteFromClipboard();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;

        // Mouse wheel reporting
        if (IsMouseTrackingActive)
        {
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
        Render();
        e.Handled = true;
    }

    // --- Visual tree ---

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

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
        CalculateCellSize();
        CalculateTerminalSize();
        Render();
    }

    private static void OnHasNotificationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TerminalControl)d).Render();
    }

    private static void OnIsPaneFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (TerminalControl)d;
        if ((bool)e.NewValue)
        {
            ctrl._cursorVisible = true;
            ctrl._cursorTimer?.Start();
        }
        ctrl.Render();
    }
}
