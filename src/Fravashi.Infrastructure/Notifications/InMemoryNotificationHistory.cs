using Fravashi.Application.Notifications;

namespace Fravashi.Infrastructure.Notifications;

public sealed class InMemoryNotificationHistory(int capacity = 50) : INotificationHistory
{
    private readonly object _gate = new();
    private readonly List<StudioNotification> _items = [];

    public event Action? Changed;

    public IReadOnlyList<StudioNotification> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(n => !n.IsRead);
            }
        }
    }

    public void Add(string message, NotificationSeverity severity = NotificationSeverity.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_gate)
        {
            _items.Insert(0, new StudioNotification(Guid.NewGuid(), DateTimeOffset.UtcNow, message.Trim(), severity, false));
            if (_items.Count > capacity)
            {
                _items.RemoveRange(capacity, _items.Count - capacity);
            }
        }

        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                _items[i] = item with { IsRead = true };
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }

        Changed?.Invoke();
    }
}
