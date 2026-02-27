using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cmux.Views;

public partial class HistoryWindow : Window
{
    private readonly List<string> _allCommands;
    private readonly Action<string> _insertAction;
    private readonly Action<string> _runAction;

    private sealed class HistoryEntryView
    {
        public string Command { get; init; } = string.Empty;
        public string IndexLabel { get; init; } = string.Empty;
    }

    public HistoryWindow(IEnumerable<string> history, Action<string> insertAction, Action<string> runAction)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);

        _allCommands = history
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Reverse()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _insertAction = insertAction;
        _runAction = runAction;

        RefreshEntries();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private HistoryEntryView? Selected => EntriesList.SelectedItem as HistoryEntryView;

    private void RefreshEntries()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;

        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCommands
            : _allCommands.Where(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var entries = filtered
            .Select((command, index) => new HistoryEntryView
            {
                Command = command,
                IndexLabel = (index + 1).ToString(),
            })
            .ToList();

        EntriesList.ItemsSource = entries;
        SummaryText.Text = entries.Count == 1 ? "1 command" : $"{entries.Count} commands";

        if (entries.Count > 0 && EntriesList.SelectedIndex < 0)
            EntriesList.SelectedIndex = 0;
    }

    private void ExecuteInsert()
    {
        if (Selected is not { Command.Length: > 0 } entry)
            return;

        _insertAction(entry.Command);
        DialogResult = true;
        Close();
    }

    private void ExecuteRun()
    {
        if (Selected is not { Command.Length: > 0 } entry)
            return;

        _runAction(entry.Command);
        DialogResult = true;
        Close();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshEntries();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (EntriesList.Items.Count > 0)
                {
                    if (EntriesList.SelectedIndex < 0)
                        EntriesList.SelectedIndex = 0;
                    EntriesList.Focus();
                }
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                ExecuteInsert();
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteRun();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void EntriesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                ExecuteInsert();
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteRun();
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                Copy_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { Command.Length: > 0 } entry)
            Clipboard.SetText(entry.Command);
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        ExecuteInsert();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        ExecuteRun();
    }

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteRun();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
