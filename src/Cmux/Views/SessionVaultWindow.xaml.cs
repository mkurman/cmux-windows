using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.ViewModels;

namespace Cmux.Views;

public partial class SessionVaultWindow : Window
{
    private readonly Dictionary<string, string> _workspaceNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _surfaceNames = new(StringComparer.Ordinal);
    private readonly List<TerminalTranscriptEntry> _entries = [];

    public SessionVaultWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        RefreshEntries();
    }

    private sealed class TranscriptEntryView
    {
        public TerminalTranscriptEntry Entry { get; init; } = null!;
        public string CapturedAtLocal { get; init; } = string.Empty;
        public string WorkspaceDisplay { get; init; } = "-";
        public string SurfaceDisplay { get; init; } = "-";
    }

    private TranscriptEntryView? Selected => EntriesList.SelectedItem as TranscriptEntryView;

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value.Length <= 8 ? value : value[..8];
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
        return _workspaceNames.TryGetValue(workspaceId, out var name)
            ? name
            : ShortId(workspaceId);
    }

    private string ResolveSurfaceName(string surfaceId)
    {
        return _surfaceNames.TryGetValue(surfaceId, out var name)
            ? name
            : ShortId(surfaceId);
    }

    private void RefreshEntries()
    {
        RefreshNameMaps();

        _entries.Clear();
        _entries.AddRange(App.CommandLogService.GetTerminalTranscripts());

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();

        IEnumerable<TerminalTranscriptEntry> filtered = _entries;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(e =>
                e.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Reason.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.WorkspaceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.SurfaceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.PaneId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(e.WorkingDirectory) && e.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                ResolveWorkspaceName(e.WorkspaceId).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ResolveSurfaceName(e.SurfaceId).Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var views = filtered
            .OrderByDescending(e => e.CapturedAt)
            .Select(e => new TranscriptEntryView
            {
                Entry = e,
                CapturedAtLocal = e.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                WorkspaceDisplay = ResolveWorkspaceName(e.WorkspaceId),
                SurfaceDisplay = ResolveSurfaceName(e.SurfaceId),
            })
            .ToList();

        EntriesList.ItemsSource = views;
        SummaryText.Text = views.Count == 1 ? "1 capture" : $"{views.Count} captures";

        if (views.Count > 0)
            EntriesList.SelectedIndex = 0;
        else
            ShowNoSelection();
    }

    private void ShowNoSelection()
    {
        MetaTitleText.Text = "Select a capture";
        MetaInfoText.Text = "";
        TranscriptText.Text = "";
    }

    private void ShowEntry(TranscriptEntryView view)
    {
        var e = view.Entry;
        var workspace = ResolveWorkspaceName(e.WorkspaceId);
        var surface = ResolveSurfaceName(e.SurfaceId);
        var pane = ShortId(e.PaneId);

        MetaTitleText.Text = $"{workspace} / {surface} / Pane {pane}";
        MetaInfoText.Text = $"{e.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · reason: {e.Reason} · cwd: {(string.IsNullOrWhiteSpace(e.WorkingDirectory) ? "-" : e.WorkingDirectory)}";
        TranscriptText.Text = App.CommandLogService.LoadTerminalTranscriptContent(e.FilePath);
        TranscriptText.ScrollToHome();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshEntries();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show($"Session vault folder: {dir}", "Session Vault", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } selected)
            ShowEntry(selected);
        else
            ShowNoSelection();
    }

    private void EntriesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected is { } selected)
            ShowEntry(selected);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TranscriptText.Text))
            Clipboard.SetText(TranscriptText.Text);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = selected.Entry.FilePath,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(selected.Entry.FilePath, "Session Vault", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
