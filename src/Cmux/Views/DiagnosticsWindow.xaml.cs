using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cmux.Core.Config;
using Cmux.Core.Logging;

namespace Cmux.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly ObservableCollection<EntryRow> _rows = [];
    private readonly Action<LogEntry> _onLogEntry;
    private string _categoryFilter = "";
    private LogLevel _minLevel = LogLevel.Info;
    private bool _autoScroll = true;

    public DiagnosticsWindow()
    {
        InitializeComponent();

        EntriesList.ItemsSource = _rows;
        EnabledToggle.IsOn = SettingsService.Current.EnableDiagnosticLogging;
        SetMinLevel(SettingsService.Current.DiagnosticLogLevel);

        // Pre-populate from the ring buffer so the window opens with whatever's
        // already been logged this session.
        foreach (var entry in Log.Snapshot())
            TryAdd(entry);

        _onLogEntry = OnLogEntry;
        Log.EntryAdded += _onLogEntry;

        Loaded += (_, _) => UpdateStatus();
        Closed += (_, _) => Log.EntryAdded -= _onLogEntry;
    }

    private void OnLogEntry(LogEntry entry)
    {
        // Marshal back to the UI thread — log calls happen on background threads.
        Dispatcher.BeginInvoke(() =>
        {
            if (PauseToggle.IsOn) return;
            TryAdd(entry);
        });
    }

    private bool MatchesFilter(LogEntry entry)
    {
        if ((int)entry.Level < (int)_minLevel) return false;
        if (!string.IsNullOrEmpty(_categoryFilter)
            && entry.Category.IndexOf(_categoryFilter, StringComparison.OrdinalIgnoreCase) < 0
            && entry.Message.IndexOf(_categoryFilter, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    private void TryAdd(LogEntry entry)
    {
        if (!MatchesFilter(entry)) return;
        _rows.Add(EntryRow.From(entry));
        // Cap visible rows so the window stays responsive even if logging spikes.
        const int maxVisible = 5000;
        while (_rows.Count > maxVisible) _rows.RemoveAt(0);

        if (_autoScroll && _rows.Count > 0)
            EntriesList.ScrollIntoView(_rows[^1]);

        UpdateStatus();
    }

    private void Rebuild()
    {
        _rows.Clear();
        foreach (var entry in Log.Snapshot())
            if (MatchesFilter(entry))
                _rows.Add(EntryRow.From(entry));
        if (_rows.Count > 0)
            EntriesList.ScrollIntoView(_rows[^1]);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"{_rows.Count} shown · {Log.Count} buffered";
    }

    private void SetMinLevel(string raw)
    {
        _minLevel = (raw ?? "info").Trim().ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

        foreach (ComboBoxItem item in LevelCombo.Items)
        {
            if (string.Equals(item.Tag as string, _minLevel.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                break;
            }
        }
    }

    private void LevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _minLevel = Enum.TryParse<LogLevel>(tag, true, out var lvl) ? lvl : LogLevel.Info;
            Rebuild();
        }
    }

    private void CategoryFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _categoryFilter = CategoryFilter.Text ?? "";
        Rebuild();
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Log.Enabled = EnabledToggle.IsOn;
        SettingsService.Current.EnableDiagnosticLogging = EnabledToggle.IsOn;
        SettingsService.Save();
        UpdateStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Log.Clear();
        _rows.Clear();
        UpdateStatus();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var row in _rows)
            sb.AppendLine($"{row.TimeText} {row.LevelText,-5} [{row.Category}] {row.Message}");

        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard locked */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// View-model for a single visible row. Pre-computes display strings so
    /// data binding stays cheap when entries scroll fast.
    /// </summary>
    private sealed record EntryRow(string TimeText, string LevelText, Brush LevelBrush, string Category, string Message)
    {
        public static EntryRow From(LogEntry entry)
        {
            var local = entry.TimestampUtc.ToLocalTime();
            return new EntryRow(
                TimeText: local.ToString("HH:mm:ss.fff"),
                LevelText: entry.Level.ToString().ToUpperInvariant(),
                LevelBrush: BrushFor(entry.Level),
                Category: entry.Category,
                Message: entry.Message);
        }

        private static Brush BrushFor(LogLevel level) => level switch
        {
            LogLevel.Debug => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
            LogLevel.Info => new SolidColorBrush(Color.FromRgb(0xA5, 0xB4, 0xFC)),
            LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
            LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => Brushes.Gray,
        };
    }
}
