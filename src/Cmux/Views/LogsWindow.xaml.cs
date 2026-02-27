using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.ViewModels;

namespace Cmux.Views;

public partial class LogsWindow : Window
{
    private readonly List<DateOnly> _dates = [];
    private readonly Dictionary<string, string> _workspaceNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _surfaceNames = new(StringComparer.Ordinal);
    private readonly List<CommandLogEntry> _entriesForDate = [];
    private bool _suppressFilterEvents;

    public LogsWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);

        WorkspaceFilterCombo.DisplayMemberPath = "Label";
        WorkspaceFilterCombo.SelectedValuePath = "Id";
        SurfaceFilterCombo.DisplayMemberPath = "Label";
        SurfaceFilterCombo.SelectedValuePath = "Id";
        PaneFilterCombo.DisplayMemberPath = "Label";
        PaneFilterCombo.SelectedValuePath = "Id";

        LoadDates();
        Loaded += (_, _) => LoadEntriesForSelectedDate();
        Closed += (_, _) => App.CommandLogService.LogChanged -= OnLogChanged;
        App.CommandLogService.LogChanged += OnLogChanged;
    }

    private sealed class FilterOption
    {
        public string Id { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
    }

    private sealed class LogEntryView
    {
        public CommandLogEntry Entry { get; init; } = null!;
        public string WorkspaceDisplay { get; init; } = "-";
        public string SurfaceDisplay { get; init; } = "-";
        public string PaneDisplay { get; init; } = "-";

        public string StartedAtLocal => Entry.StartedAt.ToLocalTime().ToString("HH:mm:ss");
        public string Command => Entry.Command ?? string.Empty;
        public string WorkingDirectoryDisplay => string.IsNullOrWhiteSpace(Entry.WorkingDirectory) ? "-" : Entry.WorkingDirectory!;
        public string ExitCodeText => Entry.ExitCode?.ToString() ?? "-";
        public string DurationDisplay => Entry.DurationDisplay;

        public static string ShortId(string id) => string.IsNullOrWhiteSpace(id)
            ? "-"
            : id.Length <= 8 ? id : id[..8];
    }

    private void RefreshNameMaps()
    {
        _workspaceNames.Clear();
        _surfaceNames.Clear();

        if (Owner?.DataContext is MainViewModel ownerVm)
        {
            foreach (var workspace in ownerVm.Workspaces)
            {
                if (!string.IsNullOrWhiteSpace(workspace.Workspace.Id) && !string.IsNullOrWhiteSpace(workspace.Name))
                    _workspaceNames[workspace.Workspace.Id] = workspace.Name;

                foreach (var surface in workspace.Surfaces)
                {
                    if (!string.IsNullOrWhiteSpace(surface.Surface.Id) && !string.IsNullOrWhiteSpace(surface.Name))
                        _surfaceNames[surface.Surface.Id] = surface.Name;
                }
            }
        }

        var persisted = SessionPersistenceService.Load();
        if (persisted == null)
            return;

        foreach (var workspace in persisted.Workspaces)
        {
            if (!_workspaceNames.ContainsKey(workspace.Id) && !string.IsNullOrWhiteSpace(workspace.Name))
                _workspaceNames[workspace.Id] = workspace.Name;

            foreach (var surface in workspace.Surfaces)
            {
                if (!_surfaceNames.ContainsKey(surface.Id) && !string.IsNullOrWhiteSpace(surface.Name))
                    _surfaceNames[surface.Id] = surface.Name;
            }
        }
    }

    private string ResolveWorkspaceName(string workspaceId)
    {
        return _workspaceNames.TryGetValue(workspaceId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : LogEntryView.ShortId(workspaceId);
    }

    private string ResolveSurfaceName(string surfaceId)
    {
        return _surfaceNames.TryGetValue(surfaceId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : LogEntryView.ShortId(surfaceId);
    }

    private static string FormatEntityLabel(string name, string id)
    {
        var shortId = LogEntryView.ShortId(id);
        return string.Equals(name, shortId, StringComparison.Ordinal)
            ? shortId
            : $"{name} · {shortId}";
    }

    private void OnLogChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            LoadDates();
            LoadEntriesForSelectedDate();
        });
    }

    private void LoadDates()
    {
        var previous = DateCombo.SelectedItem as string;

        _dates.Clear();
        _dates.AddRange(App.CommandLogService.GetAvailableDates());

        if (_dates.Count == 0)
            _dates.Add(DateOnly.FromDateTime(DateTime.Now));

        var labels = _dates.Select(d => d.ToString("yyyy-MM-dd")).ToList();
        DateCombo.ItemsSource = labels;

        if (!string.IsNullOrWhiteSpace(previous) && labels.Contains(previous))
            DateCombo.SelectedItem = previous;
        else
            DateCombo.SelectedIndex = 0;
    }

    private void PopulateFilterOptions()
    {
        var previousWorkspace = WorkspaceFilterCombo.SelectedValue as string ?? string.Empty;
        var previousSurface = SurfaceFilterCombo.SelectedValue as string ?? string.Empty;
        var previousPane = PaneFilterCombo.SelectedValue as string ?? string.Empty;

        var workspaceOptions = _entriesForDate
            .Select(e => e.WorkspaceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => ResolveWorkspaceName(id), StringComparer.OrdinalIgnoreCase)
            .Select(id => new FilterOption
            {
                Id = id,
                Label = FormatEntityLabel(ResolveWorkspaceName(id), id),
            })
            .ToList();

        var surfaceOptions = _entriesForDate
            .Select(e => e.SurfaceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => ResolveSurfaceName(id), StringComparer.OrdinalIgnoreCase)
            .Select(id => new FilterOption
            {
                Id = id,
                Label = FormatEntityLabel(ResolveSurfaceName(id), id),
            })
            .ToList();

        var paneOptions = _entriesForDate
            .Select(e => e.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => LogEntryView.ShortId(id), StringComparer.OrdinalIgnoreCase)
            .Select(id => new FilterOption
            {
                Id = id,
                Label = LogEntryView.ShortId(id),
            })
            .ToList();

        _suppressFilterEvents = true;

        WorkspaceFilterCombo.ItemsSource = (new[] { new FilterOption { Id = string.Empty, Label = "All workspaces" } })
            .Concat(workspaceOptions)
            .ToList();
        WorkspaceFilterCombo.SelectedValue = workspaceOptions.Any(x => x.Id == previousWorkspace) ? previousWorkspace : string.Empty;

        SurfaceFilterCombo.ItemsSource = (new[] { new FilterOption { Id = string.Empty, Label = "All surfaces" } })
            .Concat(surfaceOptions)
            .ToList();
        SurfaceFilterCombo.SelectedValue = surfaceOptions.Any(x => x.Id == previousSurface) ? previousSurface : string.Empty;

        PaneFilterCombo.ItemsSource = (new[] { new FilterOption { Id = string.Empty, Label = "All panes" } })
            .Concat(paneOptions)
            .ToList();
        PaneFilterCombo.SelectedValue = paneOptions.Any(x => x.Id == previousPane) ? previousPane : string.Empty;

        _suppressFilterEvents = false;
    }

    private void LoadEntriesForSelectedDate()
    {
        if (DateCombo.SelectedItem is not string selected || !DateOnly.TryParse(selected, out var date))
            return;

        RefreshNameMaps();

        _entriesForDate.Clear();
        _entriesForDate.AddRange(App.CommandLogService.GetForDate(date));

        PopulateFilterOptions();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_suppressFilterEvents)
            return;

        IEnumerable<CommandLogEntry> query = _entriesForDate;

        if (WorkspaceFilterCombo.SelectedValue is string workspaceId && !string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(e => string.Equals(e.WorkspaceId, workspaceId, StringComparison.Ordinal));

        if (SurfaceFilterCombo.SelectedValue is string surfaceId && !string.IsNullOrWhiteSpace(surfaceId))
            query = query.Where(e => string.Equals(e.SurfaceId, surfaceId, StringComparison.Ordinal));

        if (PaneFilterCombo.SelectedValue is string paneId && !string.IsNullOrWhiteSpace(paneId))
            query = query.Where(e => string.Equals(e.PaneId, paneId, StringComparison.Ordinal));

        var searchQuery = SearchBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            query = query.Where(e =>
                (!string.IsNullOrWhiteSpace(e.Command) && e.Command.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(e.WorkingDirectory) && e.WorkingDirectory.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = query
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new LogEntryView
            {
                Entry = e,
                WorkspaceDisplay = ResolveWorkspaceName(e.WorkspaceId),
                SurfaceDisplay = ResolveSurfaceName(e.SurfaceId),
                PaneDisplay = LogEntryView.ShortId(e.PaneId),
            })
            .ToList();

        EntriesList.ItemsSource = filtered;
        SummaryText.Text = filtered.Count == _entriesForDate.Count
            ? (filtered.Count == 1 ? "1 entry" : $"{filtered.Count} entries")
            : $"{filtered.Count} / {_entriesForDate.Count} entries";

        if (filtered.Count > 0)
            EntriesList.SelectedIndex = 0;
    }

    private LogEntryView? Selected => EntriesList.SelectedItem as LogEntryView;

    private bool ExecuteSelected(bool run)
    {
        if (Selected?.Entry.Command is not { Length: > 0 } command)
            return false;

        var ownerVm = Owner?.DataContext as MainViewModel;
        var surface = ownerVm?.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is not string paneId)
        {
            MessageBox.Show("No focused pane available.", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (run)
        {
            surface.RegisterCommandSubmission(paneId, command);
            surface.GetSession(paneId)?.Write(command + Environment.NewLine);
        }
        else
        {
            surface.GetSession(paneId)?.Write(command);
        }

        return true;
    }

    private void DateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadEntriesForSelectedDate();
    }

    private void WorkspaceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void SurfaceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void PaneFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilterEvents = true;
        WorkspaceFilterCombo.SelectedValue = string.Empty;
        SurfaceFilterCombo.SelectedValue = string.Empty;
        PaneFilterCombo.SelectedValue = string.Empty;
        SearchBox.Text = string.Empty;
        _suppressFilterEvents = false;

        ApplyFilters();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDates();
        LoadEntriesForSelectedDate();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.CommandLogService.GetLogsDirectoryPath();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show($"Logs folder: {dir}", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenTranscriptsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.CommandLogService.GetTerminalTranscriptsDirectoryPath();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show($"Terminal captures folder: {dir}", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenSessionVault_Click(object sender, RoutedEventArgs e)
    {
        var window = new SessionVaultWindow { Owner = this };
        window.ShowDialog();
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Entry.Command is { Length: > 0 } command)
            Clipboard.SetText(command);
    }

    private void InsertCommand_Click(object sender, RoutedEventArgs e)
    {
        ExecuteSelected(run: false);
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (ExecuteSelected(run: true))
        {
            DialogResult = true;
            Close();
        }
    }

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunCommand_Click(sender, e);
    }

    private void EntriesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                InsertCommand_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Enter:
                RunCommand_Click(sender, e);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyCommand_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
