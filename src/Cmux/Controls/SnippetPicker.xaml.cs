using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cmux.Core.Models;

namespace Cmux.Controls;

public partial class SnippetPicker : UserControl
{
    public event Action<Snippet>? SnippetSelected;
    public event Action? Closed;

    private string? _editingSnippetId;

    public SnippetPicker()
    {
        InitializeComponent();
        App.SnippetService.SnippetsChanged += OnSnippetsChanged;
        Unloaded += (_, _) => App.SnippetService.SnippetsChanged -= OnSnippetsChanged;
    }

    private void OnSnippetsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Visibility == Visibility.Visible)
                RefreshList();
        });
    }

    public void RefreshList()
    {
        var query = SearchBox.Text;
        var snippets = string.IsNullOrWhiteSpace(query)
            ? App.SnippetService.All
            : App.SnippetService.Search(query);

        SnippetsList.ItemsSource = snippets;
        SnippetsList.SelectedIndex = snippets.Count > 0 ? 0 : -1;
        UpdateEmptyState(snippets.Count);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N)
        {
            ShowNewSnippetEditor();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (NewSnippetPanel.Visibility == Visibility.Visible)
                    HideNewSnippetEditor();
                else
                    Closed?.Invoke();
                e.Handled = true;
                break;
            case Key.Enter:
                if (SnippetsList.SelectedItem is Snippet snippet)
                    ExecuteSnippet(snippet);
                e.Handled = true;
                break;
            case Key.Down:
                if (SnippetsList.SelectedIndex < SnippetsList.Items.Count - 1)
                    SnippetsList.SelectedIndex++;
                SnippetsList.ScrollIntoView(SnippetsList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (SnippetsList.SelectedIndex > 0)
                    SnippetsList.SelectedIndex--;
                SnippetsList.ScrollIntoView(SnippetsList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void ExecuteSnippet(Snippet snippet)
    {
        SnippetSelected?.Invoke(snippet);
    }

    private void SnippetsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SnippetsList.SelectedItem is Snippet snippet)
            ExecuteSnippet(snippet);
    }

    private void SnippetsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        if (FindAncestor<Button>(source) != null)
            return;

        var item = ItemsControl.ContainerFromElement(SnippetsList, source) as ListBoxItem;
        if (item?.DataContext is Snippet snippet)
        {
            ExecuteSnippet(snippet);
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Closed?.Invoke();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void UpdateEmptyState(int count)
    {
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnippetsList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewSnippet_Click(object sender, RoutedEventArgs e)
    {
        ShowNewSnippetEditor();
    }

    private void SaveNewSnippet_Click(object sender, RoutedEventArgs e)
    {
        var content = NewSnippetContent.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show("Snippet command/content cannot be empty.", "Snippet", MessageBoxButton.OK, MessageBoxImage.Information);
            NewSnippetContent.Focus();
            return;
        }

        var name = NewSnippetName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = content.Length > 40 ? content[..40] + "…" : content;

        var category = NewSnippetCategory.Text.Trim();
        if (string.IsNullOrWhiteSpace(category))
            category = "Custom";

        if (!string.IsNullOrWhiteSpace(_editingSnippetId))
        {
            var existing = App.SnippetService.All.FirstOrDefault(s => s.Id == _editingSnippetId);
            if (existing != null)
            {
                var updated = CloneSnippet(existing);
                updated.Name = name;
                updated.Category = category;
                updated.Content = content;
                App.SnippetService.Update(updated);
            }
        }
        else
        {
            var snippet = new Snippet
            {
                Name = name,
                Content = content,
                Category = category,
                Tags = [],
                Description = "User snippet",
                IsFavorite = false,
            };

            App.SnippetService.Add(snippet);
        }

        HideNewSnippetEditor();
        SearchBox.Text = string.Empty;
        RefreshList();
        FocusSearch();
    }

    private void CancelNewSnippet_Click(object sender, RoutedEventArgs e)
    {
        HideNewSnippetEditor();
        FocusSearch();
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var snippet = GetSnippetFromSender(sender);
        if (snippet == null) return;

        var updated = CloneSnippet(snippet);
        updated.IsFavorite = !snippet.IsFavorite;
        App.SnippetService.Update(updated);

        e.Handled = true;
    }

    private void EditSnippet_Click(object sender, RoutedEventArgs e)
    {
        var snippet = GetSnippetFromSender(sender);
        if (snippet == null) return;

        _editingSnippetId = snippet.Id;
        EditorTitle.Text = "Edit snippet";
        SaveSnippetButton.Content = "Update Snippet";
        NewSnippetPanel.Visibility = Visibility.Visible;

        NewSnippetName.Text = snippet.Name;
        NewSnippetCategory.Text = snippet.Category;
        NewSnippetContent.Text = snippet.Content;

        NewSnippetName.Focus();
        NewSnippetName.SelectAll();

        e.Handled = true;
    }

    private void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        var snippet = GetSnippetFromSender(sender);
        if (snippet == null) return;

        var result = MessageBox.Show(
            $"Delete snippet '{snippet.Name}'?",
            "Delete Snippet",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            App.SnippetService.Delete(snippet.Id);

            if (_editingSnippetId == snippet.Id)
                HideNewSnippetEditor();

            RefreshList();
        }

        e.Handled = true;
    }

    private void ShowNewSnippetEditor()
    {
        _editingSnippetId = null;
        EditorTitle.Text = "New snippet";
        SaveSnippetButton.Content = "Save Snippet";
        NewSnippetPanel.Visibility = Visibility.Visible;

        NewSnippetName.Text = string.Empty;
        NewSnippetCategory.Text = "Custom";
        NewSnippetContent.Text = SearchBox.Text.Trim();

        NewSnippetName.Focus();
        NewSnippetName.SelectAll();
    }

    private void HideNewSnippetEditor()
    {
        _editingSnippetId = null;
        NewSnippetPanel.Visibility = Visibility.Collapsed;
    }

    private static Snippet? GetSnippetFromSender(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is Snippet dataContextSnippet)
                return dataContextSnippet;

            if (fe.Tag is Snippet tagSnippet)
                return tagSnippet;
        }

        return null;
    }

    private static Snippet CloneSnippet(Snippet snippet)
    {
        return new Snippet
        {
            Id = snippet.Id,
            Name = snippet.Name,
            Content = snippet.Content,
            Category = snippet.Category,
            Tags = snippet.Tags.ToList(),
            Description = snippet.Description,
            CreatedAt = snippet.CreatedAt,
            UpdatedAt = snippet.UpdatedAt,
            UseCount = snippet.UseCount,
            IsFavorite = snippet.IsFavorite,
        };
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T found)
                return found;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
