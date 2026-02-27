using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

public partial class WorkspaceSidebarItem : UserControl
{
    public WorkspaceSidebarItem()
    {
        InitializeComponent();
    }

    private WorkspaceViewModel? Vm => DataContext as WorkspaceViewModel;
    private MainViewModel? MainVm => FindMainViewModel();

    private void Rename_Click(object sender, RoutedEventArgs e) => StartRename();

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            main.DuplicateWorkspace(ws);
        }
    }

    private void NewSurface_Click(object sender, RoutedEventArgs e) => Vm?.CreateNewSurface();

    private void SetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var prompt = new TextPromptWindow(
            title: "Workspace Icon",
            message: "Enter a single icon (emoji/symbol) or a glyph code like E8A5, U+E8A5, 0xE8A5.",
            defaultValue: Vm.IconGlyph)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() != true)
            return;

        var input = prompt.ResponseText;
        if (string.IsNullOrWhiteSpace(input))
            return;

        var value = input.Trim();

        if (value.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("SVG is not supported in workspace icon yet. Use emoji/symbol or MDL2 hex code.",
                "Workspace Icon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TryParseHexGlyph(value, out var glyph))
            Vm.IconGlyph = glyph;
        else
            Vm.IconGlyph = value;
    }

    private void SetColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not MenuItem item || item.Tag is not string color)
            return;

        Vm.AccentColor = color;
    }

    private void SetCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null)
            return;

        var picker = new ColorPickerWindow(Vm.AccentColor)
        {
            Owner = Window.GetWindow(this),
        };

        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedHex))
            Vm.AccentColor = picker.SelectedHex;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            int idx = main.Workspaces.IndexOf(ws);
            if (idx > 0) main.Workspaces.Move(idx, idx - 1);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            int idx = main.Workspaces.IndexOf(ws);
            if (idx >= 0 && idx < main.Workspaces.Count - 1)
                main.Workspaces.Move(idx, idx + 1);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
            main.CloseWorkspace(ws);
    }

    private void StartRename()
    {
        NameDisplay.Visibility = Visibility.Collapsed;
        NameEditor.Visibility = Visibility.Visible;
        NameEditor.SelectAll();
        NameEditor.Focus();
    }

    private void FinishRename()
    {
        NameEditor.Visibility = Visibility.Collapsed;
        NameDisplay.Visibility = Visibility.Visible;
    }

    private void NameDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            StartRename();
            e.Handled = true;
        }
    }

    private void NameEditor_LostFocus(object sender, RoutedEventArgs e) => FinishRename();

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            if (e.Key == Key.Escape && Vm != null)
                NameEditor.Text = Vm.Name; // revert
            FinishRename();
            e.Handled = true;
        }
    }

    private static bool TryParseHexGlyph(string input, out string glyph)
    {
        glyph = string.Empty;

        var normalized = input.Trim();
        if (normalized.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        else if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (!uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            return false;

        if (codePoint > 0x10FFFF)
            return false;

        glyph = char.ConvertFromUtf32((int)codePoint);
        return true;
    }

    private MainViewModel? FindMainViewModel()
    {
        var window = Window.GetWindow(this);
        return window?.DataContext as MainViewModel;
    }
}
