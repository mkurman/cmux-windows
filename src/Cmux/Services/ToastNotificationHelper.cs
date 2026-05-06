using Microsoft.Toolkit.Uwp.Notifications;
using Cmux.Core.Models;

namespace Cmux.Services;

/// <summary>
/// Sends Windows toast notifications when AI coding agents need attention.
/// Uses the Windows 10/11 notification system via Microsoft.Toolkit.Uwp.Notifications.
/// </summary>
public static class ToastNotificationHelper
{
    /// <summary>
    /// Shows a Windows toast notification for a terminal notification.
    /// </summary>
    public static void ShowToast(TerminalNotification notification, string workspaceName)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(notification.Title)
                .AddText(notification.Body)
                .AddAttributionText($"Workspace: {workspaceName}")
                .AddArgument("action", "jumpToNotification")
                .AddArgument("notificationId", notification.Id)
                .AddArgument("workspaceId", notification.WorkspaceId)
                .AddArgument("surfaceId", notification.SurfaceId)
                .Show();
        }
        catch (Exception ex)
        {
            // Surface the failure in the daemon log so users can diagnose
            // missing toasts on Win10 (AUMID/COM activator/shortcut issues).
            App.DaemonLog($"[Toast] ShowToast failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all cmux toast notifications from the notification center.
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch
        {
            // Best effort
        }
    }
}
