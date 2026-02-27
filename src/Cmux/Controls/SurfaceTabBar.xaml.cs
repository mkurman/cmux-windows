using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Linq;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class SurfaceTabBar : UserControl
{
    private SurfaceViewModel? _renamingSurface;

    public SurfaceTabBar()
    {
        InitializeComponent();
    }

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
}
