namespace Packet.Aprs;

/// <summary>
/// The APRS-IS q-construct in a packet's path, e.g. <c>qAR,N3LLO-2</c>: how the packet entered
/// APRS-IS and through which station or server (APRS12c §17 refers to the APRS-IS specification;
/// UAP §4). Present only on packets that came from APRS-IS.
/// </summary>
/// <param name="Construct">The construct itself, e.g. <c>qAR</c> (received on RF by an IGate),
/// <c>qAO</c> (receive-only IGate), <c>qAC</c> (sent directly to a server by an internet client).</param>
/// <param name="Station">The address after the construct: the IGate or server, or null if missing.</param>
/// <param name="PathIndex">Where the construct sits in <see cref="AprsPacket.Path"/>. Entries before it are
/// the path the packet had before APRS-IS (the RF path for an IGated packet).</param>
public readonly record struct AprsQConstruct(string Construct, AprsAddress? Station, int PathIndex)
{
    /// <summary>True for constructs that mean the packet was heard on RF by an IGate (<c>qAR</c>, <c>qAr</c>, <c>qAo</c>, <c>qAO</c>).</summary>
    public bool IsFromRf => Construct is "qAR" or "qAr" or "qAo" or "qAO";

    internal static AprsQConstruct? Find(IReadOnlyList<AprsPathEntry> path)
    {
        for (int i = 0; i < path.Count; i++)
        {
            string v = path[i].Address.Value;
            if (v.Length == 3 && v[0] == 'q' && v[1] is >= 'A' and <= 'Z' && char.IsAsciiLetter(v[2]))
            {
                return new AprsQConstruct(v, i + 1 < path.Count ? path[i + 1].Address : null, i);
            }
        }

        return null;
    }
}
