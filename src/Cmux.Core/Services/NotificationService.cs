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
    /// Marks all notifications targeting a specific surface as read — call
    /// when the user activates the surface tab so the tab indicator clears.
    /// </summary>
    public void MarkSurfaceAsRead(string surfaceId)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => n.SurfaceId == surfaceId && !n.IsRead))
            {
                n.IsRead = true;
                changed = true;
            }
        }
        if (changed) UnreadCountChanged?.Invoke();
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
    /// Gets unread count for a specific surface — used to light up the surface
    /// tab when an agent posts something while the user is on a different tab.
    /// </summary>
    public int GetUnreadCountForSurface(string surfaceId)
    {
        lock (_lock)
        {
            return _notifications.Count(n => n.SurfaceId == surfaceId && !n.IsRead);
        }
    }

    /// <summary>
    /// Gets unread count for a specific pane — used to ring the pane border.
    /// Notifications without a paneId (e.g. `cmux notify` from outside any
    /// pane) fall back to matching the surface so the active tab still rings.
    /// </summary>
    public int GetUnreadCountForPane(string paneId, string surfaceId)
    {
        lock (_lock)
        {
            return _notifications.Count(n => !n.IsRead &&
                ((n.PaneId == paneId) ||
                 (string.IsNullOrEmpty(n.PaneId) && n.SurfaceId == surfaceId)));
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
