using System.Windows;
using System.Windows.Input;

namespace Cmux.Views;

public partial class TextPromptWindow : Window
{
    public string ResponseText => InputTextBox.Text;

    public TextPromptWindow(string title, string message, string? defaultValue = null)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);

        Title = title;
        PromptText.Text = message;
        InputTextBox.Text = defaultValue ?? string.Empty;

        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
            e.Handled = true;
        }
    }
}
