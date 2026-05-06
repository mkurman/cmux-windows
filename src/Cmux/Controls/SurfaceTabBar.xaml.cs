using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Linq;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class SurfaceTabBar : UserControl
{
    private SurfaceViewModel? _renamingSurface;

    public event Action<string>? SearchTextChanged;
    public event Action? NextMatchRequested;
    public event Action? PreviousMatchRequested;

    public SurfaceTabBar()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    public void UpdateMatchCount(int current, int total)
    {
        MatchCount.Text = total > 0 ? $"{current + 1}/{total}" : "";
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
            SearchInput.Text = "";
            var window = Window.GetWindow(this);
            window?.Focus();
            e.Handled = true;
        }
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => PreviousMatchRequested?.Invoke();
    private void NextMatch_Click(object sender, RoutedEventArgs e) => NextMatchRequested?.Invoke();

    private SurfaceViewModel? GetSurfaceFromMenu(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu ctx)
            return ctx.Tag as SurfaceViewModel;
        return null;
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel surface)
        {
            if (e.ClickCount == 2)
            {
                _renamingSurface = surface;
                TabRenameBox.Text = surface.Name;
                TabRenameBox.Visibility = Visibility.Visible;
                TabRenameBox.SelectAll();
                TabRenameBox.Focus();
                e.Handled = true;
                return;
            }
            if (DataContext is WorkspaceViewModel workspace)
                workspace.SelectedSurface = surface;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SurfaceViewModel surface)
        {
            if (DataContext is WorkspaceViewModel workspace)
                workspace.CloseSurface(surface);
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
            workspace.CreateNewSurface();
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface == null) return;
        _renamingSurface = surface;
        TabRenameBox.Text = surface.Name;
        TabRenameBox.Visibility = Visibility.Visible;
        TabRenameBox.SelectAll();
        TabRenameBox.Focus();
    }

    private void FinishTabRename(bool save)
    {
        if (_renamingSurface != null && save)
            _renamingSurface.Name = TabRenameBox.Text;
        _renamingSurface = null;
        TabRenameBox.Visibility = Visibility.Collapsed;
    }

    private void TabRenameBox_LostFocus(object sender, RoutedEventArgs e) => FinishTabRename(true);

    private void TabRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FinishTabRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { FinishTabRename(false); e.Handled = true; }
    }

    private void DuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel ws)
        {
            ws.CreateNewSurface();
            var newSurf = ws.Surfaces[^1];
            var original = GetSurfaceFromMenu(sender);
            if (original != null) newSurf.Name = original.Name + " (copy)";
        }
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitRight();
        }
    }

    private void SplitDown_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            ws.SelectedSurface = surface;
            surface.SplitDown();
        }
    }

    private void CloseThisTab_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
            ws.CloseSurface(surface);
    }

    private void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var surface = GetSurfaceFromMenu(sender);
        if (surface != null && DataContext is WorkspaceViewModel ws)
        {
            var others = ws.Surfaces.Where(s => s != surface).ToList();
            foreach (var other in others)
                ws.CloseSurface(other);
        }
    }

    /// <summary>
    /// Populates the "Move to" submenu with the other workspaces at the moment the user
    /// hovers over it. Built lazily so it always reflects the live workspace list.
    /// </summary>
    private void MoveToMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem moveMenu)
        {
            App.DaemonLog($"[MoveTo] sender is not MenuItem: {sender?.GetType().Name}");
            return;
        }

        // MenuItem.Parent is not reliable for items that own a submenu — walk the
        // logical tree until we find the owning ContextMenu.
        var ctx = FindAncestorContextMenu(moveMenu);
        if (ctx == null)
        {
            App.DaemonLog("[MoveTo] could not locate parent ContextMenu");
            return;
        }

        // Tag was bound via {Binding} on the ContextMenu definition; the inherited
        // DataContext is the SurfaceViewModel for the right-clicked tab.
        SurfaceViewModel? surface = ctx.Tag as SurfaceViewModel
            ?? (ctx.PlacementTarget as FrameworkElement)?.DataContext as SurfaceViewModel;

        if (surface == null)
        {
            App.DaemonLog($"[MoveTo] ctx.Tag={ctx.Tag?.GetType().Name ?? "null"} placementTarget DataContext lookup failed");
            return;
        }

        moveMenu.Items.Clear();

        var main = (Application.Current.MainWindow?.DataContext) as MainViewModel;
        if (main == null)
        {
            App.DaemonLog("[MoveTo] MainWindow.DataContext is not MainViewModel");
            moveMenu.Items.Add(new MenuItem { Header = "(no workspaces)", IsEnabled = false });
            return;
        }

        var sourceWorkspace = DataContext as WorkspaceViewModel;
        var targets = main.Workspaces.Where(w => w != sourceWorkspace).ToList();
        if (targets.Count == 0)
        {
            moveMenu.Items.Add(new MenuItem { Header = "(create new workspace)", Cursor = System.Windows.Input.Cursors.Hand });
            ((MenuItem)moveMenu.Items[0]!).Click += (_, _) =>
            {
                App.DaemonLog($"[MoveTo] no other workspaces — creating new and moving surface={surface.Name}");
                main.CreateNewWorkspace();
                var newWs = main.Workspaces[^1];
                // The just-created workspace already has an empty surface; remove it so the moved one stands alone.
                main.MoveSurfaceToWorkspace(surface, newWs);
            };
            return;
        }

        foreach (var target in targets)
        {
            var item = new MenuItem { Header = target.Name, Cursor = System.Windows.Input.Cursors.Hand };
            var capturedSurface = surface;
            var capturedTarget = target;
            item.Click += (_, _) =>
            {
                App.DaemonLog($"[MoveTo] click → moving surface={capturedSurface.Name} to workspace={capturedTarget.Name}");
                main.MoveSurfaceToWorkspace(capturedSurface, capturedTarget);
            };
            moveMenu.Items.Add(item);
        }
    }

    private static ContextMenu? FindAncestorContextMenu(DependencyObject start)
    {
        DependencyObject? cur = start;
        while (cur != null)
        {
            if (cur is ContextMenu cm) return cm;
            cur = System.Windows.LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }
}
