using System.Windows;
using System;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using Cmux.Controls;
using Cmux.ViewModels;

namespace Cmux.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private readonly DispatcherTimer _uiRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ICollectionView? _workspaceView;

    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        SetupWorkspaceFilter();

        CommandPaletteControl.PaletteClosed += () => FocusTerminal();
        CommandPaletteControl.ItemExecuted += item => FocusTerminal();


        // Wire snippet picker events
        SnippetPickerControl.SnippetSelected += OnSnippetSelected;
        SnippetPickerControl.Closed += () => SnippetPickerControl.Visibility = Visibility.Collapsed;

        // Wire search overlay events
        SearchOverlayControl.SearchTextChanged += OnSearchTextChanged;
        SearchOverlayControl.NextMatchRequested += OnSearchNext;
        SearchOverlayControl.PreviousMatchRequested += OnSearchPrevious;
        SearchOverlayControl.SearchClosed += OnSearchClosed;

        // Wire terminal surface events
        SplitPaneContainerControl.SearchRequested += () =>
        {
            if (SearchOverlayControl.Visibility != Visibility.Visible)
                ToggleSearch();
            else
                SearchOverlayControl.FocusInput();
        };

        // Periodically refresh lightweight UI state (pane count, zoom icon)
        _uiRefreshTimer.Tick += (_, _) => RefreshSurfaceUiState();
        _uiRefreshTimer.Start();

        // Subscribe to settings changes
        Cmux.Core.Config.SettingsService.SettingsChanged += OnSettingsChanged;
        OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        var settings = Cmux.Core.Config.SettingsService.Current;
        var theme = Cmux.Core.Config.TerminalThemes.GetEffective(settings);

        Opacity = Math.Clamp(settings.Opacity, 0.5, 1.0);

        // Update all visible terminal controls
        foreach (var workspace in ViewModel.Workspaces)
        {
            foreach (var surface in workspace.Surfaces)
            {
                // Find the SplitPaneContainer for this surface and update terminals
                var container = FindVisualChild<SplitPaneContainer>(ContentArea, null);
                if (container != null)
                {
                    container.UpdateAllTerminals(theme, settings.FontFamily, settings.FontSize);
                }
            }
        }

        RefreshSurfaceUiState();
    }

    private void SetupWorkspaceFilter()
    {
        _workspaceView = CollectionViewSource.GetDefaultView(ViewModel.Workspaces);
        if (_workspaceView != null)
        {
            _workspaceView.Filter = WorkspaceFilterPredicate;
            WorkspaceList.ItemsSource = _workspaceView;
        }
    }

    private bool WorkspaceFilterPredicate(object obj)
    {
        if (obj is not WorkspaceViewModel ws)
            return false;

        var query = WorkspaceFilterBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return (ws.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (ws.WorkingDirectory?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (ws.GitBranch?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (ws.AgentLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void WorkspaceFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _workspaceView?.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore window position from session state if available
        var session = Cmux.Core.Services.SessionPersistenceService.Load();
        if (session?.Window != null)
        {
            var w = session.Window;
            if (w.Width > 0 && w.Height > 0)
            {
                Left = w.X;
                Top = w.Y;
                Width = w.Width;
                Height = w.Height;
                WindowState = w.IsMaximized ? WindowState.Maximized : WindowState.Normal;
            }
        }

        RefreshSurfaceUiState();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiRefreshTimer.Stop();
        ViewModel.SaveSession(Left, Top, Width, Height, WindowState == WindowState.Maximized);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // Workspaces
        if (ctrl && !shift && !alt)
        {
            switch (e.Key)
            {
                case Key.N: // New workspace
                    ViewModel.CreateNewWorkspace();
                    e.Handled = true;
                    return;
                case Key.B: // Toggle sidebar
                    ViewModel.ToggleSidebar();
                    e.Handled = true;
                    return;
                case Key.I: // Notification panel
                    ViewModel.ToggleNotificationPanel();
                    e.Handled = true;
                    return;
                case Key.T: // New surface
                    ViewModel.SelectedWorkspace?.CreateNewSurface();
                    e.Handled = true;
                    return;
                case Key.W: // Close surface
                    var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
                    if (surface != null)
                        ViewModel.SelectedWorkspace?.CloseSurface(surface);
                    e.Handled = true;
                    return;
                case Key.D: // Split right
                    ViewModel.SelectedWorkspace?.SelectedSurface?.SplitRight();
                    e.Handled = true;
                    return;
                // Workspace 1-8
                case Key.D1: ViewModel.SelectWorkspace(0); e.Handled = true; return;
                case Key.D2: ViewModel.SelectWorkspace(1); e.Handled = true; return;
                case Key.D3: ViewModel.SelectWorkspace(2); e.Handled = true; return;
                case Key.D4: ViewModel.SelectWorkspace(3); e.Handled = true; return;
                case Key.D5: ViewModel.SelectWorkspace(4); e.Handled = true; return;
                case Key.D6: ViewModel.SelectWorkspace(5); e.Handled = true; return;
                case Key.D7: ViewModel.SelectWorkspace(6); e.Handled = true; return;
                case Key.D8: ViewModel.SelectWorkspace(7); e.Handled = true; return;
                case Key.D9: // Last workspace
                    if (ViewModel.Workspaces.Count > 0)
                        ViewModel.SelectWorkspace(ViewModel.Workspaces.Count - 1);
                    e.Handled = true;
                    return;
                case Key.OemComma: // Settings (Ctrl+,)
                    OpenSettings();
                    e.Handled = true;
                    return;
            }
        }

        if (ctrl && shift && !alt)
        {
            switch (e.Key)
            {
                case Key.W: // Close workspace
                    ViewModel.CloseWorkspace(ViewModel.SelectedWorkspace);
                    e.Handled = true;
                    return;
                case Key.R: // Rename workspace
                    ViewModel.SelectedWorkspace?.Rename();
                    e.Handled = true;
                    return;
                case Key.D: // Split down
                    ViewModel.SelectedWorkspace?.SelectedSurface?.SplitDown();
                    e.Handled = true;
                    return;
                case Key.U: // Jump to latest unread
                    ViewModel.JumpToLatestUnread();
                    e.Handled = true;
                    return;
                case Key.OemCloseBrackets: // Next surface (Ctrl+Shift+])
                    ViewModel.SelectedWorkspace?.NextSurface();
                    e.Handled = true;
                    return;
                case Key.OemOpenBrackets: // Previous surface (Ctrl+Shift+[)
                    ViewModel.SelectedWorkspace?.PreviousSurface();
                    e.Handled = true;
                    return;
                case Key.Z: // Zoom toggle (Ctrl+Shift+Z)
                    ViewModel.SelectedWorkspace?.SelectedSurface?.ToggleZoom();
                    e.Handled = true;
                    return;
                case Key.F: // Search (Ctrl+Shift+F)
                    ToggleSearch();
                    e.Handled = true;
                    return;
                case Key.P: // Command palette (Ctrl+Shift+P)
                    ToggleCommandPalette();
                    e.Handled = true;
                    return;
                case Key.L: // Logs (Ctrl+Shift+L)
                    OpenLogsWindow();
                    e.Handled = true;
                    return;
                case Key.V: // Session Vault (Ctrl+Shift+V)
                    OpenSessionVault();
                    e.Handled = true;
                    return;
                case Key.H: // History: insert last command (Ctrl+Shift+H)
                    InsertLastCommandFromHistory();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Alt: pane focus + history picker
        if (ctrl && alt && !shift)
        {
            switch (e.Key)
            {
                case Key.Right:
                    ViewModel.SelectedWorkspace?.SelectedSurface?.FocusNextPane();
                    e.Handled = true;
                    return;
                case Key.Left:
                    ViewModel.SelectedWorkspace?.SelectedSurface?.FocusPreviousPane();
                    e.Handled = true;
                    return;
                case Key.Down:
                    ViewModel.SelectedWorkspace?.SelectedSurface?.FocusNextPane();
                    e.Handled = true;
                    return;
                case Key.Up:
                    ViewModel.SelectedWorkspace?.SelectedSurface?.FocusPreviousPane();
                    e.Handled = true;
                    return;
                case Key.H: // Open command history picker (Ctrl+Alt+H)
                    OpenCommandHistoryPicker();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Tab / Ctrl+Shift+Tab: cycle surfaces
        if (ctrl && e.Key == Key.Tab)
        {
            if (shift)
                ViewModel.SelectedWorkspace?.PreviousSurface();
            else
                ViewModel.SelectedWorkspace?.NextSurface();
            e.Handled = true;
        }
    }

    // Title bar handlers
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;

        if (sender is not Thumb thumb || thumb.Tag is not string edge)
            return;

        const double minW = 600;
        const double minH = 400;

        double left = Left;
        double top = Top;
        double width = Width;
        double height = Height;

        void ResizeLeft(double dx)
        {
            var newWidth = Math.Max(minW, width - dx);
            var delta = width - newWidth;
            width = newWidth;
            left += delta;
        }

        void ResizeRight(double dx)
        {
            width = Math.Max(minW, width + dx);
        }

        void ResizeTop(double dy)
        {
            var newHeight = Math.Max(minH, height - dy);
            var delta = height - newHeight;
            height = newHeight;
            top += delta;
        }

        void ResizeBottom(double dy)
        {
            height = Math.Max(minH, height + dy);
        }

        switch (edge)
        {
            case "Left": ResizeLeft(e.HorizontalChange); break;
            case "Right": ResizeRight(e.HorizontalChange); break;
            case "Top": ResizeTop(e.VerticalChange); break;
            case "Bottom": ResizeBottom(e.VerticalChange); break;
            case "TopLeft":
                ResizeLeft(e.HorizontalChange);
                ResizeTop(e.VerticalChange);
                break;
            case "TopRight":
                ResizeRight(e.HorizontalChange);
                ResizeTop(e.VerticalChange);
                break;
            case "BottomLeft":
                ResizeLeft(e.HorizontalChange);
                ResizeBottom(e.VerticalChange);
                break;
            case "BottomRight":
                ResizeRight(e.HorizontalChange);
                ResizeBottom(e.VerticalChange);
                break;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    // --- Workspace drag-and-drop reordering ---

    private Point _dragStartPoint;

    private void WorkspaceItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void WorkspaceItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is System.Windows.Controls.ListBoxItem item &&
            item.DataContext is ViewModels.WorkspaceViewModel workspace)
        {
            DragDrop.DoDragDrop(item, workspace, DragDropEffects.Move);
        }
    }

    private void WorkspaceItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBoxItem targetItem) return;
        if (targetItem.DataContext is not ViewModels.WorkspaceViewModel targetWorkspace) return;

        var sourceWorkspace = e.Data.GetData(typeof(ViewModels.WorkspaceViewModel)) as ViewModels.WorkspaceViewModel;
        if (sourceWorkspace == null || sourceWorkspace == targetWorkspace) return;

        int sourceIndex = ViewModel.Workspaces.IndexOf(sourceWorkspace);
        int targetIndex = ViewModel.Workspaces.IndexOf(targetWorkspace);

        if (sourceIndex >= 0 && targetIndex >= 0)
        {
            ViewModel.Workspaces.Move(sourceIndex, targetIndex);
        }
    }

    // Title bar + menu handlers
    private void CommandPalette_Click(object sender, RoutedEventArgs e) => ToggleCommandPalette();
    private void Search_Click(object sender, RoutedEventArgs e) => ToggleSearch();
    private void Snippets_Click(object sender, RoutedEventArgs e) => ToggleSnippetPicker();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void MenuOpenLogs_Click(object sender, RoutedEventArgs e) => OpenLogsWindow();
    private void MenuOpenSessionVault_Click(object sender, RoutedEventArgs e) => OpenSessionVault();
    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "cmux for Windows\nA terminal multiplexer optimized for modern workflows.",
            "About cmux",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // Toolbar handlers
    private void ToolbarSplitRight_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedWorkspace?.SelectedSurface?.SplitRight();
    private void ToolbarSplitDown_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedWorkspace?.SelectedSurface?.SplitDown();
    private void ToolbarLayout2Col_Click(object sender, RoutedEventArgs e) => ApplyLayout(2, 1);
    private void ToolbarLayoutGrid_Click(object sender, RoutedEventArgs e) => ApplyLayout(2, 2);
    private void ToolbarLayoutMainStack_Click(object sender, RoutedEventArgs e) => ApplyMainStackLayout();
    private void ToolbarEqualize_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedWorkspace?.SelectedSurface?.EqualizePanes();
    private void ToolbarZoom_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedWorkspace?.SelectedSurface?.ToggleZoom();
        RefreshSurfaceUiState();
    }


    private void ToggleSearch()
    {
        if (SearchOverlayControl.Visibility == Visibility.Visible)
            SearchOverlayControl.Visibility = Visibility.Collapsed;
        else
        {
            SearchOverlayControl.Visibility = Visibility.Visible;
            SearchOverlayControl.FocusInput();
        }
    }

    private void ToggleSnippetPicker()
    {
        if (SnippetPickerControl.Visibility == Visibility.Visible)
        {
            SnippetPickerControl.Visibility = Visibility.Collapsed;
        }
        else
        {
            SnippetPickerControl.RefreshList();
            SnippetPickerControl.Visibility = Visibility.Visible;
            SnippetPickerControl.FocusSearch();
        }
    }

    private void OnSnippetSelected(Cmux.Core.Models.Snippet snippet)
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is string paneId)
        {
            var session = surface.GetSession(paneId);
            var content = snippet.Resolve();
            session?.Write(content);
            App.SnippetService.IncrementUseCount(snippet.Id);
        }
        SnippetPickerControl.Visibility = Visibility.Collapsed;
    }

    private void ToggleCommandPalette()
    {
        if (CommandPaletteControl.Visibility == Visibility.Visible)
        {
            CommandPaletteControl.Hide();
        }
        else
        {
            var items = BuildPaletteItems();
            CommandPaletteControl.Show(items);
        }
    }

    private List<PaletteItem> BuildPaletteItems()
    {
        return
        [
            new() { Id = "new-workspace", Label = "New Workspace", Icon = "\uE710", Shortcut = "Ctrl+N", Category = "Workspace", Execute = () => ViewModel.CreateNewWorkspace() },
            new() { Id = "new-surface", Label = "New Surface", Icon = "\uE710", Shortcut = "Ctrl+T", Category = "Surface", Execute = () => ViewModel.SelectedWorkspace?.CreateNewSurface() },
            new() { Id = "close-surface", Label = "Close Surface", Icon = "\uE711", Shortcut = "Ctrl+W", Category = "Surface", Execute = () => { var s = ViewModel.SelectedWorkspace?.SelectedSurface; if (s != null) ViewModel.SelectedWorkspace?.CloseSurface(s); } },
            new() { Id = "close-workspace", Label = "Close Workspace", Icon = "\uE711", Shortcut = "Ctrl+Shift+W", Category = "Workspace", Execute = () => ViewModel.CloseWorkspace(ViewModel.SelectedWorkspace) },
            new() { Id = "split-right", Label = "Split Right", Icon = "\uE26B", Shortcut = "Ctrl+D", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.SplitRight() },
            new() { Id = "split-down", Label = "Split Down", Icon = "\uE74B", Shortcut = "Ctrl+Shift+D", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.SplitDown() },
            new() { Id = "toggle-sidebar", Label = "Toggle Sidebar", Icon = "\uE700", Shortcut = "Ctrl+B", Category = "View", Execute = () => ViewModel.ToggleSidebar() },
            new() { Id = "notifications", Label = "Notifications", Icon = "\uEA8F", Shortcut = "Ctrl+I", Category = "View", Execute = () => ViewModel.ToggleNotificationPanel() },
            new() { Id = "test-notification", Label = "Test Notification", Icon = "\uE7F4", Category = "View", Execute = ShowTestNotification },
            new() { Id = "open-logs", Label = "Open Command Logs", Icon = "\uE7BA", Shortcut = "Ctrl+Shift+L", Category = "Logs", Execute = OpenLogsWindow },
            new() { Id = "open-session-vault", Label = "Open Session Vault", Icon = "\uE8D1", Shortcut = "Ctrl+Shift+V", Category = "Logs", Execute = OpenSessionVault },
            new() { Id = "open-command-history", Label = "Open Command History", Icon = "\uE81C", Shortcut = "Ctrl+Alt+H", Category = "History", Execute = OpenCommandHistoryPicker },
            new() { Id = "insert-last-command", Label = "Insert Last Command", Icon = "\uE8A7", Shortcut = "Ctrl+Shift+H", Category = "History", Execute = InsertLastCommandFromHistory },
            new() { Id = "search", Label = "Search", Icon = "\uE721", Shortcut = "Ctrl+Shift+F", Category = "View", Execute = () => ToggleSearch() },
            new() { Id = "zoom-pane", Label = "Zoom Pane", Icon = "\uE740", Shortcut = "Ctrl+Shift+Z", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.ToggleZoom() },
            new() { Id = "focus-next", Label = "Focus Next Pane", Icon = "\uE76C", Shortcut = "Ctrl+Alt+Right", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.FocusNextPane() },
            new() { Id = "focus-prev", Label = "Focus Previous Pane", Icon = "\uE76B", Shortcut = "Ctrl+Alt+Left", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.FocusPreviousPane() },
            new() { Id = "next-surface", Label = "Next Surface", Icon = "\uE76C", Shortcut = "Ctrl+Shift+]", Category = "Surface", Execute = () => ViewModel.SelectedWorkspace?.NextSurface() },
            new() { Id = "prev-surface", Label = "Previous Surface", Icon = "\uE76B", Shortcut = "Ctrl+Shift+[", Category = "Surface", Execute = () => ViewModel.SelectedWorkspace?.PreviousSurface() },
            new() { Id = "settings", Label = "Settings", Icon = "\uE713", Shortcut = "Ctrl+,", Category = "App", Execute = () => OpenSettings() },
            new() { Id = "equalize", Label = "Equalize Panes", Icon = "\uE9D5", Category = "Pane", Execute = () => ViewModel.SelectedWorkspace?.SelectedSurface?.EqualizePanes() },
            new() { Id = "layout-2col", Label = "Layout: 2 Columns", Icon = "\uE745", Category = "Layout", Execute = () => ApplyLayout(2, 1) },
            new() { Id = "layout-3col", Label = "Layout: 3 Columns", Icon = "\uE745", Category = "Layout", Execute = () => ApplyLayout(3, 1) },
            new() { Id = "layout-grid", Label = "Layout: Grid 2x2", Icon = "\uF0E2", Category = "Layout", Execute = () => ApplyLayout(2, 2) },
            new() { Id = "layout-main-stack", Label = "Layout: Main + Stack", Icon = "\uE745", Category = "Layout", Execute = () => ApplyMainStackLayout() },
        ];
    }

    private void ApplyLayout(int cols, int rows)
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface == null) return;

        NormalizeToSinglePane(surface);

        for (int c = 1; c < cols; c++)
            surface.SplitRight();

        if (rows > 1)
        {
            var columnPaneIds = surface.RootNode.GetLeaves()
                .Select(l => l.PaneId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList();

            foreach (var paneId in columnPaneIds)
            {
                surface.FocusPane(paneId);
                for (int r = 1; r < rows; r++)
                    surface.SplitDown();
            }
        }

        surface.EqualizePanes();
    }

    private void ApplyMainStackLayout()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface == null) return;

        NormalizeToSinglePane(surface);

        // Main pane on left, stack of 2 on right
        surface.SplitRight();

        var rightPaneId = surface.RootNode.GetLeaves()
            .Skip(1)
            .Select(l => l.PaneId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        if (!string.IsNullOrWhiteSpace(rightPaneId))
        {
            surface.FocusPane(rightPaneId);
            surface.SplitDown();
            surface.EqualizePanes();
        }
    }

    private static void NormalizeToSinglePane(SurfaceViewModel surface)
    {
        var paneIds = surface.RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();

        if (paneIds.Count <= 1) return;

        var focusedPaneId = surface.FocusedPaneId;
        string keepPaneId = !string.IsNullOrWhiteSpace(focusedPaneId) && paneIds.Contains(focusedPaneId)
            ? focusedPaneId
            : paneIds[0];

        surface.FocusPane(keepPaneId);

        foreach (var paneId in paneIds.Where(id => id != keepPaneId))
            surface.ClosePane(paneId);
    }

    private void FocusTerminal()
    {
        // Return focus to the active terminal pane
        ContentArea.Focus();
    }

    private void RefreshSurfaceUiState()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface == null)
        {
            PaneCountText.Text = "0 panes";
            ToolbarZoomIcon.Text = "\uE740";
            ToolbarZoomButton.ToolTip = "Zoom Pane (Ctrl+Shift+Z)";
            return;
        }

        var paneCount = surface.RootNode.GetLeaves().Count();
        PaneCountText.Text = surface.IsZoomed
            ? $"{paneCount} panes (1 zoomed)"
            : paneCount == 1 ? "1 pane" : $"{paneCount} panes";

        ToolbarZoomIcon.Text = surface.IsZoomed ? "\uE73F" : "\uE740";
        ToolbarZoomButton.ToolTip = surface.IsZoomed
            ? "Unzoom Pane (Ctrl+Shift+Z)"
            : "Zoom Pane (Ctrl+Shift+Z)";
    }

    // --- Search handling ---
    private int _currentSearchMatch = 0;
    private List<(int row, int col, int length)> _searchMatches = [];

    private void OnSearchTextChanged(string query)
    {
        _searchMatches = [];
        _currentSearchMatch = 0;

        if (string.IsNullOrEmpty(query))
        {
            ClearSearchHighlights();
            SearchOverlayControl.UpdateMatchCount(0, 0);
            return;
        }

        // Search in focused terminal
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is string paneId)
        {
            var session = surface.GetSession(paneId);
            if (session != null)
            {
                _searchMatches = FindAllInBuffer(session.Buffer, query);
                _currentSearchMatch = 0;
                UpdateSearchHighlights();
            }
        }

        SearchOverlayControl.UpdateMatchCount(_currentSearchMatch, _searchMatches.Count);
    }

    private void OnSearchNext()
    {
        if (_searchMatches.Count == 0) return;
        _currentSearchMatch = (_currentSearchMatch + 1) % _searchMatches.Count;
        UpdateSearchHighlights();
        SearchOverlayControl.UpdateMatchCount(_currentSearchMatch, _searchMatches.Count);
    }

    private void OnSearchPrevious()
    {
        if (_searchMatches.Count == 0) return;
        _currentSearchMatch = (_currentSearchMatch - 1 + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchHighlights();
        SearchOverlayControl.UpdateMatchCount(_currentSearchMatch, _searchMatches.Count);
    }

    private void OnSearchClosed()
    {
        ClearSearchHighlights();
        _searchMatches = [];
        SearchOverlayControl.Visibility = Visibility.Collapsed;
    }

    private void UpdateSearchHighlights()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is string paneId)
        {
            var terminal = FindTerminalForPane(paneId);
            terminal?.SetSearchHighlights(_searchMatches, _currentSearchMatch);
        }
    }

    private void ClearSearchHighlights()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is string paneId)
        {
            var terminal = FindTerminalForPane(paneId);
            terminal?.ClearSearchHighlights();
        }
    }

    private TerminalControl? FindTerminalForPane(string paneId)
    {
        return FindVisualChild<TerminalControl>(ContentArea, null);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (predicate == null || predicate(typed)))
                return typed;
            var result = FindVisualChild(child, predicate);
            if (result != null) return result;
        }
        return null;
    }

    private static List<(int row, int col, int length)> FindAllInBuffer(Cmux.Core.Terminal.TerminalBuffer buffer, string query)
    {
        var matches = new List<(int, int, int)>();
        if (string.IsNullOrEmpty(query)) return matches;

        for (int row = 0; row < buffer.Rows; row++)
        {
            var lineText = GetRowText(buffer, row);
            int idx = 0;
            while ((idx = lineText.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                matches.Add((row, idx, query.Length));
                idx++;
            }
        }
        return matches;
    }

    private static string GetRowText(Cmux.Core.Terminal.TerminalBuffer buffer, int row)
    {
        var sb = new System.Text.StringBuilder();
        for (int col = 0; col < buffer.Cols; col++)
        {
            var cell = buffer.CellAt(row, col);
            sb.Append(cell.Character ?? " ");
        }
        return sb.ToString();
    }

    private void ShowTestNotification()
    {
        var workspaceId = ViewModel.SelectedWorkspace?.Workspace.Id ?? string.Empty;
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        var surfaceId = surface?.Surface.Id ?? string.Empty;
        var paneId = surface?.FocusedPaneId;

        App.NotificationService.AddNotification(
            workspaceId,
            surfaceId,
            paneId,
            "cmux test",
            "Notification check",
            "If you see this in panel/toast, notifications are working.",
            Cmux.Core.Models.NotificationSource.Cli);
    }

    private void OpenLogsWindow()
    {
        var window = new LogsWindow { Owner = this };
        window.ShowDialog();
    }

    private void OpenSessionVault()
    {
        var window = new SessionVaultWindow { Owner = this };
        window.ShowDialog();
    }

    private void OpenCommandHistoryPicker()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is not string paneId)
            return;

        var history = surface.GetCommandHistory(paneId);
        if (history.Count == 0)
        {
            MessageBox.Show("No command history found yet for this pane.", "History", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var paneLabel = paneId.Length <= 8 ? paneId : paneId[..8];
        var window = new HistoryWindow(
            history,
            insertAction: command => surface.GetSession(paneId)?.Write(command),
            runAction: command =>
            {
                surface.RegisterCommandSubmission(paneId, command);
                surface.GetSession(paneId)?.Write(command + Environment.NewLine);
            })
        {
            Owner = this,
            Title = $"Command History · Pane {paneLabel}",
        };

        window.ShowDialog();
    }

    private void InsertLastCommandFromHistory()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface?.FocusedPaneId is not string paneId)
            return;

        var history = surface.GetCommandHistory(paneId);
        if (history.Count == 0)
        {
            MessageBox.Show("No command history found yet for this pane.", "History", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var last = history[^1];
        surface.GetSession(paneId)?.Write(last);
    }

    private void OpenSettings()
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
    }
}