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

    public static string GetRowText(TerminalBuffer buffer, int row)
    {
        if (row < 0 || row >= buffer.Rows)
            return string.Empty;

        var chars = new char[buffer.Cols];
        for (int c = 0; c < buffer.Cols; c++)
        {
            var ch = buffer.CellAt(row, c).Character;
            chars[c] = string.IsNullOrEmpty(ch) ? ' ' : ch[0];
        }
        return new string(chars);
    }
}
