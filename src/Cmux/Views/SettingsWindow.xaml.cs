using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cmux.Core.Config;
using Cmux.Core.Services;

namespace Cmux.Views;

public partial class SettingsWindow : Window
{
    // Variants are scoped to a mode — picking Dark hides the Light variants and vice versa.
    // Pulled from Cmux.Themes.AppThemeCatalog so adding a palette there flows here for free.
    private static IEnumerable<(string Variant, string Mode)> AppThemeCatalog =>
        Cmux.Themes.AppThemeCatalog.All.Select(v => (v.Name, v.Mode));

    private bool _suppressTerminalColorEvents;
    private bool _suppressAppThemeSync;
    private string _initialAppThemeMode = "Dark";
    private string _initialAppThemeVariant = "Default Dark";

    public SettingsWindow(string initialSection = "Appearance")
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        AppVersionText.Text = App.AppVersion;
        PopulateThemes();
        LoadSettings();
        ShowSection(initialSection);
    }

    private void PopulateThemes()
    {
        ThemeCombo.ItemsSource = TerminalThemes.Names;
        CursorStyleCombo.ItemsSource = new[] { "bar", "block", "underline" };

        var fontFamilies = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name)
            .ToList();
        FontFamilyCombo.ItemsSource = fontFamilies;

        // Detect available shells
        var shells = ShellDetector.DetectShells();
        ShellCombo.ItemsSource = shells.OrderBy(shell => shell.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ShellCombo.DisplayMemberPath = "Name";
        ShellCombo.SelectedValuePath = "Path";

        // App-level theme: mode (Light/Dark) + named variant. Restart-required to take effect.
        // Variant list is filtered in RepopulateAppThemeVariants() based on the selected mode.
        AppThemeModeCombo.ItemsSource = new[] { "Dark", "Light" };
    }

    private void RepopulateAppThemeVariants(string mode, string? preferredSelection)
    {
        _suppressAppThemeSync = true;
        // Alphabetical for a scannable list — see feedback_alphabetize-dropdowns.md
        var variants = AppThemeCatalog
            .Where(v => string.Equals(v.Mode, mode, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Variant)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AppThemeVariantCombo.ItemsSource = variants;
        AppThemeVariantCombo.SelectedItem = variants.Contains(preferredSelection)
            ? preferredSelection
            : variants.FirstOrDefault();
        _suppressAppThemeSync = false;
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

        ThemeCombo.SelectedItem = s.ThemeName;

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

        // Snapshot current values so AppThemeSetting_Changed can detect dirty-vs-saved
        // and only show the restart hint when the user actually changes something.
        _initialAppThemeMode = NormalizeAppThemeMode(s.AppThemeMode);
        _initialAppThemeVariant = string.IsNullOrWhiteSpace(s.AppThemeVariant) ? "Default Dark" : s.AppThemeVariant;
        _suppressAppThemeSync = true;
        AppThemeModeCombo.SelectedItem = _initialAppThemeMode;
        _suppressAppThemeSync = false;
        RepopulateAppThemeVariants(_initialAppThemeMode, _initialAppThemeVariant);
        AppThemeRestartHint.Visibility = Visibility.Collapsed;

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

    private async System.Threading.Tasks.Task<bool> SaveSettingsAsync()
    {
        // Guard: Mode/Variant mismatch (e.g., Dark mode + Default Light variant) makes no sense
        // and would load the wrong dictionary on next launch. The variant combo filters by mode,
        // so this should never trigger via normal UI flow — but a defensive check is cheap.
        var pendingMode = NormalizeAppThemeMode(AppThemeModeCombo.SelectedItem as string);
        var pendingVariant = AppThemeVariantCombo.SelectedItem as string;
        if (!IsVariantValidForMode(pendingVariant, pendingMode))
        {
            await new ModernWpf.Controls.ContentDialog
            {
                Title = "Theme mismatch",
                Content = $"The selected App Theme '{pendingVariant ?? "(none)"}' isn't a {pendingMode} theme. Pick a matching variant before saving.",
                CloseButtonText = "OK",
            }.ShowAsync();
            ShowSection("Appearance");
            AppThemeVariantCombo.Focus();
            return false;
        }

        var s = SettingsService.Current;
        s.FontFamily = FontFamilyCombo.SelectedItem as string ?? FontFamilyCombo.Text;
        s.FontSize = (int)Math.Round(FontSizeSlider.Value);
        s.ThemeName = ThemeCombo.SelectedItem as string ?? "Default Dark";
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

        s.AppThemeMode = NormalizeAppThemeMode(AppThemeModeCombo.SelectedItem as string);
        s.AppThemeVariant = AppThemeVariantCombo.SelectedItem as string ?? "Default Dark";

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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot pre-save values so we can detect restart-required changes after Save.
        var prevMode = _initialAppThemeMode;
        var prevVariant = _initialAppThemeVariant;

        if (!await SaveSettingsAsync())
            return;

        var s = SettingsService.Current;
        var themeChanged =
            !string.Equals(s.AppThemeMode, prevMode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(s.AppThemeVariant, prevVariant, StringComparison.Ordinal);

        if (themeChanged)
        {
            var dialog = new ModernWpf.Controls.ContentDialog
            {
                Title = "Restart required",
                Content = "App theme changes only take effect after a restart. Restart cmux now?",
                PrimaryButtonText = "Restart now",
                SecondaryButtonText = "Later",
                DefaultButton = ModernWpf.Controls.ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ModernWpf.Controls.ContentDialogResult.Primary)
            {
                RestartApp();
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private static void RestartApp()
    {
        // Spawn a detached cmd that waits briefly for this process to exit (releasing the
        // single-instance mutex), then relaunches the same EXE. Without the wait the new
        // instance races us and gets blocked by the "cmux is already running" guard.
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Application.Current.Shutdown();
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >NUL && start \"\" \"{exePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        catch { /* fall through to shutdown anyway — user can relaunch manually */ }

        Application.Current.Shutdown();
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
        UpdateThemePreview();
        RefreshCustomColorsFromPresetIfNeeded();
    }

    private void UpdateThemePreview()
    {
        var themeName = ThemeCombo.SelectedItem as string;

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
        TerminalColorOverridesSection.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TerminalBackgroundColorPanel.IsEnabled = enabled;
        TerminalForegroundColorPanel.IsEnabled = enabled;
        TerminalCursorColorPanel.IsEnabled = enabled;
        TerminalSelectionColorPanel.IsEnabled = enabled;
    }

    private void RefreshCustomColorsFromPresetIfNeeded()
    {
        if (UseCustomTerminalColorsCheck.IsOn)
            return;

        if (ThemeCombo.SelectedItem is not string presetName)
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
        if (ThemeCombo.SelectedItem is not string presetName)
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

    private static string NormalizeAppThemeMode(string? raw)
    {
        return string.Equals(raw?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
    }

    private static bool IsVariantValidForMode(string? variant, string mode)
    {
        if (string.IsNullOrWhiteSpace(variant)) return false;
        var match = Cmux.Themes.AppThemeCatalog.Find(variant);
        return match != null && string.Equals(match.Mode, mode, StringComparison.OrdinalIgnoreCase);
    }

    private void AppThemeSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressAppThemeSync) return;

        // Mode change → repopulate variant list so the user can't pick a Light variant
        // while the app is in Dark mode (and vice versa).
        if (ReferenceEquals(sender, AppThemeModeCombo))
        {
            var newMode = NormalizeAppThemeMode(AppThemeModeCombo.SelectedItem as string);
            RepopulateAppThemeVariants(newMode, _initialAppThemeVariant);
        }

        var modeChanged = !string.Equals(AppThemeModeCombo.SelectedItem as string,
            _initialAppThemeMode, StringComparison.OrdinalIgnoreCase);
        var variantChanged = !string.Equals(AppThemeVariantCombo.SelectedItem as string,
            _initialAppThemeVariant, StringComparison.Ordinal);

        AppThemeRestartHint.Visibility = (modeChanged || variantChanged)
            ? Visibility.Visible : Visibility.Collapsed;
    }

}
