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
        }

        _session = session;
        _session.Redraw += OnRedraw;
        CalculateTerminalSize();
        Render();
    }

    private void OnRedraw()
    {
        _scrollOffset = 0; // Auto-scroll to bottom on new output
        Dispatcher.BeginInvoke(Render);
    }

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

    private void Render()
    {
        if (_session == null) return;

        var buffer = _session.Buffer;
        using var dc = _visual.RenderOpen();

        // Background
        var bgColor = ToWpfColor(_theme.Background);
        dc.DrawRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Notification ring (blue border glow)
        if (HasNotification)
        {
            var notifColor = Color.FromArgb(180, 0x3B, 0x82, 0xF6);
            var pen = new Pen(new SolidColorBrush(notifColor), 2);
            dc.DrawRectangle(null, pen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
        }

        // Focused pane indicator
        if (IsPaneFocused)
        {
            var focusColor = Color.FromArgb(60, 0x56, 0x9C, 0xD6);
            var pen = new Pen(new SolidColorBrush(focusColor), 1);
            dc.DrawRectangle(null, pen, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Render cells
        for (int r = 0; r < buffer.Rows && r < _rows; r++)
        {
            for (int c = 0; c < buffer.Cols && c < _cols; c++)
            {
                var cell = buffer.CellAt(r, c);
                double x = c * _cellWidth;
                double y = r * _cellHeight;

                var attr = cell.Attribute;
                bool isSelected = _selection.IsSelected(r, c);
                bool isInverse = attr.Flags.HasFlag(CellFlags.Inverse) != isSelected;

                // Cell background
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

                if (!cellBg.IsDefault)
                {
                    var bgBrush = new SolidColorBrush(ToWpfColor(cellBg));
                    dc.DrawRectangle(bgBrush, null, new Rect(x, y, _cellWidth, _cellHeight));
                }

                // Cell character
                if (!string.IsNullOrEmpty(cell.Character) && cell.Character != " ")
                {
                    var fgColor = cellFg.IsDefault ? ToWpfColor(_theme.Foreground) : ToWpfColor(cellFg);

                    // Bold / Dim
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

        // Cursor
        if (buffer.CursorVisible && _cursorVisible && IsPaneFocused)
        {
            double cx = buffer.CursorCol * _cellWidth;
            double cy = buffer.CursorRow * _cellHeight;
            var cursorColor = _theme.CursorColor.HasValue
                ? ToWpfColor(_theme.CursorColor.Value)
                : ToWpfColor(_theme.Foreground);
            var cursorBrush = new SolidColorBrush(Color.FromArgb(180, cursorColor.R, cursorColor.G, cursorColor.B));
            dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, _cellWidth, _cellHeight));
        }
    }

    private static Color ToWpfColor(TerminalColor c) =>
        c.IsDefault ? Colors.Transparent : Color.FromRgb(c.R, c.G, c.B);

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
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (_session.Buffer.BracketedPasteMode)
                    _session.Write("\x1b[200~" + text + "\x1b[201~");
                else
                    _session.Write(text);
            }
            return;
        }

        _session.Write(e.Text);
        _selection.ClearSelection();
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
        if (!_mouseDown) return;

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
        _selection.ExtendSelection(row, col);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mouseDown)
        {
            _mouseDown = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        // Right-click paste (common terminal behavior)
        if (Clipboard.ContainsText() && _session != null)
        {
            var text = Clipboard.GetText();
            if (_session.Buffer.BracketedPasteMode)
                _session.Write("\x1b[200~" + text + "\x1b[201~");
            else
                _session.Write(text);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;

        int lines = e.Delta > 0 ? -3 : 3; // Negative = scroll up (into history)
        _scrollOffset = Math.Clamp(_scrollOffset + lines, -_session.Buffer.ScrollbackCount, 0);
        Render();
        e.Handled = true;
    }

    // --- Visual tree ---

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    private static string? KeyToVtSequence(Key key, ModifierKeys modifiers, bool appCursor)
    {
        // Application cursor keys mode sends ESC O instead of ESC [
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
