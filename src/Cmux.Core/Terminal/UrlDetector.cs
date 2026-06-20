using System.Text;
using System.Text.RegularExpressions;

namespace Cmux.Core.Terminal;

public static partial class UrlDetector
{
    [GeneratedRegex(@"(https?://|ftp://|file://)[\w\-_.~:/?#\[\]@!$&'()*+,;=%]+", RegexOptions.Compiled)]
    private static partial Regex UrlPattern();

    public static List<(int startCol, int endCol, string url)> FindUrls(string line)
    {
        var results = new List<(int, int, string)>();
        foreach (Match match in UrlPattern().Matches(line))
            results.Add((match.Index, match.Index + match.Length - 1, match.Value));
        return results;
    }

    public static List<(int startCol, int endCol, string url)> FindUrls(TerminalBuffer buffer, int row)
    {
        var (line, cellColumns) = GetRowTextWithCellColumns(buffer, row);
        var results = new List<(int, int, string)>();

        foreach (Match match in UrlPattern().Matches(line))
        {
            if (match.Index >= cellColumns.Count)
                continue;

            var lastIndex = match.Index + match.Length - 1;
            if (lastIndex >= cellColumns.Count)
                continue;

            var endCol = cellColumns[lastIndex] + TerminalBuffer.GetCharacterCellWidth(line[lastIndex]) - 1;
            results.Add((cellColumns[match.Index], endCol, match.Value));
        }

        return results;
    }

    public static string GetRowText(TerminalBuffer buffer, int row)
    {
        return GetRowTextWithCellColumns(buffer, row).text;
    }

    private static (string text, List<int> cellColumns) GetRowTextWithCellColumns(TerminalBuffer buffer, int row)
    {
        if (row < 0 || row >= buffer.Rows)
            return (string.Empty, []);

        var text = new StringBuilder(buffer.Cols);
        var cellColumns = new List<int>(buffer.Cols);

        for (int c = 0; c < buffer.Cols; c++)
        {
            var cell = buffer.CellAt(row, c);
            if (cell.Width == 0)
                continue;

            text.Append(cell.Character == '\0' ? ' ' : cell.Character);
            cellColumns.Add(c);
        }

        return (text.ToString(), cellColumns);
    }
}
