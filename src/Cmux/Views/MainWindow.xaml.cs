using System.Windows;
using System;
using System.Linq;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cmux.Controls;
using Cmux.ViewModels;
using Cmux.Services;

namespace Cmux.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private readonly DispatcherTimer _uiRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ICollectionView? _workspaceView;
    private readonly Dictionary<string, AgentChatMessageView> _streamingAssistantByThread = new(StringComparer.Ordinal);
    private List<AgentThreadView> _agentThreadViews = [];
    private List<AgentChatMessageView> _allThreadMessages = [];
    private readonly ObservableCollection<AgentChatMessageView> _visibleThreadMessages = [];
    private string? _selectedAgentThreadId;
    private string _lastAgentContextKey = "";

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

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        App.AgentRuntime.RuntimeUpdated += OnAgentRuntimeUpdated;
        App.AgentConversationStore.StoreChanged += OnAgentConversationStoreChanged;
        AgentMessagesList.ItemsSource = _visibleThreadMessages;
        UpdateAgentPanelLayout();
        RefreshAgentThreads();
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

        ApplyAgentChatFont(settings);
        RefreshSurfaceUiState();
    }

    private void ApplyAgentChatFont(Cmux.Core.Config.CmuxSettings settings)
    {
        var agent = settings.Agent ?? new Cmux.Core.Config.AgentSettings();
        var fontFamilyName = string.IsNullOrWhiteSpace(agent.ChatFontFamily)
            ? settings.FontFamily
            : agent.ChatFontFamily.Trim();
        var fontSize = Math.Clamp(agent.ChatFontSize, 9, 28);

        System.Windows.Media.FontFamily fontFamily;
        try
        {
            fontFamily = new System.Windows.Media.FontFamily(fontFamilyName);
        }
        catch
        {
            fontFamily = new System.Windows.Media.FontFamily("Cascadia Code");
        }

        AgentThreadsList.FontFamily = fontFamily;
        AgentThreadsList.FontSize = fontSize;
        AgentMessagesList.FontFamily = fontFamily;
        AgentMessagesList.FontSize = fontSize;
        AgentPromptBox.FontFamily = fontFamily;
        AgentPromptBox.FontSize = fontSize;
        AgentThreadSearchBox.FontFamily = fontFamily;
        AgentThreadSearchBox.FontSize = fontSize;
        AgentMessageSearchBox.FontFamily = fontFamily;
        AgentMessageSearchBox.FontSize = fontSize;
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
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        App.AgentRuntime.RuntimeUpdated -= OnAgentRuntimeUpdated;
        App.AgentConversationStore.StoreChanged -= OnAgentConversationStoreChanged;
        ViewModel.SaveSession(Left, Top, Width, Height, WindowState == WindowState.Maximized);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AgentPanelVisible) ||
            e.PropertyName == nameof(MainViewModel.AgentPanelWidth))
        {
            UpdateAgentPanelLayout();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedWorkspace))
            RefreshAgentThreads();
    }

    private void OnAgentConversationStoreChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshAgentThreads();
        });
    }

    private void OnAgentRuntimeUpdated(AgentRuntimeUpdate update)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsUpdateForCurrentPane(update))
                return;

            if (!string.IsNullOrWhiteSpace(update.ThreadId) &&
                !string.Equals(_selectedAgentThreadId, update.ThreadId, StringComparison.Ordinal))
            {
                _selectedAgentThreadId = update.ThreadId;
                SelectAgentThreadInList(update.ThreadId);
            }

            switch (update.Type)
            {
                case AgentRuntimeUpdateType.ThreadChanged:
                    RefreshAgentThreads();
                    break;

                case AgentRuntimeUpdateType.UserMessage:
                    AppendAgentMessage("user", update.Message, update.CreatedAtUtc, "-", update.ThreadId);
                    AgentStatusText.Text = "User message sent";
                    break;

                case AgentRuntimeUpdateType.AssistantDelta:
                    AppendAssistantDelta(update.ThreadId, update.Message);
                    AgentStatusText.Text = "Streaming response...";
                    break;

                case AgentRuntimeUpdateType.AssistantCompleted:
                    FinalizeAssistantMessage(update.ThreadId, update.Message, update.CreatedAtUtc,
                        $"usage in:{update.InputTokens} out:{update.OutputTokens} total:{update.TotalTokens} · {update.Provider}/{update.Model}");
                    AgentUsageText.Text = $"Usage: in {update.InputTokens} · out {update.OutputTokens} · total {update.TotalTokens}";
                    AgentContextText.Text = update.ContextBudgetTokens > 0
                        ? $"Context: {update.EstimatedContextTokens}/{update.ContextBudgetTokens} tokens{(update.ContextNeedsCompaction ? " (near limit)" : "")}"
                        : "Context: -";
                    AgentStatusText.Text = "Response completed";
                    RefreshAgentThreads();
                    break;

                case AgentRuntimeUpdateType.ContextMetrics:
                    AgentContextText.Text = update.ContextBudgetTokens > 0
                        ? $"Context: {update.EstimatedContextTokens}/{update.ContextBudgetTokens} tokens{(update.ContextNeedsCompaction ? " (near limit)" : "")}{(update.CompactionApplied ? " · compacted" : "")}"
                        : "Context: -";
                    break;

                case AgentRuntimeUpdateType.Error:
                    AppendAgentMessage("error", update.Message, update.CreatedAtUtc, "error", update.ThreadId);
                    AgentStatusText.Text = $"Error: {update.Message}";
                    break;

                case AgentRuntimeUpdateType.Status:
                    AgentStatusText.Text = string.IsNullOrWhiteSpace(update.Message) ? "Idle" : update.Message;
                    break;
            }
        });
    }

    private bool IsUpdateForCurrentPane(AgentRuntimeUpdate update)
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface == null)
            return false;

        return string.Equals(update.WorkspaceId, ViewModel.SelectedWorkspace?.Workspace.Id ?? "", StringComparison.Ordinal)
            && string.Equals(update.SurfaceId, surface.Surface.Id, StringComparison.Ordinal);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (ctrl && !alt && IsTerminalFocusActive())
            return;

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
                case Key.A: // Toggle agent chat
                    ToggleAgentChat();
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
    private void MenuToggleAgentChat_Click(object sender, RoutedEventArgs e) => ToggleAgentChat();
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
    private void ToggleAgentChat_Click(object sender, RoutedEventArgs e) => ToggleAgentChat();
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

    private void ToggleAgentChat()
    {
        ViewModel.ToggleAgentPanel();
        UpdateAgentPanelLayout();
        if (ViewModel.AgentPanelVisible)
        {
            RefreshAgentThreads();
            AgentPromptBox.Focus();
        }
        else
        {
            FocusTerminal();
        }
    }

    private void UpdateAgentPanelLayout()
    {
        if (ViewModel.AgentPanelVisible)
        {
            var width = Math.Clamp(ViewModel.AgentPanelWidth, 300, 620);
            AgentChatColumn.Width = new GridLength(width);
            AgentChatPanel.Visibility = Visibility.Visible;
            ToolbarAgentChatButton.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            AgentChatColumn.Width = new GridLength(0);
            AgentChatPanel.Visibility = Visibility.Collapsed;
            ToolbarAgentChatButton.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundDimBrush");
        }
    }

    private (SurfaceViewModel Surface, string PaneId)? GetCurrentPaneContext()
    {
        var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
        if (surface == null)
            return null;

        var paneId = surface.FocusedPaneId;
        if (string.IsNullOrWhiteSpace(paneId))
        {
            paneId = surface.RootNode.GetLeaves()
                .Select(l => l.PaneId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        }

        if (string.IsNullOrWhiteSpace(paneId))
            return null;

        return (surface, paneId);
    }

    private void RefreshAgentThreadsIfContextChanged()
    {
        var contextKey = ViewModel.SelectedWorkspace?.Workspace.Id ?? "";

        if (string.Equals(contextKey, _lastAgentContextKey, StringComparison.Ordinal))
            return;

        _lastAgentContextKey = contextKey;
        RefreshAgentThreads();
    }

    private void RefreshAgentThreads()
    {
        var context = GetCurrentPaneContext();
        var workspaceId = ViewModel.SelectedWorkspace?.Workspace.Id ?? "";
        var query = AgentThreadSearchBox.Text?.Trim() ?? "";
        IReadOnlyList<Cmux.Core.Models.AgentConversationThread> threads;

        // Keep history stable across pane/surface changes: list is workspace-wide.
        threads = string.IsNullOrWhiteSpace(query)
            ? App.AgentConversationStore.GetThreads(workspaceId, surfaceId: "", paneId: "")
            : App.AgentConversationStore.SearchThreads(workspaceId, surfaceId: "", paneId: "", query: query);

        if (threads.Count == 0 && !string.IsNullOrWhiteSpace(workspaceId))
        {
            // Last-resort fallback: show global agent history (across workspace ids).
            threads = string.IsNullOrWhiteSpace(query)
                ? App.AgentConversationStore.GetThreads(workspaceId: "", surfaceId: "", paneId: "")
                : App.AgentConversationStore.SearchThreads(workspaceId: "", surfaceId: "", paneId: "", query: query);
        }

        _agentThreadViews = threads
            .Select(t => new AgentThreadView
            {
                Id = t.Id,
                Title = t.Title,
                Meta = $"{t.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {t.MessageCount} msg · tok {t.TotalTokens}",
            })
            .ToList();

        AgentThreadsList.ItemsSource = _agentThreadViews;

        var preferredThreadId = _selectedAgentThreadId;
        if (string.IsNullOrWhiteSpace(preferredThreadId) && context != null)
            preferredThreadId = App.AgentRuntime.GetActiveThreadId(workspaceId, context.Value.Surface.Surface.Id, context.Value.PaneId);
        if (string.IsNullOrWhiteSpace(preferredThreadId))
            preferredThreadId = _agentThreadViews.FirstOrDefault()?.Id;

        if (!string.IsNullOrWhiteSpace(preferredThreadId))
        {
            var match = _agentThreadViews.FirstOrDefault(t => string.Equals(t.Id, preferredThreadId, StringComparison.Ordinal));
            if (match != null)
            {
                bool switchedThread = !string.Equals(_selectedAgentThreadId, match.Id, StringComparison.Ordinal);
                _selectedAgentThreadId = match.Id;
                var currentSelectedId = (AgentThreadsList.SelectedItem as AgentThreadView)?.Id;
                if (!string.Equals(currentSelectedId, match.Id, StringComparison.Ordinal))
                    AgentThreadsList.SelectedItem = match;

                bool needsInitialLoad = _allThreadMessages.Count == 0
                    || !_allThreadMessages.All(m => string.Equals(m.ThreadId, match.Id, StringComparison.Ordinal));

                if (switchedThread || needsInitialLoad)
                    LoadAgentMessages(match.Id);
            }
            else
            {
                // Keep currently loaded messages if user search/filter hides thread list rows.
                if (_allThreadMessages.Count == 0)
                    LoadAgentMessages("");
            }
        }
        else
        {
            if (_allThreadMessages.Count == 0)
                LoadAgentMessages("");
        }
    }

    private void SelectAgentThreadInList(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        var match = _agentThreadViews.FirstOrDefault(t => string.Equals(t.Id, threadId, StringComparison.Ordinal));
        if (match == null)
            return;

        var switchedThread = !string.Equals(_selectedAgentThreadId, match.Id, StringComparison.Ordinal);
        _selectedAgentThreadId = match.Id;

        var currentSelectedId = (AgentThreadsList.SelectedItem as AgentThreadView)?.Id;
        if (!string.Equals(currentSelectedId, match.Id, StringComparison.Ordinal))
            AgentThreadsList.SelectedItem = match;

        if (switchedThread || _allThreadMessages.Count == 0 || !_allThreadMessages.All(m => string.Equals(m.ThreadId, match.Id, StringComparison.Ordinal)))
            LoadAgentMessages(match.Id);
    }

    private void LoadAgentMessages(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            _allThreadMessages = [];
            _visibleThreadMessages.Clear();
            return;
        }

        _streamingAssistantByThread.Remove(threadId);

        var messages = App.AgentConversationStore.GetMessages(threadId, 2000)
            .Select(m =>
            {
                var role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role.Trim().ToLowerInvariant();
                var roleLabel = role switch
                {
                    "assistant" => "assistant",
                    "system" => "system",
                    _ => role == "error" ? "error" : "user",
                };

                var meta = m.TotalTokens > 0
                    ? $"{m.CreatedAtUtc.ToLocalTime():HH:mm:ss} · tok {m.TotalTokens}"
                    : $"{m.CreatedAtUtc.ToLocalTime():HH:mm:ss}";

                if (m.InputTokens > 0 || m.OutputTokens > 0)
                    meta = $"{meta} · in {m.InputTokens} / out {m.OutputTokens}";

                if (!string.IsNullOrWhiteSpace(m.Provider) || !string.IsNullOrWhiteSpace(m.Model))
                    meta = $"{meta} · {m.Provider}/{m.Model}".TrimEnd('/');

                return new AgentChatMessageView
                {
                    ThreadId = threadId,
                    Role = roleLabel,
                    Header = $"{roleLabel} · {m.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                    Content = m.Content,
                    Meta = meta,
                    CreatedAtUtc = m.CreatedAtUtc,
                };
            })
            .ToList();

        _allThreadMessages = messages;
        ApplyAgentMessageFilter();
        ScrollAgentMessagesToBottom();
    }

    private void ApplyAgentMessageFilter()
    {
        var query = AgentMessageSearchBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allThreadMessages
            : _allThreadMessages.Where(m => MatchesMessageFilter(m, query)).ToList();

        _visibleThreadMessages.Clear();
        foreach (var msg in filtered)
            _visibleThreadMessages.Add(msg);
    }

    private void AppendAgentMessage(string role, string content, DateTime createdAtUtc, string meta, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !string.Equals(_selectedAgentThreadId, threadId, StringComparison.Ordinal))
            return;

        var last = _allThreadMessages.LastOrDefault();
        if (last != null &&
            string.Equals(last.ThreadId, threadId, StringComparison.Ordinal) &&
            string.Equals(last.Role, role, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(last.Content?.Trim(), (content ?? "").Trim(), StringComparison.Ordinal) &&
            Math.Abs((last.CreatedAtUtc - createdAtUtc).TotalSeconds) < 3)
        {
            return;
        }

        var view = new AgentChatMessageView
        {
            ThreadId = threadId,
            Role = role,
            Header = $"{role} · {createdAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            Content = content ?? "",
            Meta = string.IsNullOrWhiteSpace(meta) ? createdAtUtc.ToLocalTime().ToString("HH:mm:ss") : meta,
            CreatedAtUtc = createdAtUtc,
        };

        _allThreadMessages.Add(view);
        var query = AgentMessageSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query) || MatchesMessageFilter(view, query))
            _visibleThreadMessages.Add(view);
        ScrollAgentMessagesToBottom();
    }

    private void AppendAssistantDelta(string threadId, string delta)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !string.Equals(_selectedAgentThreadId, threadId, StringComparison.Ordinal))
            return;

        if (!_streamingAssistantByThread.TryGetValue(threadId, out var message))
        {
            message = new AgentChatMessageView
            {
                ThreadId = threadId,
                Role = "assistant",
                Header = $"assistant · {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                Content = "",
                Meta = "streaming...",
                CreatedAtUtc = DateTime.UtcNow,
            };
            _streamingAssistantByThread[threadId] = message;
            _allThreadMessages.Add(message);

            var query = AgentMessageSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query) || MatchesMessageFilter(message, query))
                _visibleThreadMessages.Add(message);
        }

        message.Content += delta ?? "";
        var activeQuery = AgentMessageSearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(activeQuery))
        {
            var visible = _visibleThreadMessages.Contains(message);
            var matches = MatchesMessageFilter(message, activeQuery);
            if (matches && !visible)
                _visibleThreadMessages.Add(message);
            else if (!matches && visible)
                _visibleThreadMessages.Remove(message);
        }
        ScrollAgentMessagesToBottom();
    }

    private void FinalizeAssistantMessage(string threadId, string finalText, DateTime createdAtUtc, string meta)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !string.Equals(_selectedAgentThreadId, threadId, StringComparison.Ordinal))
            return;

        if (_streamingAssistantByThread.TryGetValue(threadId, out var message))
        {
            message.Header = $"assistant · {createdAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            message.Content = string.IsNullOrWhiteSpace(finalText) ? message.Content : finalText;
            message.Meta = meta;
            message.CreatedAtUtc = createdAtUtc;
            _streamingAssistantByThread.Remove(threadId);
        }
        else
        {
            var newMessage = new AgentChatMessageView
            {
                ThreadId = threadId,
                Role = "assistant",
                Header = $"assistant · {createdAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                Content = finalText ?? "",
                Meta = meta,
                CreatedAtUtc = createdAtUtc,
            };
            _allThreadMessages.Add(newMessage);

            var query = AgentMessageSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query) || MatchesMessageFilter(newMessage, query))
                _visibleThreadMessages.Add(newMessage);
        }

        if (!string.IsNullOrWhiteSpace(AgentMessageSearchBox.Text))
            ApplyAgentMessageFilter();
        ScrollAgentMessagesToBottom();
    }

    private void ScrollAgentMessagesToBottom()
    {
        if (AgentMessagesList.Items.Count > 0)
            AgentMessagesList.ScrollIntoView(AgentMessagesList.Items[AgentMessagesList.Items.Count - 1]);
    }

    private static bool IsTerminalFocusActive()
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current != null)
        {
            if (current is TerminalControl)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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
            new() { Id = "toggle-agent-chat", Label = "Toggle Agent Chat", Icon = "\uE11B", Shortcut = "Ctrl+Shift+A", Category = "View", Execute = ToggleAgentChat },
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
        RefreshAgentThreadsIfContextChanged();

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

    private void AgentThreadSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshAgentThreads();
    }

    private void AgentRefreshThreads_Click(object sender, RoutedEventArgs e)
    {
        RefreshAgentThreads();
    }

    private void AgentNewThread_Click(object sender, RoutedEventArgs e)
    {
        var context = GetCurrentPaneContext();
        if (context == null)
            return;

        var workspaceId = ViewModel.SelectedWorkspace?.Workspace.Id ?? "";
        var thread = App.AgentConversationStore.CreateThread(
            workspaceId,
            context.Value.Surface.Surface.Id,
            context.Value.PaneId,
            Cmux.Core.Config.SettingsService.Current.Agent.AgentName);

        App.AgentRuntime.SetActiveThreadId(workspaceId, context.Value.Surface.Surface.Id, context.Value.PaneId, thread.Id);
        _selectedAgentThreadId = thread.Id;
        RefreshAgentThreads();
        SelectAgentThreadInList(thread.Id);
        AgentStatusText.Text = "New thread created";
    }

    private void AgentThreadsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AgentThreadsList.SelectedItem is not AgentThreadView selected)
            return;

        var switchedThread = !string.Equals(_selectedAgentThreadId, selected.Id, StringComparison.Ordinal);
        _selectedAgentThreadId = selected.Id;

        var context = GetCurrentPaneContext();
        if (context != null)
        {
            var workspaceId = ViewModel.SelectedWorkspace?.Workspace.Id ?? "";
            App.AgentRuntime.SetActiveThreadId(workspaceId, context.Value.Surface.Surface.Id, context.Value.PaneId, selected.Id);
        }

        if (switchedThread || _allThreadMessages.Count == 0 || !_allThreadMessages.All(m => string.Equals(m.ThreadId, selected.Id, StringComparison.Ordinal)))
            LoadAgentMessages(selected.Id);
    }

    private void AgentMessageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyAgentMessageFilter();
    }

    private static bool MatchesMessageFilter(AgentChatMessageView message, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return (message.Content?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (message.Header?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (message.Meta?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void AgentPromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SendAgentPromptFromPanel();
            e.Handled = true;
        }
    }

    private void AgentSend_Click(object sender, RoutedEventArgs e)
    {
        SendAgentPromptFromPanel();
    }

    private void SendAgentPromptFromPanel()
    {
        var prompt = AgentPromptBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var context = GetCurrentPaneContext();
        if (context == null)
        {
            AgentStatusText.Text = "No active pane selected";
            return;
        }

        var workspaceId = ViewModel.SelectedWorkspace?.Workspace.Id ?? "";
        var threadId = _selectedAgentThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            var created = App.AgentConversationStore.CreateThread(
                workspaceId,
                context.Value.Surface.Surface.Id,
                context.Value.PaneId,
                Cmux.Core.Config.SettingsService.Current.Agent.AgentName);
            threadId = created.Id;
            _selectedAgentThreadId = threadId;
        }

        App.AgentRuntime.SetActiveThreadId(workspaceId, context.Value.Surface.Surface.Id, context.Value.PaneId, threadId);

        var session = context.Value.Surface.GetSession(context.Value.PaneId);
        bool accepted = App.AgentRuntime.TrySendChatPrompt(
            prompt,
            new AgentPaneContext
            {
                WorkspaceId = workspaceId,
                SurfaceId = context.Value.Surface.Surface.Id,
                PaneId = context.Value.PaneId,
                WorkingDirectory = session?.WorkingDirectory,
                WriteToPane = text =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                        session?.Write(text);
                },
            },
            threadId);

        if (!accepted)
        {
            AgentStatusText.Text = "Agent did not accept the prompt";
            return;
        }

        AgentPromptBox.Text = "";
        AgentStatusText.Text = "Prompt sent";
        RefreshAgentThreads();
        if (!string.IsNullOrWhiteSpace(threadId))
            SelectAgentThreadInList(threadId);
    }

    private void OpenSettings()
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
    }

    private sealed class AgentThreadView
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Meta { get; init; } = "";
    }

    private sealed class AgentChatMessageView : INotifyPropertyChanged
    {
        private string _threadId = "";
        private string _role = "";
        private string _header = "";
        private string _content = "";
        private string _meta = "";
        private DateTime _createdAtUtc = DateTime.UtcNow;

        public string ThreadId
        {
            get => _threadId;
            set
            {
                if (string.Equals(_threadId, value, StringComparison.Ordinal))
                    return;
                _threadId = value;
                OnPropertyChanged(nameof(ThreadId));
            }
        }

        public string Role
        {
            get => _role;
            set
            {
                if (string.Equals(_role, value, StringComparison.Ordinal))
                    return;
                _role = value;
                OnPropertyChanged(nameof(Role));
            }
        }

        public string Header
        {
            get => _header;
            set
            {
                if (string.Equals(_header, value, StringComparison.Ordinal))
                    return;
                _header = value;
                OnPropertyChanged(nameof(Header));
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (string.Equals(_content, value, StringComparison.Ordinal))
                    return;
                _content = value;
                OnPropertyChanged(nameof(Content));
            }
        }

        public string Meta
        {
            get => _meta;
            set
            {
                if (string.Equals(_meta, value, StringComparison.Ordinal))
                    return;
                _meta = value;
                OnPropertyChanged(nameof(Meta));
            }
        }

        public DateTime CreatedAtUtc
        {
            get => _createdAtUtc;
            set
            {
                if (_createdAtUtc == value)
                    return;
                _createdAtUtc = value;
                OnPropertyChanged(nameof(CreatedAtUtc));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
