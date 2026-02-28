using System.Text;

namespace Cmux.Core.Terminal;

public struct SelectionPoint
{
    public int Row;
    public int Col;

    public SelectionPoint(int row, int col)
    {
        Row = row;
        Col = col;
    }
}

/// <summary>
/// Manages text selection in the terminal buffer.
/// Supports click-to-start, drag-to-extend, double-click word select,
/// triple-click line select, and copy-to-clipboard.
/// </summary>
public class TerminalSelection
{
    private SelectionPoint? _start;
    private SelectionPoint? _end;

    public bool HasSelection => _start.HasValue && _end.HasValue;
    public SelectionPoint? Start => _start;
    public SelectionPoint? End => _end;

    public event Action? SelectionChanged;

    public void StartSelection(int row, int col)
    {
        _start = new SelectionPoint(row, col);
        _end = new SelectionPoint(row, col);
        SelectionChanged?.Invoke();
    }

    public void ExtendSelection(int row, int col)
    {
        if (!_start.HasValue) return;
        _end = new SelectionPoint(row, col);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        _start = null;
        _end = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Gets the normalized selection range (start before end).
    /// </summary>
    public (SelectionPoint start, SelectionPoint end)? GetNormalizedRange()
    {
        if (!_start.HasValue || !_end.HasValue) return null;

        var s = _start.Value;
        var e = _end.Value;

        if (s.Row > e.Row || (s.Row == e.Row && s.Col > e.Col))
            (s, e) = (e, s);

        return (s, e);
    }

    /// <summary>
    /// Tests whether a cell is within the current selection.
    /// </summary>
    public bool IsSelected(int row, int col)
    {
        var range = GetNormalizedRange();
        if (!range.HasValue) return false;

        var (s, e) = range.Value;

        if (row < s.Row || row > e.Row) return false;
        if (row == s.Row && row == e.Row) return col >= s.Col && col <= e.Col;
        if (row == s.Row) return col >= s.Col;
        if (row == e.Row) return col <= e.Col;
        return true;
    }

    /// <summary>
    /// Extracts selected text from the terminal buffer.
    /// </summary>
    public string GetSelectedText(TerminalBuffer buffer)
    {
        var range = GetNormalizedRange();
        if (!range.HasValue) return "";

        var (s, e) = range.Value;
        var sb = new StringBuilder();

        for (int row = s.Row; row <= e.Row; row++)
        {
            int startCol = row == s.Row ? s.Col : 0;
            int endCol = row == e.Row ? e.Col : buffer.Cols - 1;

            for (int col = startCol; col <= endCol && col < buffer.Cols; col++)
            {
                if (row >= 0 && row < buffer.Rows)
                {
                    var cell = buffer.CellAt(row, col);
                    sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
                }
            }

            // Trim trailing spaces on each line
            if (row < e.Row)
            {
                while (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Selects the word at the given position (double-click behavior).
    /// </summary>
    public void SelectWord(TerminalBuffer buffer, int row, int col)
    {
        if (row < 0 || row >= buffer.Rows || col < 0 || col >= buffer.Cols)
            return;

        var cell = buffer.CellAt(row, col);
        bool IsWordChar(char ch) => ch != '\0' && ch != ' ' && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');

        if (!IsWordChar(cell.Character))
        {
            _start = new SelectionPoint(row, col);
            _end = new SelectionPoint(row, col);
            SelectionChanged?.Invoke();
            return;
        }

        int startCol = col;
        int endCol = col;

        while (startCol > 0 && IsWordChar(buffer.CellAt(row, startCol - 1).Character))
            startCol--;

        while (endCol < buffer.Cols - 1 && IsWordChar(buffer.CellAt(row, endCol + 1).Character))
            endCol++;

        _start = new SelectionPoint(row, startCol);
        _end = new SelectionPoint(row, endCol);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Selects the entire line (triple-click behavior).
    /// </summary>
    public void SelectLine(int row, int cols)
    {
        _start = new SelectionPoint(row, 0);
        _end = new SelectionPoint(row, cols - 1);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Selects all content in the terminal buffer.
    /// </summary>
    public void SelectAll(int rows, int cols)
    {
        _start = new SelectionPoint(0, 0);
        _end = new SelectionPoint(rows - 1, cols - 1);
        SelectionChanged?.Invoke();
    }
}
