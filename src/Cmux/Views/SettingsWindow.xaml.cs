using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Cmux.Core.Config;

namespace Cmux.Views;

public partial class SettingsWindow : Window
{
    private bool _suppressTerminalColorEvents;
    private bool _suppressThemeSync;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        PopulateThemes();
        LoadSettings();
        ShowSection("Appearance");
    }

    private void PopulateThemes()
    {
        ThemeCombo.ItemsSource = TerminalThemes.Names;
        TerminalThemePresetCombo.ItemsSource = TerminalThemes.Names;
        CursorStyleCombo.ItemsSource = new[] { "bar", "block", "underline" };

        var fontFamilies = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name)
            .ToList();
        FontFamilyCombo.ItemsSource = fontFamilies;

        // Detect available shells
        var shells = DetectShells();
        ShellCombo.ItemsSource = shells;
        ShellCombo.DisplayMemberPath = "Name";
        ShellCombo.SelectedValuePath = "Path";

        // Detect system theme
        var isLight = IsSystemLightTheme();
        SystemThemeText.Text = isLight ? "Light" : "Dark";
    }

    private void LoadSettings()
    {
        LoadSettingsFrom(SettingsService.Current);
    }

    private void LoadSettingsFrom(CmuxSettings s)
    {
        FontFamilyCombo.SelectedItem = s.FontFamily;
        if (FontFamilyCombo.SelectedItem == null)
            FontFamilyCombo.Text = s.FontFamily;

        FontSizeSlider.Value = Math.Clamp(s.FontSize, 9, 28);
        UpdateFontSizeText();

        _suppressThemeSync = true;
        ThemeCombo.SelectedItem = s.ThemeName;
        TerminalThemePresetCombo.SelectedItem = s.ThemeName;
        _suppressThemeSync = false;

        OpacitySlider.Value = s.Opacity;
        UpdateOpacityText();
        CursorStyleCombo.SelectedItem = s.CursorStyle;
        CursorBlinkCheck.IsChecked = s.CursorBlink;

        // Shell selection (set after PopulateThemes populates the combo)
        var shellPath = s.DefaultShell;
        var shells = ShellCombo.ItemsSource as List<ShellInfo>;
        var shellIndex = shells?.FindIndex(sh => sh.Path == shellPath) ?? -1;
        ShellCombo.SelectedIndex = shellIndex >= 0 ? shellIndex : 0;

        ShellArgsBox.Text = s.DefaultShellArgs;
        ScrollbackBox.Text = s.ScrollbackLines.ToString();
        VisualBellCheck.IsChecked = s.VisualBell;
        BracketedPasteCheck.IsChecked = s.BracketedPaste;

        RestoreSessionCheck.IsChecked = s.RestoreSessionOnStartup;
        ConfirmCloseCheck.IsChecked = s.ConfirmOnClose;
        AutoCopyCheck.IsChecked = s.AutoCopyOnSelect;
        CtrlClickUrlCheck.IsChecked = s.CtrlClickOpensUrls;
        AutoSaveBox.Text = s.AutoSaveIntervalSeconds.ToString();
        LogRetentionDaysBox.Text = Math.Clamp(s.CommandLogRetentionDays, 0, 3650).ToString();
        CaptureOnCloseCheck.IsChecked = s.CaptureTranscriptsOnClose;
        CaptureOnClearCheck.IsChecked = s.CaptureTranscriptsOnClear;
        TranscriptRetentionDaysBox.Text = Math.Clamp(s.TranscriptRetentionDays, 0, 3650).ToString();

        UseCustomTerminalColorsCheck.IsChecked = s.UseCustomTerminalColors;

        var preset = TerminalThemes.Get(s.ThemeName);
        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = NormalizeHexColor(s.CustomTerminalBackground) ?? TerminalThemes.ToHex(preset.Background);
        TerminalForegroundHexBox.Text = NormalizeHexColor(s.CustomTerminalForeground) ?? TerminalThemes.ToHex(preset.Foreground);
        TerminalCursorHexBox.Text = NormalizeHexColor(s.CustomTerminalCursor) ?? TerminalThemes.ToHex(preset.CursorColor);
        TerminalSelectionHexBox.Text = NormalizeHexColor(s.CustomTerminalSelection) ?? TerminalThemes.ToHex(preset.SelectionBg);
        _suppressTerminalColorEvents = false;

        UpdateTerminalColorEditorsEnabledState();
        RefreshTerminalColorPreviews();
        UpdateThemePreview();
    }

    private void SaveSettings()
    {
        var s = SettingsService.Current;
        s.FontFamily = FontFamilyCombo.SelectedItem as string ?? FontFamilyCombo.Text;
        s.FontSize = (int)Math.Round(FontSizeSlider.Value);
        s.ThemeName = TerminalThemePresetCombo.SelectedItem as string
            ?? ThemeCombo.SelectedItem as string
            ?? "Default Dark";
        s.Opacity = OpacitySlider.Value;
        s.CursorStyle = CursorStyleCombo.SelectedItem as string ?? "bar";
        s.CursorBlink = CursorBlinkCheck.IsChecked == true;

        s.DefaultShell = ShellCombo.SelectedValue as string ?? "";
        s.DefaultShellArgs = ShellArgsBox.Text;
        if (int.TryParse(ScrollbackBox.Text, out int sb)) s.ScrollbackLines = sb;
        s.VisualBell = VisualBellCheck.IsChecked == true;
        s.BracketedPaste = BracketedPasteCheck.IsChecked == true;

        s.RestoreSessionOnStartup = RestoreSessionCheck.IsChecked == true;
        s.ConfirmOnClose = ConfirmCloseCheck.IsChecked == true;
        s.AutoCopyOnSelect = AutoCopyCheck.IsChecked == true;
        s.CtrlClickOpensUrls = CtrlClickUrlCheck.IsChecked == true;
        if (int.TryParse(AutoSaveBox.Text, out int asv)) s.AutoSaveIntervalSeconds = asv;
        if (int.TryParse(LogRetentionDaysBox.Text, out int retentionDays))
            s.CommandLogRetentionDays = Math.Clamp(retentionDays, 0, 3650);
        s.CaptureTranscriptsOnClose = CaptureOnCloseCheck.IsChecked == true;
        s.CaptureTranscriptsOnClear = CaptureOnClearCheck.IsChecked == true;
        if (int.TryParse(TranscriptRetentionDaysBox.Text, out int transcriptRetention))
            s.TranscriptRetentionDays = Math.Clamp(transcriptRetention, 0, 3650);

        s.UseCustomTerminalColors = UseCustomTerminalColorsCheck.IsChecked == true;
        s.CustomTerminalBackground = NormalizeHexColor(TerminalBackgroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalForeground = NormalizeHexColor(TerminalForegroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalCursor = NormalizeHexColor(TerminalCursorHexBox.Text) ?? string.Empty;
        s.CustomTerminalSelection = NormalizeHexColor(TerminalSelectionHexBox.Text) ?? string.Empty;

        SettingsService.Save();
        SettingsService.NotifyChanged();
    }

    private void ShowSection(string section)
    {
        AppearanceSection.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        TerminalSection.Visibility = section == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorSection.Visibility = section == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        KeyboardSection.Visibility = section == "Keyboard" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;

        // Update nav button active state via Tag
        foreach (var btn in new[] { NavAppearance, NavTerminal, NavBehavior, NavKeyboard, NavAbout })
            btn.Tag = btn.Name == $"Nav{section}" ? "active" : null;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var section = btn.Name.Replace("Nav", "");
            ShowSection(section);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Reset();
        LoadSettings();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressThemeSync)
        {
            _suppressThemeSync = true;
            TerminalThemePresetCombo.SelectedItem = ThemeCombo.SelectedItem;
            _suppressThemeSync = false;
        }

        UpdateThemePreview();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void TerminalThemePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressThemeSync)
        {
            _suppressThemeSync = true;
            ThemeCombo.SelectedItem = TerminalThemePresetCombo.SelectedItem;
            _suppressThemeSync = false;
        }

        UpdateThemePreview();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void UpdateThemePreview()
    {
        var themeName = TerminalThemePresetCombo.SelectedItem as string
            ?? ThemeCombo.SelectedItem as string;

        if (themeName is null)
            return;

        var theme = TerminalThemes.Get(themeName);

        ThemePreview.Background = new SolidColorBrush(
            Color.FromRgb(theme.Background.R, theme.Background.G, theme.Background.B));
        ThemePreviewText.Foreground = new SolidColorBrush(
            Color.FromRgb(theme.Foreground.R, theme.Foreground.G, theme.Foreground.B));
    }

    private void UpdateOpacityText()
    {
        if (OpacityValueText != null)
            OpacityValueText.Text = $"{OpacitySlider.Value:P0}";
    }

    private void UpdateFontSizeText()
    {
        if (FontSizeValueText != null)
            FontSizeValueText.Text = $"{(int)Math.Round(FontSizeSlider.Value)} px";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontSizeText();
    }

    private static string? NormalizeHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        if (value.Length == 7)
            value = "#FF" + value[1..];

        if (value.Length != 9)
            return null;

        if (TerminalThemes.TryParseHexColor(value, out _))
            return value.ToUpperInvariant();

        return null;
    }

    private void SetColorField(TextBox box, Border preview, string? colorText)
    {
        var normalized = NormalizeHexColor(colorText);
        if (normalized == null)
            return;

        _suppressTerminalColorEvents = true;
        box.Text = normalized;
        _suppressTerminalColorEvents = false;

        if (TerminalThemes.TryParseHexColor(normalized, out var color))
            preview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private void RefreshTerminalColorPreviews()
    {
        SetColorField(TerminalBackgroundHexBox, TerminalBackgroundPreview, TerminalBackgroundHexBox.Text);
        SetColorField(TerminalForegroundHexBox, TerminalForegroundPreview, TerminalForegroundHexBox.Text);
        SetColorField(TerminalCursorHexBox, TerminalCursorPreview, TerminalCursorHexBox.Text);
        SetColorField(TerminalSelectionHexBox, TerminalSelectionPreview, TerminalSelectionHexBox.Text);
    }

    private void UpdateTerminalColorEditorsEnabledState()
    {
        var enabled = UseCustomTerminalColorsCheck.IsChecked == true;
        TerminalBackgroundColorPanel.IsEnabled = enabled;
        TerminalForegroundColorPanel.IsEnabled = enabled;
        TerminalCursorColorPanel.IsEnabled = enabled;
        TerminalSelectionColorPanel.IsEnabled = enabled;
    }

    private void RefreshCustomColorsFromPresetIfNeeded()
    {
        if (UseCustomTerminalColorsCheck.IsChecked == true)
            return;

        if (TerminalThemePresetCombo.SelectedItem is not string presetName)
            return;

        var theme = TerminalThemes.Get(presetName);

        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = TerminalThemes.ToHex(theme.Background);
        TerminalForegroundHexBox.Text = TerminalThemes.ToHex(theme.Foreground);
        TerminalCursorHexBox.Text = TerminalThemes.ToHex(theme.CursorColor);
        TerminalSelectionHexBox.Text = TerminalThemes.ToHex(theme.SelectionBg);
        _suppressTerminalColorEvents = false;

        RefreshTerminalColorPreviews();
    }

    private void UseCustomTerminalColorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTerminalColorEditorsEnabledState();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void TerminalColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTerminalColorEvents)
            return;

        RefreshTerminalColorPreviews();
    }

    private string PickColor(string initial)
    {
        var picker = new ColorPickerWindow(initial) { Owner = this };
        return picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedHex)
            ? picker.SelectedHex
            : initial;
    }

    private void PickTerminalBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalBackgroundHexBox, TerminalBackgroundPreview, PickColor(TerminalBackgroundHexBox.Text));
    }

    private void PickTerminalForegroundColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalForegroundHexBox, TerminalForegroundPreview, PickColor(TerminalForegroundHexBox.Text));
    }

    private void PickTerminalCursorColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalCursorHexBox, TerminalCursorPreview, PickColor(TerminalCursorHexBox.Text));
    }

    private void PickTerminalSelectionColor_Click(object sender, RoutedEventArgs e)
    {
        SetColorField(TerminalSelectionHexBox, TerminalSelectionPreview, PickColor(TerminalSelectionHexBox.Text));
    }

    private void ResetTerminalColors_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalThemePresetCombo.SelectedItem is not string presetName)
            return;

        var theme = TerminalThemes.Get(presetName);
        _suppressTerminalColorEvents = true;
        TerminalBackgroundHexBox.Text = TerminalThemes.ToHex(theme.Background);
        TerminalForegroundHexBox.Text = TerminalThemes.ToHex(theme.Foreground);
        TerminalCursorHexBox.Text = TerminalThemes.ToHex(theme.CursorColor);
        TerminalSelectionHexBox.Text = TerminalThemes.ToHex(theme.SelectionBg);
        _suppressTerminalColorEvents = false;
        RefreshTerminalColorPreviews();
    }

    private List<ShellInfo> DetectShells()
    {
        var shells = new List<ShellInfo>();

        try
        {
            // PowerShell 7+
            var pwshRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
            if (Directory.Exists(pwshRoot))
            {
                var pwshPaths = Directory.GetFiles(pwshRoot, "pwsh.exe", SearchOption.AllDirectories);
                foreach (var path in pwshPaths.OrderByDescending(p => p)) // newest first
                    shells.Add(new ShellInfo("PowerShell 7", path));
            }
        } catch { /* ignore */ }

        try
        {
            // Windows PowerShell
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(powershell))
                shells.Add(new ShellInfo("Windows PowerShell", powershell));

            // Command Prompt
            var cmd = Path.Combine(system32, "cmd.exe");
            if (File.Exists(cmd))
                shells.Add(new ShellInfo("Command Prompt", cmd));

            // WSL
            var wslPath = Path.Combine(system32, "wsl.exe");
            if (File.Exists(wslPath))
                shells.Add(new ShellInfo("WSL", wslPath));
        } catch { /* ignore */ }

        try
        {
            // Git Bash
            var gitBashPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            };
            foreach (var path in gitBashPaths)
            {
                if (File.Exists(path))
                {
                    shells.Add(new ShellInfo("Git Bash", path));
                    break;
                }
            }
        } catch { /* ignore */ }

        return shells;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; } // Default to dark
    }

    private record ShellInfo(string Name, string Path);
}