using System.Collections;

namespace Packet.Aprs.Internal;

/// <summary>
/// A read-only list with value equality, so records that hold lists compare by content.
/// Public properties expose it as <see cref="IReadOnlyList{T}"/> and wrap whatever the caller
/// supplies on init.
/// </summary>
internal sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    private readonly T[] items;

    private EquatableList(T[] items) => this.items = items;

    public static EquatableList<T> Of(IEnumerable<T>? source) => source switch
    {
        null => Empty,
        EquatableList<T> e => e,
        _ => source.Any() ? new EquatableList<T>([.. source]) : Empty,
    };

    public int Count => items.Length;

    public T this[int index] => items[index];

    public bool Equals(EquatableList<T>? other) =>
        other is not null && items.AsSpan().SequenceEqual(other.items, EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (T item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    public override string ToString() => $"[{string.Join(", ", items)}]";
}
