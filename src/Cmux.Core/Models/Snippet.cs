using System.Text.RegularExpressions;

namespace Cmux.Core.Models;

public partial class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "General";
    public List<string> Tags { get; set; } = [];
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int UseCount { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Replaces <c>{{key}}</c> placeholders in <see cref="Content"/> with values from the dictionary.
    /// Unmatched placeholders are left as-is.
    /// </summary>
    public string Resolve(Dictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return Content;

        return PlaceholderRegex().Replace(Content, match =>
        {
            var key = match.Groups[1].Value;
            return parameters.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    /// <summary>
    /// Returns the content without parameter substitution.
    /// </summary>
    public string Resolve() => Content;

    /// <summary>
    /// Extracts distinct placeholder names from <see cref="Content"/>.
    /// </summary>
    public List<string> GetPlaceholders()
    {
        return PlaceholderRegex()
            .Matches(Content)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
