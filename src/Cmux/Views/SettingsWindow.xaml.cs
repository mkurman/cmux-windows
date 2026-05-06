using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Cmux.Core.Config;
using Cmux.Core.Services;

namespace Cmux.Views;

public partial class SettingsWindow : Window
{
    private bool _suppressTerminalColorEvents;
    private bool _suppressThemeSync;

    public SettingsWindow(string initialSection = "Appearance")
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        PopulateThemes();
        LoadSettings();
        ShowSection(initialSection);
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
        var shells = ShellDetector.DetectShells();
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
        CursorBlinkCheck.IsOn = s.CursorBlink;

        // Shell selection (set after PopulateThemes populates the combo)
        var shellPath = s.DefaultShell;
        var shells = ShellCombo.ItemsSource as List<ShellInfo>;
        var shellIndex = shells?.FindIndex(sh => sh.Path == shellPath) ?? -1;
        ShellCombo.SelectedIndex = shellIndex >= 0 ? shellIndex : 0;

        ShellArgsBox.Text = s.DefaultShellArgs;
        ScrollbackBox.Value = s.ScrollbackLines;
        VisualBellCheck.IsOn = s.VisualBell;
        BracketedPasteCheck.IsOn = s.BracketedPaste;

        RestoreSessionCheck.IsOn = s.RestoreSessionOnStartup;
        ConfirmCloseCheck.IsOn = s.ConfirmOnClose;
        AutoCopyCheck.IsOn = s.AutoCopyOnSelect;
        RightClickPasteCheck.IsOn = s.RightClickPaste;
        CtrlClickUrlCheck.IsOn = s.CtrlClickOpensUrls;
        ToastEnabledCheck.IsOn = s.EnableToastNotifications;
        ToastWhileFocusedCheck.IsOn = s.ShowToastsWhileFocused;
        AutoSaveBox.Value = s.AutoSaveIntervalSeconds;
        LogRetentionDaysBox.Value = Math.Clamp(s.CommandLogRetentionDays, 0, 3650);
        CaptureOnCloseCheck.IsOn = s.CaptureTranscriptsOnClose;
        CaptureOnClearCheck.IsOn = s.CaptureTranscriptsOnClear;
        TranscriptRetentionDaysBox.Value = Math.Clamp(s.TranscriptRetentionDays, 0, 3650);

        UseCustomTerminalColorsCheck.IsOn = s.UseCustomTerminalColors;

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

    private bool SaveSettings()
    {
        var s = SettingsService.Current;
        s.FontFamily = FontFamilyCombo.SelectedItem as string ?? FontFamilyCombo.Text;
        s.FontSize = (int)Math.Round(FontSizeSlider.Value);
        s.ThemeName = TerminalThemePresetCombo.SelectedItem as string
            ?? ThemeCombo.SelectedItem as string
            ?? "Default Dark";
        s.Opacity = OpacitySlider.Value;
        s.CursorStyle = CursorStyleCombo.SelectedItem as string ?? "bar";
        s.CursorBlink = CursorBlinkCheck.IsOn;

        s.DefaultShell = ShellCombo.SelectedValue as string ?? "";
        s.DefaultShellArgs = ShellArgsBox.Text;
        if (!double.IsNaN(ScrollbackBox.Value)) s.ScrollbackLines = (int)ScrollbackBox.Value;
        s.VisualBell = VisualBellCheck.IsOn;
        s.BracketedPaste = BracketedPasteCheck.IsOn;

        s.RestoreSessionOnStartup = RestoreSessionCheck.IsOn;
        s.ConfirmOnClose = ConfirmCloseCheck.IsOn;
        s.AutoCopyOnSelect = AutoCopyCheck.IsOn;
        s.RightClickPaste = RightClickPasteCheck.IsOn;
        s.CtrlClickOpensUrls = CtrlClickUrlCheck.IsOn;
        s.EnableToastNotifications = ToastEnabledCheck.IsOn;
        s.ShowToastsWhileFocused = ToastWhileFocusedCheck.IsOn;
        if (!double.IsNaN(AutoSaveBox.Value)) s.AutoSaveIntervalSeconds = (int)AutoSaveBox.Value;
        if (!double.IsNaN(LogRetentionDaysBox.Value))
            s.CommandLogRetentionDays = Math.Clamp((int)LogRetentionDaysBox.Value, 0, 3650);
        s.CaptureTranscriptsOnClose = CaptureOnCloseCheck.IsOn;
        s.CaptureTranscriptsOnClear = CaptureOnClearCheck.IsOn;
        if (!double.IsNaN(TranscriptRetentionDaysBox.Value))
            s.TranscriptRetentionDays = Math.Clamp((int)TranscriptRetentionDaysBox.Value, 0, 3650);

        s.UseCustomTerminalColors = UseCustomTerminalColorsCheck.IsOn;
        s.CustomTerminalBackground = NormalizeHexColor(TerminalBackgroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalForeground = NormalizeHexColor(TerminalForegroundHexBox.Text) ?? string.Empty;
        s.CustomTerminalCursor = NormalizeHexColor(TerminalCursorHexBox.Text) ?? string.Empty;
        s.CustomTerminalSelection = NormalizeHexColor(TerminalSelectionHexBox.Text) ?? string.Empty;

        SettingsService.Save();
        SettingsService.NotifyChanged();
        return true;
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
        if (!SaveSettings())
            return;

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
        var enabled = UseCustomTerminalColorsCheck.IsOn;
        TerminalBackgroundColorPanel.IsEnabled = enabled;
        TerminalForegroundColorPanel.IsEnabled = enabled;
        TerminalCursorColorPanel.IsEnabled = enabled;
        TerminalSelectionColorPanel.IsEnabled = enabled;
    }

    private void RefreshCustomColorsFromPresetIfNeeded()
    {
        if (UseCustomTerminalColorsCheck.IsOn)
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
        if (!IsLoaded) return; // Toggled fires once during InitializeComponent before fields exist
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

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; } // Default to dark
    }

    private static int ResolveSubmitKeyComboIndex(string? submitKey)
    {
        var normalized = (submitKey ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "enter" => 1,
            "linefeed" => 2,
            "crlf" => 3,
            _ => 0,
        };
    }

}
