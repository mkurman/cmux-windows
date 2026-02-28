namespace Cmux.Core.Terminal;

/// <summary>
/// A fixed-capacity circular buffer for terminal scrollback lines.
/// Provides O(1) Add and RemoveOldest operations, avoiding the O(n)
/// cost of List&lt;T&gt;.RemoveAt(0).
/// </summary>
public sealed class ScrollbackBuffer<T>
{
    private T[] _items;
    private int _head; // Index of the oldest item
    private int _count;

    public int Count => _count;
    public int Capacity => _items.Length;

    public ScrollbackBuffer(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length];
        }
    }

    /// <summary>
    /// Adds an item to the end. If at capacity, the oldest item is overwritten.
    /// </summary>
    public void Add(T item)
    {
        int insertIndex = (_head + _count) % _items.Length;
        _items[insertIndex] = item;

        if (_count < _items.Length)
        {
            _count++;
        }
        else
        {
            // Buffer is full — advance head (oldest item is overwritten)
            _head = (_head + 1) % _items.Length;
        }
    }

    /// <summary>
    /// Adds all items from the given list.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Copies all items to a new list (in order from oldest to newest).
    /// </summary>
    public List<T> ToList()
    {
        var result = new List<T>(_count);
        for (int i = 0; i < _count; i++)
            result.Add(_items[(_head + i) % _items.Length]);
        return result;
    }
}
