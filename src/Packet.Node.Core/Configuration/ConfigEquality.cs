namespace Packet.Node.Core.Configuration;

/// <summary>
/// Value-equality helpers for config records that carry a collection member.
/// </summary>
/// <remarks>
/// A C# <c>record</c> synthesises member-wise equality, but a collection member
/// (<see cref="IReadOnlyList{T}"/> / <see cref="IReadOnlyDictionary{TKey,TValue}"/>)
/// compares by <b>reference</b> - so two configs with equal-but-distinct lists
/// (which is exactly what a YAML serialise→parse round-trip produces) would be
/// unequal, breaking change-detection identity. Every config record with a
/// collection member therefore hand-rolls <c>Equals</c>/<c>GetHashCode</c> and
/// routes the collection comparison through these helpers, so the logic lives in
/// one place instead of being copy-pasted per record. List comparison is
/// order-significant (<see cref="Enumerable.SequenceEqual{T}(IEnumerable{T}, IEnumerable{T})"/>);
/// dictionary comparison is order-independent.
/// </remarks>
internal static class ConfigEquality
{
    /// <summary>Order-significant value equality for two optional lists (null == null;
    /// reference-equal short-circuits).</summary>
    public static bool ListEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return a.SequenceEqual(b);
    }

    /// <summary>Order-independent value equality for two optional dictionaries (null ==
    /// null; reference-equal short-circuits).</summary>
    public static bool DictEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? a, IReadOnlyDictionary<TKey, TValue>? b)
        where TKey : notnull
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other)
                || !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A content-based hash for a list (order-significant, matching
    /// <see cref="ListEqual{T}"/>). Null hashes to 0.</summary>
    public static int ListHash<T>(IReadOnlyList<T>? items)
    {
        if (items is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    /// <summary>A content-based hash for a dictionary (order-independent, matching
    /// <see cref="DictEqual{TKey,TValue}"/>). Null hashes to 0.</summary>
    public static int DictHash<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? items)
        where TKey : notnull
    {
        if (items is null)
        {
            return 0;
        }
        var acc = 0;
        foreach (var (key, value) in items)
        {
            acc ^= HashCode.Combine(key, value);   // XOR ⇒ order-independent
        }
        return acc;
    }
}
