using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class SurfaceTabBar : UserControl
{
    public SurfaceTabBar()
    {
        InitializeComponent();
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SurfaceViewModel surface)
        {
            if (DataContext is WorkspaceViewModel workspace)
            {
                workspace.SelectedSurface = surface;
            }
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SurfaceViewModel surface)
        {
            if (DataContext is WorkspaceViewModel workspace)
            {
                workspace.CloseSurface(surface);
            }
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
        {
            workspace.CreateNewSurface();
        }
    }
}
