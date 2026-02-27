using System.Windows;

namespace Cmux.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var theme = Cmux.Core.Config.GhosttyConfigReader.ReadConfig();
        FontFamilyBox.Text = theme.FontFamily;
        FontSizeBox.Text = theme.FontSize.ToString();
        ShellBox.Text = ""; // Auto-detect
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Settings are currently read-only from Ghostty config.
        // Future: save overrides to %LOCALAPPDATA%\cmux\settings.json
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
