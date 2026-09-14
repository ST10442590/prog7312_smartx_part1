using System.Collections;

namespace SmartX.Shared.Collections;

// A fixed-capacity circular buffer: once full, each new item overwrites the
// oldest one.
// <remarks>
// <para><b>Why a custom structure rather than List or Queue.</b> The Pulse
// Grid needs a rolling window of the last N readings for every node in the
// mesh, and nothing older. A <c>List&lt;T&gt;</c> would grow without bound.
// A <c>Queue&lt;T&gt;</c> would need a Dequeue for every Enqueue and still
// reallocates as it grows. This buffer allocates its array exactly once at
// construction and then never allocates again, no matter how many millions
// of packets pass through it — which is the property that matters when the
// structure is instantiated once per device across thousands of devices.</para>
//
// <para>Enumeration yields items oldest-first and uses a struct enumerator,
// so a <c>foreach</c> over the window allocates nothing on the heap.</para>
// </remarks>
public sealed class RingBuffer<T> : IReadOnlyCollection<T>
{
    private readonly T[] _items;
    private int _head;      // index of the next write
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(
                nameof(capacity), "Capacity must be at least 1.");

        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsFull => _count == _items.Length;

    // Adds an item, overwriting the oldest once full. Constant time, and
    // never allocates.
    // <returns>
    // The item that was evicted, or default when nothing was evicted yet.
    // </returns>
    public T? Add(T item)
    {
        T? evicted = default;
        if (IsFull) evicted = _items[_head];

        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        if (_count < _items.Length) _count++;

        return evicted;
    }

    // Indexed oldest-first: <c>this[0]</c> is the oldest item.
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var start = IsFull ? _head : 0;
            return _items[(start + index) % _items.Length];
        }
    }

    // The most recently added item.
    public T Newest =>
        _count == 0
            ? throw new InvalidOperationException("The buffer is empty.")
            : this[_count - 1];

    // The oldest item still retained.
    public T Oldest =>
        _count == 0
            ? throw new InvalidOperationException("The buffer is empty.")
            : this[0];

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    // Copies the window into a new array, oldest-first.
    public T[] ToArray()
    {
        var copy = new T[_count];
        for (var i = 0; i < _count; i++) copy[i] = this[i];
        return copy;
    }

    // foreach binds to this struct overload before the interface one,
    // so iterating the window allocates no enumerator on the heap.
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    public struct Enumerator : IEnumerator<T>
    {
        private readonly RingBuffer<T> _buffer;
        private int _index;

        internal Enumerator(RingBuffer<T> buffer)
        {
            _buffer = buffer;
            _index = -1;
        }

        public T Current => _buffer[_index];

        object? IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _buffer._count;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
