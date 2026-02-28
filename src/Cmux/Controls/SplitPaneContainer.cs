using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cmux.Core.Models;
using Cmux.Core.Config;
using Cmux.Core.Terminal;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

/// <summary>
/// Recursively renders a SplitNode tree as nested Grid panels with
/// GridSplitters for resizable dividers. Leaf nodes contain TerminalControl instances.
/// </summary>
public class SplitPaneContainer : ContentControl
{
    private SurfaceViewModel? _surface;
    private readonly Dictionary<string, TerminalControl> _terminalCache = [];

    public event Action? SearchRequested;

    public SplitPaneContainer()
    {
        Background = Brushes.Transparent;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SurfaceViewModel oldSurface)
        {
            oldSurface.PropertyChanged -= OnSurfacePropertyChanged;
        }

        // Clear terminal cache when switching surfaces/workspaces
        // This prevents reusing terminals from a different workspace
        _terminalCache.Clear();

        _surface = e.NewValue as SurfaceViewModel;

        if (_surface != null)
        {
            _surface.PropertyChanged += OnSurfacePropertyChanged;
            Rebuild();
        }
        else
        {
            Content = null;
        }
    }

    private void OnSurfacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurfaceViewModel.RootNode)
            or nameof(SurfaceViewModel.IsZoomed))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
        else if (e.PropertyName is nameof(SurfaceViewModel.FocusedPaneId))
        {
            Dispatcher.BeginInvoke(UpdateFocusState);
        }
    }

    /// <summary>
    /// Updates only focus-related visual state on cached terminals without
    /// rebuilding the entire UI tree.
    /// </summary>
    private void UpdateFocusState()
    {
        if (_surface == null) return;

        // In zoom mode, focus change may require rebuild if the zoomed pane changed
        if (_surface.IsZoomed)
        {
            Rebuild();
            return;
        }

        foreach (var (paneId, terminal) in _terminalCache)
        {
            terminal.IsPaneFocused = paneId == _surface.FocusedPaneId;
        }
    }

    private void Rebuild()
    {
        if (_surface == null) return;

        // Zoom mode: show only the focused pane full-size
        if (_surface.IsZoomed && _surface.FocusedPaneId != null)
        {
            var focusedNode = _surface.RootNode.FindNode(_surface.FocusedPaneId);
            if (focusedNode != null)
            {
                Content = BuildLeaf(focusedNode);
                return;
            }
        }

        Content = BuildNode(_surface.RootNode);
    }

    private UIElement BuildNode(SplitNode node)
    {
        if (node.IsLeaf)
        {
            return BuildLeaf(node);
        }

        return BuildSplit(node);
    }

    private UIElement BuildLeaf(SplitNode node)
    {
        if (node.PaneId == null)
            return new Border { Background = Brushes.Transparent };

        var paneId = node.PaneId; // Capture for closures

        // Reuse cached terminal if available (preserves session and scroll position)
        if (!_terminalCache.TryGetValue(paneId, out var terminal))
        {
            terminal = new TerminalControl();
            _terminalCache[paneId] = terminal;
        }
        else
        {
            // Detach from old parent before reusing
            // Terminal could be inside DockPanel (with header) or Border
            var oldParent = System.Windows.Media.VisualTreeHelper.GetParent(terminal) as FrameworkElement;
            
            if (oldParent is DockPanel dockPanel)
            {
                dockPanel.Children.Remove(terminal);
            }
            else if (oldParent is Border border)
            {
                border.Child = null;
            }
            
            // Clear old event handlers to prevent memory leaks and wrong callbacks
            terminal.ClearEventHandlers();
        }

        // Wire up event handlers with closures capturing the current pane ID
        terminal.FocusRequested += () => _surface?.FocusPane(paneId);
        terminal.CommandInterceptRequested += command => _surface?.TryHandlePaneCommand(paneId, command) == true;
        terminal.CommandSubmitted += command => _surface?.RegisterCommandSubmission(paneId, command);
        terminal.ClearRequested += () => _surface?.CapturePaneTranscript(paneId, "clear-terminal");
        terminal.SplitRequested += dir =>
        {
            _surface?.FocusPane(paneId);
            _surface?.SplitFocused(dir);
        };
        terminal.ZoomRequested += () => _surface?.ToggleZoom();
        terminal.ClosePaneRequested += () => _surface?.ClosePane(paneId);
        terminal.SearchRequested += () => SearchRequested?.Invoke();
        terminal.IsPaneFocused = paneId == _surface?.FocusedPaneId;
        terminal.IsSurfaceZoomed = _surface?.IsZoomed == true;

        // Attach the terminal session
        var session = _surface?.GetSession(paneId);
        if (session != null)
            terminal.AttachSession(session);

        // Get pane title (custom name takes precedence over shell title)
        var title = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";

        // Create panel with header
        var panel = new DockPanel { LastChildFill = true };

        // Header bar with title and close button
        var header = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x2E)),
            Height = 24,
            Padding = new Thickness(8, 2, 8, 2),
        };

        var headerMenu = new ContextMenu();
        var renamePane = new MenuItem { Header = "Rename Pane" };
        renamePane.Click += (_, _) =>
        {
            var currentName = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";
            var prompt = new TextPromptWindow(
                title: "Rename Pane",
                message: "Set a custom name for this pane.",
                defaultValue: currentName)
            {
                Owner = Window.GetWindow(this),
            };

            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.ResponseText))
                _surface?.SetPaneCustomName(paneId, prompt.ResponseText);
        };
        headerMenu.Items.Add(renamePane);

        var resetPaneName = new MenuItem { Header = "Reset Pane Name" };
        resetPaneName.Click += (_, _) => _surface?.SetPaneCustomName(paneId, string.Empty);
        headerMenu.Items.Add(resetPaneName);

        header.ContextMenu = headerMenu;

        DockPanel.SetDock(header, Dock.Top);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Focus indicator
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Close button

        // Focus indicator (shows which pane is focused)
        var focusIndicator = new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = terminal.IsPaneFocused
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x81, 0x8C, 0xF8))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x3B, 0x4F)),
        };
        Grid.SetColumn(focusIndicator, 0);

        // Title text
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);

        // Close button
        var closeButton = new Button
        {
            Content = "\u2715",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close pane",
        };
        closeButton.Click += (s, e) => _surface?.ClosePane(paneId);
        Grid.SetColumn(closeButton, 2);

        headerGrid.Children.Add(focusIndicator);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(closeButton);
        header.Child = headerGrid;

        panel.Children.Add(header);
        panel.Children.Add(terminal);

        return new Border
        {
            Child = panel,
            BorderBrush = terminal.IsPaneFocused
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x81, 0x8C, 0xF8))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x3E)),
            BorderThickness = new Thickness(1),
        };
    }


    private UIElement BuildSplit(SplitNode node)
    {
        var grid = new Grid();

        if (node.Direction == SplitDirection.Vertical)
        {
            // Left | Right
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(4, GridUnitType.Pixel),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetColumn(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeWE,
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetColumn(second, 2);
                grid.Children.Add(second);
            }
        }
        else
        {
            // Top / Bottom
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(4, GridUnitType.Pixel),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetRow(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeNS,
            };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetRow(second, 2);
                grid.Children.Add(second);
            }
        }

        return grid;
    }

    /// <summary>
    /// Updates settings for all cached terminal controls.
    /// </summary>
    public void UpdateAllTerminals(TerminalTheme theme, string fontFamily, int fontSize)
    {
        foreach (var terminal in _terminalCache.Values)
        {
            terminal.UpdateSettings(theme, fontFamily, fontSize);
        }
    }
}
