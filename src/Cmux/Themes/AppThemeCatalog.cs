using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Cmux.Themes;

/// <summary>
/// Catalog of selectable App Theme variants — the user-facing list shown in
/// Settings → Appearance → App Theme. Each entry describes a Mode (Light/Dark)
/// and a small set of overridden brush colors that paint the WPF chrome.
///
/// The base brand dictionary (Themes/DarkTheme.xaml or Themes/LightTheme.xaml)
/// supplies the full token + style surface; the overlay built from
/// <see cref="BuildOverlay"/> only redefines a handful of high-impact brushes
/// (background, foreground, accent, sidebar, surface, border) so each named
/// variant looks distinct without authoring 20+ full XAML dictionaries.
///
/// Last-merged-wins is how WPF resolves <c>StaticResource</c> across merged
/// dictionaries, so callers must append the overlay AFTER the base dictionary
/// in <see cref="System.Windows.ResourceDictionary.MergedDictionaries"/>.
/// </summary>
public static class AppThemeCatalog
{
    public sealed record Variant(
        string Name,
        string Mode, // "Dark" or "Light"
        string Background,
        string Foreground,
        string ForegroundDim,
        string Accent,
        string Border,
        string Surface,
        string SurfaceHigh,
        string SidebarBackground,
        string SidebarHover,
        string SidebarSelected,
        string Notification);

    /// <summary>The variant the app falls back to when a saved name isn't in the list.</summary>
    public const string DefaultDarkName = "Default Dark";
    public const string DefaultLightName = "Default Light";

    /// <summary>Authoritative variant list. Order does not matter — UI sorts before showing.</summary>
    public static readonly IReadOnlyList<Variant> All =
    [
        // ── Default (matches the colors in the base XAML dictionaries) ─────────
        new(DefaultDarkName,  "Dark",
            Background: "#FF0F0F0F", Foreground: "#FFE2E2E9", ForegroundDim: "#FF6B6B80",
            Accent: "#FF818CF8", Border: "#FF2A2A3C",
            Surface: "#FF141420", SurfaceHigh: "#FF1C1C30",
            SidebarBackground: "#FF0A0A0A", SidebarHover: "#FF1A1A2E", SidebarSelected: "#FF252547",
            Notification: "#FF6366F1"),
        new(DefaultLightName, "Light",
            Background: "#FFF7F7FA", Foreground: "#FF1A1A1F", ForegroundDim: "#FF6B7280",
            Accent: "#FF6366F1", Border: "#FFD4D4DB",
            Surface: "#FFFFFFFF", SurfaceHigh: "#FFFAFAFC",
            SidebarBackground: "#FFEDEDF2", SidebarHover: "#FFE0E0EC", SidebarSelected: "#FFD8D8F0",
            Notification: "#FF4F46E5"),

        // ── Catppuccin family ──────────────────────────────────────────────────
        new("Catppuccin Mocha",  "Dark",
            Background: "#FF1E1E2E", Foreground: "#FFCDD6F4", ForegroundDim: "#FF7F849C",
            Accent: "#FF89B4FA", Border: "#FF313244",
            Surface: "#FF181825", SurfaceHigh: "#FF313244",
            SidebarBackground: "#FF11111B", SidebarHover: "#FF313244", SidebarSelected: "#FF45475A",
            Notification: "#FFCBA6F7"),
        new("Catppuccin Frappe", "Dark",
            Background: "#FF303446", Foreground: "#FFC6D0F5", ForegroundDim: "#FF838BA7",
            Accent: "#FF8CAAEE", Border: "#FF414559",
            Surface: "#FF292C3C", SurfaceHigh: "#FF414559",
            SidebarBackground: "#FF232634", SidebarHover: "#FF414559", SidebarSelected: "#FF51576D",
            Notification: "#FFCA9EE6"),
        new("Catppuccin Latte",  "Light",
            Background: "#FFEFF1F5", Foreground: "#FF4C4F69", ForegroundDim: "#FF6C6F85",
            Accent: "#FF1E66F5", Border: "#FFCCD0DA",
            Surface: "#FFFFFFFF", SurfaceHigh: "#FFE6E9EF",
            SidebarBackground: "#FFE6E9EF", SidebarHover: "#FFCCD0DA", SidebarSelected: "#FFBCC0CC",
            Notification: "#FFEA76CB"),

        // ── Dracula family ─────────────────────────────────────────────────────
        new("Dracula",  "Dark",
            Background: "#FF282A36", Foreground: "#FFF8F8F2", ForegroundDim: "#FF6272A4",
            Accent: "#FFBD93F9", Border: "#FF44475A",
            Surface: "#FF21222C", SurfaceHigh: "#FF44475A",
            SidebarBackground: "#FF21222C", SidebarHover: "#FF44475A", SidebarSelected: "#FF6272A4",
            Notification: "#FFFF79C6"),
        new("Alucard", "Light",
            Background: "#FFF8F8F2", Foreground: "#FF22212C", ForegroundDim: "#FF7970A9",
            Accent: "#FF644AC9", Border: "#FFE6E6E6",
            Surface: "#FFFFFFFF", SurfaceHigh: "#FFEFEFEC",
            SidebarBackground: "#FFEFEFEC", SidebarHover: "#FFE6E6E6", SidebarSelected: "#FFD9D9D9",
            Notification: "#FF644AC9"),

        // ── GitHub family ──────────────────────────────────────────────────────
        new("GitHub Dark",  "Dark",
            Background: "#FF0D1117", Foreground: "#FFF0F6FC", ForegroundDim: "#FF6E7681",
            Accent: "#FF4493F8", Border: "#FF30363D",
            Surface: "#FF161B22", SurfaceHigh: "#FF21262D",
            SidebarBackground: "#FF010409", SidebarHover: "#FF161B22", SidebarSelected: "#FF21262D",
            Notification: "#FFAB7DF8"),
        new("GitHub Light", "Light",
            Background: "#FFFFFFFF", Foreground: "#FF24292F", ForegroundDim: "#FF57606A",
            Accent: "#FF0969DA", Border: "#FFD0D7DE",
            Surface: "#FFF6F8FA", SurfaceHigh: "#FFEAEEF2",
            SidebarBackground: "#FFF6F8FA", SidebarHover: "#FFEAEEF2", SidebarSelected: "#FFD0D7DE",
            Notification: "#FF8250DF"),

        // ── Nord family ────────────────────────────────────────────────────────
        new("Nord",       "Dark",
            Background: "#FF2E3440", Foreground: "#FFD8DEE9", ForegroundDim: "#FF4C566A",
            Accent: "#FF88C0D0", Border: "#FF3B4252",
            Surface: "#FF3B4252", SurfaceHigh: "#FF434C5E",
            SidebarBackground: "#FF242933", SidebarHover: "#FF3B4252", SidebarSelected: "#FF434C5E",
            Notification: "#FFB48EAD"),
        new("Nord Light", "Light",
            Background: "#FFECEFF4", Foreground: "#FF2E3440", ForegroundDim: "#FF4C566A",
            Accent: "#FF5E81AC", Border: "#FFD8DEE9",
            Surface: "#FFE5E9F0", SurfaceHigh: "#FFD8DEE9",
            SidebarBackground: "#FFD8DEE9", SidebarHover: "#FFC8D0E0", SidebarSelected: "#FFB8C0D0",
            Notification: "#FFB48EAD"),

        // ── One family ─────────────────────────────────────────────────────────
        new("One Dark",  "Dark",
            Background: "#FF282C34", Foreground: "#FFABB2BF", ForegroundDim: "#FF5C6370",
            Accent: "#FF61AFEF", Border: "#FF3E4451",
            Surface: "#FF21252B", SurfaceHigh: "#FF353B45",
            SidebarBackground: "#FF21252B", SidebarHover: "#FF353B45", SidebarSelected: "#FF3E4451",
            Notification: "#FFC678DD"),
        new("One Light", "Light",
            Background: "#FFFAFAFA", Foreground: "#FF383A42", ForegroundDim: "#FFA0A1A7",
            Accent: "#FF0184BC", Border: "#FFE5E5E6",
            Surface: "#FFFFFFFF", SurfaceHigh: "#FFEFEFEF",
            SidebarBackground: "#FFEFEFEF", SidebarHover: "#FFE5E5E6", SidebarSelected: "#FFD8D8D9",
            Notification: "#FFA626A4"),

        // ── Rose Pine family ──────────────────────────────────────────────────
        new("Rose Pine",      "Dark",
            Background: "#FF191724", Foreground: "#FFE0DEF4", ForegroundDim: "#FF6E6A86",
            Accent: "#FFC4A7E7", Border: "#FF26233A",
            Surface: "#FF1F1D2E", SurfaceHigh: "#FF26233A",
            SidebarBackground: "#FF15131F", SidebarHover: "#FF26233A", SidebarSelected: "#FF403D52",
            Notification: "#FFEB6F92"),
        new("Rose Pine Dawn", "Light",
            Background: "#FFFAF4ED", Foreground: "#FF575279", ForegroundDim: "#FF9893A5",
            Accent: "#FF907AA9", Border: "#FFDFDAD9",
            Surface: "#FFFFFAF3", SurfaceHigh: "#FFF4EDE8",
            SidebarBackground: "#FFF2E9E1", SidebarHover: "#FFDFDAD9", SidebarSelected: "#FFCECACD",
            Notification: "#FFB4637A"),

        // ── Tokyo Night family ─────────────────────────────────────────────────
        new("Tokyo Night",     "Dark",
            Background: "#FF1A1B26", Foreground: "#FFA9B1D6", ForegroundDim: "#FF565F89",
            Accent: "#FF7AA2F7", Border: "#FF2F334D",
            Surface: "#FF1F2335", SurfaceHigh: "#FF292E42",
            SidebarBackground: "#FF16161E", SidebarHover: "#FF24283B", SidebarSelected: "#FF364A82",
            Notification: "#FFBB9AF7"),
        new("Tokyo Night Day", "Light",
            Background: "#FFE1E2E7", Foreground: "#FF3760BF", ForegroundDim: "#FF6172B0",
            Accent: "#FF2E7DE9", Border: "#FFC4C8DA",
            Surface: "#FFE9E9ED", SurfaceHigh: "#FFD8DAE5",
            SidebarBackground: "#FFD8DAE5", SidebarHover: "#FFC4C8DA", SidebarSelected: "#FFA1A6C5",
            Notification: "#FF9854F1"),

        // ── Solarized family ──────────────────────────────────────────────────
        new("Solarized Dark",  "Dark",
            Background: "#FF002B36", Foreground: "#FF839496", ForegroundDim: "#FF586E75",
            Accent: "#FF268BD2", Border: "#FF073642",
            Surface: "#FF073642", SurfaceHigh: "#FF0C4A5C",
            SidebarBackground: "#FF002129", SidebarHover: "#FF073642", SidebarSelected: "#FF0F4F62",
            Notification: "#FFD33682"),
        new("Solarized Light", "Light",
            Background: "#FFFDF6E3", Foreground: "#FF657B83", ForegroundDim: "#FF93A1A1",
            Accent: "#FF268BD2", Border: "#FFEEE8D5",
            Surface: "#FFFDF6E3", SurfaceHigh: "#FFEEE8D5",
            SidebarBackground: "#FFEEE8D5", SidebarHover: "#FFE5DEC2", SidebarSelected: "#FFD0C7A8",
            Notification: "#FFD33682"),

        // ── Gruvbox family ─────────────────────────────────────────────────────
        new("Gruvbox Dark",  "Dark",
            Background: "#FF282828", Foreground: "#FFEBDBB2", ForegroundDim: "#FF928374",
            Accent: "#FF83A598", Border: "#FF3C3836",
            Surface: "#FF32302F", SurfaceHigh: "#FF504945",
            SidebarBackground: "#FF1D2021", SidebarHover: "#FF3C3836", SidebarSelected: "#FF504945",
            Notification: "#FFD3869B"),
        new("Gruvbox Light", "Light",
            Background: "#FFFBF1C7", Foreground: "#FF3C3836", ForegroundDim: "#FF7C6F64",
            Accent: "#FF076678", Border: "#FFEBDBB2",
            Surface: "#FFF9F5D7", SurfaceHigh: "#FFEBDBB2",
            SidebarBackground: "#FFEBDBB2", SidebarHover: "#FFD5C4A1", SidebarSelected: "#FFBDAE93",
            Notification: "#FF8F3F71"),

        // ── Everforest family ──────────────────────────────────────────────────
        new("Everforest Dark",  "Dark",
            Background: "#FF2D353B", Foreground: "#FFD3C6AA", ForegroundDim: "#FF859289",
            Accent: "#FF7FBBB3", Border: "#FF3C474D",
            Surface: "#FF343F44", SurfaceHigh: "#FF475258",
            SidebarBackground: "#FF272E33", SidebarHover: "#FF374247", SidebarSelected: "#FF475258",
            Notification: "#FFD699B6"),
        new("Everforest Light", "Light",
            Background: "#FFFDF6E3", Foreground: "#FF5C6A72", ForegroundDim: "#FF939F91",
            Accent: "#FF3A94C5", Border: "#FFE8E0C9",
            Surface: "#FFEFEBD4", SurfaceHigh: "#FFE5DCC4",
            SidebarBackground: "#FFEFEBD4", SidebarHover: "#FFE5DCC4", SidebarSelected: "#FFD3CFB7",
            Notification: "#FFDF69BA"),

        // ── Monokai (no canonical light counterpart) ───────────────────────────
        new("Monokai", "Dark",
            Background: "#FF272822", Foreground: "#FFF8F8F2", ForegroundDim: "#FF75715E",
            Accent: "#FF66D9EF", Border: "#FF49483E",
            Surface: "#FF1E1F1B", SurfaceHigh: "#FF49483E",
            SidebarBackground: "#FF1E1F1B", SidebarHover: "#FF49483E", SidebarSelected: "#FF75715E",
            Notification: "#FFAE81FF"),
    ];

    /// <summary>Find a variant by name (case-sensitive). Returns null if not found.</summary>
    public static Variant? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : All.FirstOrDefault(v => v.Name == name);

    /// <summary>
    /// Builds the runtime overlay <see cref="ResourceDictionary"/> for the given variant.
    /// Returns null when the variant is one of the Default entries — its colors already
    /// match the base XAML dictionary, so no overlay is needed.
    /// </summary>
    public static ResourceDictionary? BuildOverlay(Variant v)
    {
        if (v.Name == DefaultDarkName || v.Name == DefaultLightName)
            return null;

        var dict = new ResourceDictionary();
        Add(dict, "BackgroundColor", v.Background);
        Add(dict, "ForegroundColor", v.Foreground);
        Add(dict, "ForegroundDimColor", v.ForegroundDim);
        Add(dict, "AccentColor", v.Accent);
        Add(dict, "BorderColor", v.Border);
        Add(dict, "SurfaceColor", v.Surface);
        Add(dict, "SurfaceHighColor", v.SurfaceHigh);
        Add(dict, "SidebarBackgroundColor", v.SidebarBackground);
        Add(dict, "SidebarItemHoverColor", v.SidebarHover);
        Add(dict, "SidebarItemSelectedColor", v.SidebarSelected);
        Add(dict, "NotificationColor", v.Notification);
        Add(dict, "InputBackgroundColor", v.Surface);

        // Brushes — last-merged wins for x:Key lookups, so consumers picking up
        // BackgroundBrush/etc. via StaticResource will see ours instead of the base.
        AddBrush(dict, "BackgroundBrush", v.Background);
        AddBrush(dict, "ForegroundBrush", v.Foreground);
        AddBrush(dict, "ForegroundDimBrush", v.ForegroundDim);
        AddBrush(dict, "AccentBrush", v.Accent);
        AddBrush(dict, "BorderBrush", v.Border);
        AddBrush(dict, "SurfaceBrush", v.Surface);
        AddBrush(dict, "SurfaceHighBrush", v.SurfaceHigh);
        AddBrush(dict, "SidebarBackgroundBrush", v.SidebarBackground);
        AddBrush(dict, "SidebarItemHoverBrush", v.SidebarHover);
        AddBrush(dict, "SidebarItemSelectedBrush", v.SidebarSelected);
        AddBrush(dict, "NotificationBrush", v.Notification);
        AddBrush(dict, "InputBackgroundBrush", v.Surface);
        return dict;
    }

    private static void Add(ResourceDictionary dict, string key, string hex)
    {
        if (TryParseColor(hex, out var c)) dict[key] = c;
    }

    private static void AddBrush(ResourceDictionary dict, string key, string hex)
    {
        if (TryParseColor(hex, out var c))
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            dict[key] = brush;
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            color = Colors.Transparent;
            return false;
        }
    }
}
