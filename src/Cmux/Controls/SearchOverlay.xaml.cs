using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cmux.Controls;

public partial class SearchOverlay : UserControl
{
    public event Action<string>? SearchTextChanged;
    public event Action? NextMatchRequested;
    public event Action? PreviousMatchRequested;
    public event Action? SearchClosed;

    public SearchOverlay()
    {
        InitializeComponent();
    }

    public void FocusInput() => SearchInput.Focus();

    public void UpdateMatchCount(int current, int total)
    {
        MatchCount.Text = total > 0 ? $"{current + 1}/{total}" : "0/0";
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        => SearchTextChanged?.Invoke(SearchInput.Text);

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                PreviousMatchRequested?.Invoke();
            else
                NextMatchRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchClosed?.Invoke();
            e.Handled = true;
        }
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => PreviousMatchRequested?.Invoke();
    private void NextMatch_Click(object sender, RoutedEventArgs e) => NextMatchRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => SearchClosed?.Invoke();
}
