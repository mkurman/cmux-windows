namespace Cmux.Core.Terminal;

/// <summary>
/// Manages the terminal cell grid, cursor state, scrollback buffer,
/// and scroll regions. This is the core data structure that the VT parser
/// operates on and the renderer reads from.
/// </summary>
public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private readonly ScrollbackBuffer<TerminalCell[]> _scrollback;
    private readonly int _maxScrollback;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;

    // Scroll region (inclusive, 0-based)
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    // Saved cursor state
    private int _savedCursorRow;
    private int _savedCursorCol;
    private TerminalAttribute _savedAttribute;

    // Current writing attribute
    public TerminalAttribute CurrentAttribute { get; set; } = TerminalAttribute.Default;

    // Mode flags
    public bool OriginMode { get; set; }
    public bool AutoWrapMode { get; set; } = true;
    public bool InsertMode { get; set; }
    public bool ApplicationCursorKeys { get; set; }
    public bool BracketedPasteMode { get; set; }
    public bool IsAlternateScreen { get; private set; }

    // Mouse tracking modes
    public bool MouseTrackingNormal { get; set; }    // Mode 1000: button events
    public bool MouseTrackingButton { get; set; }    // Mode 1002: button + motion while pressed
    public bool MouseTrackingAny { get; set; }       // Mode 1003: all motion
    public bool MouseSgrExtended { get; set; }       // Mode 1006: SGR extended coordinates
    public bool MouseEnabled => MouseTrackingNormal || MouseTrackingButton || MouseTrackingAny;

    private bool _wrapPending;

    // Alternate screen buffer state
    private TerminalCell[,]? _savedMainCells;
    private List<TerminalCell[]>? _savedMainScrollbackList;
    private int _savedMainCursorRow;
    private int _savedMainCursorCol;
    private TerminalAttribute _savedMainAttribute;

    public int ScrollbackCount => _scrollback.Count;
    public int TotalLines => Rows + _scrollback.Count;

    public event Action? ContentChanged;

    public TerminalBuffer(int cols, int rows, int maxScrollback = 10_000)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _maxScrollback = maxScrollback;
        _scrollback = new ScrollbackBuffer<TerminalCell[]>(maxScrollback);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        _cells = new TerminalCell[Rows, Cols];
        Clear();
    }

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c] = TerminalCell.Empty;
    }

    public ref TerminalCell CellAt(int row, int col)
    {
        int maxRow = _cells.GetLength(0) - 1;
        int maxCol = _cells.GetLength(1) - 1;
        row = Math.Clamp(row, 0, maxRow);
        col = Math.Clamp(col, 0, maxCol);
        return ref _cells[row, col];
    }

    public TerminalCell[] GetLine(int row)
    {
        int cols = _cells.GetLength(1);
        int safeRow = Math.Clamp(row, 0, _cells.GetLength(0) - 1);
        var line = new TerminalCell[cols];
        for (int c = 0; c < cols; c++)
            line[c] = _cells[safeRow, c];
        return line;
    }

    public TerminalCell[]? GetScrollbackLine(int index)
    {
        if (index < 0 || index >= _scrollback.Count) return null;
        return _scrollback[index];
    }

    public void SetChar(int row, int col, char ch, TerminalAttribute attr)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
        _cells[row, col] = new TerminalCell
        {
            Character = ch,
            Attribute = attr,
            IsDirty = true,
            Width = 1,
        };
    }

    /// <summary>
    /// Writes a character at the current cursor position and advances the cursor.
    /// Handles auto-wrap and insert mode.
    /// </summary>
    public void WriteChar(char c)
    {
        if (!ClampCursorToBounds())
            return;

        if (_wrapPending && AutoWrapMode)
        {
            CarriageReturn();
            LineFeed();
            _wrapPending = false;
        }

        if (InsertMode)
        {
            // Shift characters right
            for (int col = Cols - 1; col > CursorCol; col--)
                _cells[CursorRow, col] = _cells[CursorRow, col - 1];
        }

        if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
        {
            _cells[CursorRow, CursorCol] = new TerminalCell
            {
                Character = c,
                Attribute = CurrentAttribute,
                IsDirty = true,
                Width = 1,
            };
        }

        if (CursorCol + 1 >= Cols)
        {
            _wrapPending = true;
        }
        else
        {
            CursorCol++;
        }
    }

    public void WriteString(string text)
    {
        foreach (var c in text)
            WriteChar(c);
    }

    public void CarriageReturn()
    {
        CursorCol = 0;
        _wrapPending = false;
    }

    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    public void ReverseLineFeed()
    {
        if (CursorRow == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    public void NewLine()
    {
        CarriageReturn();
        LineFeed();
    }

    /// <summary>
    /// Scrolls the scroll region up by the given number of lines.
    /// Lines scrolled out of the top go to scrollback if the scroll region is the full screen.
    /// </summary>
    public void ScrollUp(int lines = 1)
    {
        for (int n = 0; n < lines; n++)
        {
            // If the scroll region starts at line 0, push to scrollback
            if (ScrollTop == 0)
            {
                var scrolledLine = new TerminalCell[Cols];
                for (int c = 0; c < Cols; c++)
                    scrolledLine[c] = _cells[0, c];

                _scrollback.Add(scrolledLine);
            }

            // Shift lines up within the scroll region
            for (int r = ScrollTop; r < ScrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];

            // Clear the bottom line
            for (int c = 0; c < Cols; c++)
                _cells[ScrollBottom, c] = TerminalCell.Empty;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Scrolls the scroll region down by the given number of lines.
    /// </summary>
    public void ScrollDown(int lines = 1)
    {
        for (int n = 0; n < lines; n++)
        {
            for (int r = ScrollBottom; r > ScrollTop; r--)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];

            for (int c = 0; c < Cols; c++)
                _cells[ScrollTop, c] = TerminalCell.Empty;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Erases parts of the display.
    /// 0 = cursor to end, 1 = start to cursor, 2 = all, 3 = all + scrollback
    /// </summary>
    public void EraseInDisplay(int mode)
    {
        if (!ClampCursorToBounds())
            return;

        switch (mode)
        {
            case 0: // Cursor to end
                for (int c = CursorCol; c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                for (int r = CursorRow + 1; r < Rows; r++)
                    for (int c = 0; c < Cols; c++)
                        _cells[r, c] = TerminalCell.Empty;
                break;
            case 1: // Start to cursor
                for (int r = 0; r < CursorRow; r++)
                    for (int c = 0; c < Cols; c++)
                        _cells[r, c] = TerminalCell.Empty;
                for (int c = 0; c <= CursorCol; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
            case 2: // All
                Clear();
                break;
            case 3: // All + scrollback
                Clear();
                _scrollback.Clear();
                break;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Erases parts of the current line.
    /// 0 = cursor to end, 1 = start to cursor, 2 = entire line
    /// </summary>
    public void EraseInLine(int mode)
    {
        if (!ClampCursorToBounds())
            return;

        switch (mode)
        {
            case 0:
                for (int c = CursorCol; c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
            case 1:
                for (int c = 0; c <= CursorCol; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
            case 2:
                for (int c = 0; c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
        }

        RaiseContentChanged();
    }

    public void EraseChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int i = 0; i < count && CursorCol + i < Cols; i++)
            _cells[CursorRow, CursorCol + i] = TerminalCell.Empty;
        RaiseContentChanged();
    }

    public void InsertLines(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        int savedBottom = ScrollBottom;
        ScrollBottom = Rows - 1;
        for (int n = 0; n < count; n++)
        {
            for (int r = ScrollBottom; r > CursorRow; r--)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];
            for (int c = 0; c < Cols; c++)
                _cells[CursorRow, c] = TerminalCell.Empty;
        }
        ScrollBottom = savedBottom;
        RaiseContentChanged();
    }

    public void DeleteLines(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int r = CursorRow; r < ScrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
            for (int c = 0; c < Cols; c++)
                _cells[ScrollBottom, c] = TerminalCell.Empty;
        }
        RaiseContentChanged();
    }

    public void InsertChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int c = Cols - 1; c > CursorCol; c--)
                _cells[CursorRow, c] = _cells[CursorRow, c - 1];
            _cells[CursorRow, CursorCol] = TerminalCell.Empty;
        }
        RaiseContentChanged();
    }

    public void DeleteChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int c = CursorCol; c < Cols - 1; c++)
                _cells[CursorRow, c] = _cells[CursorRow, c + 1];
            _cells[CursorRow, Cols - 1] = TerminalCell.Empty;
        }
        RaiseContentChanged();
    }

    public void SetScrollRegion(int top, int bottom)
    {
        ScrollTop = Math.Max(0, Math.Min(top, Rows - 1));
        ScrollBottom = Math.Max(0, Math.Min(bottom, Rows - 1));
        if (ScrollTop > ScrollBottom)
            (ScrollTop, ScrollBottom) = (ScrollBottom, ScrollTop);
    }

    public void ResetScrollRegion()
    {
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    public void SaveCursor()
    {
        _savedCursorRow = CursorRow;
        _savedCursorCol = CursorCol;
        _savedAttribute = CurrentAttribute;
    }

    public void RestoreCursor()
    {
        CursorRow = _savedCursorRow;
        CursorCol = _savedCursorCol;
        CurrentAttribute = _savedAttribute;
    }

    /// <summary>
    /// Switches to the alternate screen buffer (DECSET 1049).
    /// Saves main screen cells, scrollback, cursor, and attribute.
    /// </summary>
    public void SwitchToAlternateScreen()
    {
        if (IsAlternateScreen) return;

        // Save main screen state
        _savedMainCells = _cells;
        _savedMainScrollbackList = _scrollback.ToList();
        _savedMainCursorRow = CursorRow;
        _savedMainCursorCol = CursorCol;
        _savedMainAttribute = CurrentAttribute;

        // Create a fresh screen
        _cells = new TerminalCell[Rows, Cols];
        Clear();
        _scrollback.Clear();

        CursorRow = 0;
        CursorCol = 0;
        CurrentAttribute = TerminalAttribute.Default;
        SetScrollRegion(0, Rows - 1);
        IsAlternateScreen = true;
    }

    /// <summary>
    /// Switches back to the main screen buffer (DECRST 1049).
    /// Restores saved main screen state.
    /// </summary>
    public void SwitchToMainScreen()
    {
        if (!IsAlternateScreen) return;

        // Restore main screen state
        if (_savedMainCells != null)
        {
            _cells = _savedMainCells;
            _savedMainCells = null;
        }

        _scrollback.Clear();
        if (_savedMainScrollbackList != null)
        {
            _scrollback.AddRange(_savedMainScrollbackList);
            _savedMainScrollbackList = null;
        }

        CursorRow = _savedMainCursorRow;
        CursorCol = _savedMainCursorCol;
        CurrentAttribute = _savedMainAttribute;
        SetScrollRegion(0, Rows - 1);
        IsAlternateScreen = false;

        RaiseContentChanged();
    }

    public void MoveCursorTo(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
        _wrapPending = false;
    }

    public void MoveCursorUp(int count = 1)
    {
        CursorRow = Math.Max(ScrollTop, CursorRow - count);
        _wrapPending = false;
    }

    public void MoveCursorDown(int count = 1)
    {
        CursorRow = Math.Min(ScrollBottom, CursorRow + count);
        _wrapPending = false;
    }

    public void MoveCursorForward(int count = 1)
    {
        CursorCol = Math.Min(Cols - 1, CursorCol + count);
        _wrapPending = false;
    }

    public void MoveCursorBackward(int count = 1)
    {
        CursorCol = Math.Max(0, CursorCol - count);
        _wrapPending = false;
    }

    public void Tab()
    {
        int nextTab = ((CursorCol / 8) + 1) * 8;
        CursorCol = Math.Min(nextTab, Cols - 1);
    }

    public void Backspace()
    {
        if (CursorCol > 0)
            CursorCol--;
        _wrapPending = false;
    }

    /// <summary>
    /// Resizes the buffer, preserving content as much as possible.
    /// </summary>
    public void Resize(int newCols, int newRows)
    {
        newCols = Math.Max(1, newCols);
        newRows = Math.Max(1, newRows);

        var newCells = new TerminalCell[newRows, newCols];
        for (int r = 0; r < newRows; r++)
            for (int c = 0; c < newCols; c++)
                newCells[r, c] = TerminalCell.Empty;

        int copyRows = Math.Min(Rows, newRows);
        int copyCols = Math.Min(Cols, newCols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newCells[r, c] = _cells[r, c];

        _cells = newCells;
        Cols = newCols;
        Rows = newRows;
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
        CursorRow = Math.Min(CursorRow, newRows - 1);
        CursorCol = Math.Min(CursorCol, newCols - 1);

        RaiseContentChanged();
    }

    public string ExportPlainText(int maxScrollbackLines = 20000)
    {
        var lines = new List<string>();

        int scrollbackStart = Math.Max(0, _scrollback.Count - Math.Max(0, maxScrollbackLines));
        for (int i = scrollbackStart; i < _scrollback.Count; i++)
            lines.Add(LineToText(_scrollback[i], Cols));

        for (int row = 0; row < Rows; row++)
            lines.Add(LineToText(GetLine(row), Cols));

        int lastNonEmpty = lines.FindLastIndex(line => !string.IsNullOrWhiteSpace(line));
        if (lastNonEmpty < 0)
            return string.Empty;

        return string.Join(Environment.NewLine, lines.Take(lastNonEmpty + 1));
    }

    /// <summary>
    /// Creates a plain-text snapshot of scrollback and visible rows.
    /// Used for restoring terminal context across app restarts.
    /// </summary>
    public TerminalBufferSnapshot CreateSnapshot(int maxScrollbackLines = 3000)
    {
        var snapshot = new TerminalBufferSnapshot
        {
            Cols = Cols,
            Rows = Rows,
            CursorRow = CursorRow,
            CursorCol = CursorCol,
        };

        int scrollbackStart = Math.Max(0, _scrollback.Count - Math.Max(0, maxScrollbackLines));
        for (int i = scrollbackStart; i < _scrollback.Count; i++)
            snapshot.ScrollbackLines.Add(LineToText(_scrollback[i], Cols));

        for (int row = 0; row < Rows; row++)
            snapshot.ScreenLines.Add(LineToText(GetLine(row), Cols));

        return snapshot;
    }

    /// <summary>
    /// Restores a previously captured plain-text snapshot.
    /// </summary>
    public void RestoreSnapshot(TerminalBufferSnapshot snapshot)
    {
        if (snapshot == null) return;

        _scrollback.Clear();
        foreach (var line in snapshot.ScrollbackLines)
            _scrollback.Add(TextToLine(line, Cols));

        Clear();

        int rowCount = Math.Min(Rows, snapshot.ScreenLines.Count);
        for (int row = 0; row < rowCount; row++)
        {
            var text = snapshot.ScreenLines[row];
            int colCount = Math.Min(Cols, text.Length);
            for (int col = 0; col < colCount; col++)
            {
                _cells[row, col] = new TerminalCell
                {
                    Character = text[col],
                    Attribute = TerminalAttribute.Default,
                    IsDirty = true,
                    Width = 1,
                };
            }
        }

        CursorRow = Math.Clamp(snapshot.CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(snapshot.CursorCol, 0, Cols - 1);
        ResetScrollRegion();
        MarkAllDirty();
        RaiseContentChanged();
    }

    private bool ClampCursorToBounds()
    {
        if (Rows <= 0 || Cols <= 0)
            return false;

        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
        return true;
    }

    private static string LineToText(TerminalCell[] line, int cols)
    {
        var chars = new char[cols];
        for (int i = 0; i < cols; i++)
        {
            var ch = i < line.Length ? line[i].Character : ' ';
            chars[i] = ch == '\0' ? ' ' : ch;
        }

        return new string(chars).TrimEnd();
    }

    private static TerminalCell[] TextToLine(string? text, int cols)
    {
        var line = new TerminalCell[cols];
        for (int i = 0; i < cols; i++)
            line[i] = TerminalCell.Empty;

        if (string.IsNullOrEmpty(text)) return line;

        int len = Math.Min(cols, text.Length);
        for (int i = 0; i < len; i++)
        {
            line[i] = new TerminalCell
            {
                Character = text[i],
                Attribute = TerminalAttribute.Default,
                IsDirty = true,
                Width = 1,
            };
        }

        return line;
    }

    /// <summary>
    /// Marks all cells as dirty (for full repaint).
    /// </summary>
    public void MarkAllDirty()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c].IsDirty = true;
    }

    /// <summary>
    /// Clears dirty flags on all cells.
    /// </summary>
    public void ClearDirty()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c].IsDirty = false;
    }

    private void RaiseContentChanged() => ContentChanged?.Invoke();
}

public class TerminalBufferSnapshot
{
    public int Cols { get; set; }
    public int Rows { get; set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public List<string> ScrollbackLines { get; set; } = [];
    public List<string> ScreenLines { get; set; } = [];
}
