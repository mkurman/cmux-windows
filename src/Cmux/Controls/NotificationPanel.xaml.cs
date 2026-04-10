using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cmux.Core.Models;

namespace Cmux.Controls;

public partial class NotificationPanel : UserControl
{
    /// <summary>Raised when a notification item is clicked.</summary>
    public event Action<TerminalNotification>? NotificationClicked;

    public NotificationPanel()
    {
        InitializeComponent();
    }

    private void NotificationItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalNotification notification })
        {
            NotificationClicked?.Invoke(notification);
        }
    }
}
