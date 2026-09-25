namespace Packet.Aprs;

/// <summary>
/// One entry in a packet's digipeater (via) path: an address and whether it has been used.
/// </summary>
/// <remarks>
/// In an AX.25 frame each digipeater address carries its own has-been-repeated (H) bit. The
/// TNC2 text form marks only the last used entry with <c>*</c>; every earlier entry is implied
/// used (UAP §1.3.1, APRS-Digipeater-Algorithm §2.1). Decoding TNC2 text sets
/// <see cref="HasBeenRepeated"/> on the starred entry and every entry before it, and encoding
/// writes a single <c>*</c> after the last used entry.
/// </remarks>
/// <param name="Address">The digipeater, alias or APRS-IS path element.</param>
/// <param name="HasBeenRepeated">True if this entry has been used (AX.25 H bit set).</param>
public readonly record struct AprsPathEntry(AprsAddress Address, bool HasBeenRepeated = false)
{
    /// <summary>Parses a comma-separated TNC2 path such as <c>WIDE1-1,WIDE2-1</c> or
    /// <c>N2GH,W2UB*,WIDE2-1</c>. An empty string gives an empty path.</summary>
    public static IReadOnlyList<AprsPathEntry> ParseList(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return [];
        }

        string[] parts = path.Split(',');
        var entries = new AprsPathEntry[parts.Length];
        int lastStar = -1;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.EndsWith('*'))
            {
                part = part[..^1];
                lastStar = i;
            }

            entries[i] = new AprsPathEntry(AprsAddress.Parse(part));
        }

        for (int i = 0; i <= lastStar; i++)
        {
            entries[i] = entries[i] with { HasBeenRepeated = true };
        }

        return entries;
    }

    /// <summary>Formats a path in TNC2 form with a single <c>*</c> after the last used entry.</summary>
    public static string FormatList(IEnumerable<AprsPathEntry> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var list = path as IReadOnlyList<AprsPathEntry> ?? path.ToList();
        int lastUsed = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].HasBeenRepeated)
            {
                lastUsed = i;
            }
        }

        return string.Join(',', list.Select((e, i) => i == lastUsed ? e.Address.Value + "*" : e.Address.Value));
    }

    /// <summary>Returns the address, followed by <c>*</c> if used.</summary>
    public override string ToString() => HasBeenRepeated ? Address.Value + "*" : Address.Value;
}
