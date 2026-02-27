using System.Collections.ObjectModel;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Manages terminal notifications. Tracks unread state, provides
/// jump-to-unread functionality, and fires Windows toast notifications.
/// </summary>
public class NotificationService
{
    private readonly ObservableCollection<TerminalNotification> _notifications = [];
    private readonly object _lock = new();

    public ObservableCollection<TerminalNotification> Notifications => _notifications;
    public int UnreadCount => _notifications.Count(n => !n.IsRead);

    public event Action<TerminalNotification>? NotificationAdded;
    public event Action? UnreadCountChanged;

    /// <summary>
    /// Adds a new notification.
    /// </summary>
    public void AddNotification(
        string workspaceId,
        string surfaceId,
        string? paneId,
        string title,
        string? subtitle,
        string body,
        NotificationSource source)
    {
        var notification = new TerminalNotification
        {
            WorkspaceId = workspaceId,
            SurfaceId = surfaceId,
            PaneId = paneId,
            Title = title,
            Subtitle = subtitle,
            Body = body,
            Source = source,
            IsRead = false,
        };

        lock (_lock)
        {
            _notifications.Insert(0, notification);

            // Keep max 500 notifications
            while (_notifications.Count > 500)
                _notifications.RemoveAt(_notifications.Count - 1);
        }

        NotificationAdded?.Invoke(notification);
        UnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Marks a notification as read.
    /// </summary>
    public void MarkAsRead(string notificationId)
    {
        lock (_lock)
        {
            var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null)
            {
                notification.IsRead = true;
                UnreadCountChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Marks all notifications for a workspace as read.
    /// </summary>
    public void MarkWorkspaceAsRead(string workspaceId)
    {
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => n.WorkspaceId == workspaceId && !n.IsRead))
                n.IsRead = true;
        }
        UnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Marks all notifications as read.
    /// </summary>
    public void MarkAllAsRead()
    {
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => !n.IsRead))
                n.IsRead = true;
        }
        UnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Gets the most recent unread notification.
    /// </summary>
    public TerminalNotification? GetLatestUnread()
    {
        lock (_lock)
        {
            return _notifications.FirstOrDefault(n => !n.IsRead);
        }
    }

    /// <summary>
    /// Gets unread count for a specific workspace.
    /// </summary>
    public int GetUnreadCount(string workspaceId)
    {
        lock (_lock)
        {
            return _notifications.Count(n => n.WorkspaceId == workspaceId && !n.IsRead);
        }
    }

    /// <summary>
    /// Gets the latest notification text for a workspace (for sidebar display).
    /// </summary>
    public string? GetLatestText(string workspaceId)
    {
        lock (_lock)
        {
            var latest = _notifications.FirstOrDefault(n => n.WorkspaceId == workspaceId);
            return latest?.Body;
        }
    }

    /// <summary>
    /// Clears all notifications.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _notifications.Clear();
        }
        UnreadCountChanged?.Invoke();
    }
}
