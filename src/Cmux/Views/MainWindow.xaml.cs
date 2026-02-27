using System.Windows;
using System.Windows.Input;
using Cmux.ViewModels;

namespace Cmux.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
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
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
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
            }
        }

        // Ctrl+Alt+Arrow: Focus pane directionally
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

}