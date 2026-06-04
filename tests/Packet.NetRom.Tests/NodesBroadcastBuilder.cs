using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests;

/// <summary>
/// Test-only encoder for NET/ROM NODES-broadcast information fields. The
/// production library is strictly read-only (it parses heard broadcasts and
/// never originates one), so the tests bring their own byte builder to exercise
/// the parser and the routing table with realistic, spec-shaped input — encode
/// here, parse with <see cref="NodesBroadcast.TryParse"/>, assert.
/// </summary>
/// <remarks>
/// The callsign fields use the genuine AX.25 shifted form via
/// <see cref="Ax25Address.Write"/> — the same codec the parser decodes with — so
/// a round-trip proves the shift/SSID handling, not a tautology against a
/// hand-rolled encoder.
/// </remarks>
internal static class NodesBroadcastBuilder
{
    /// <summary>Build a NODES info field: 0xFF signature + 6-byte alias + the entries.</summary>
    public static byte[] Build(string senderAlias, params (Callsign Dest, string DestAlias, Callsign Neighbour, byte Quality)[] entries)
    {
        var buf = new List<byte> { NodesBroadcast.Signature };
        buf.AddRange(EncodeAlias(senderAlias));
        foreach (var e in entries)
        {
            buf.AddRange(EncodeShifted(e.Dest));
            buf.AddRange(EncodeAlias(e.DestAlias));
            buf.AddRange(EncodeShifted(e.Neighbour));
            buf.Add(e.Quality);
        }
        return [.. buf];
    }

    /// <summary>Encode a callsign in the 7-octet AX.25 shifted form.</summary>
    public static byte[] EncodeShifted(Callsign call)
    {
        var addr = new Ax25Address(call, CrhBit: false, ExtensionBit: false);
        var bytes = new byte[Ax25Address.EncodedLength];
        addr.Write(bytes);
        return bytes;
    }

    /// <summary>Encode a 6-char alias as plain space-padded ASCII (truncated to 6).</summary>
    public static byte[] EncodeAlias(string alias)
    {
        var bytes = new byte[NetRomCallsign.AliasLength];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)' ';
        }
        for (int i = 0; i < Math.Min(alias.Length, bytes.Length); i++)
        {
            bytes[i] = (byte)alias[i];
        }
        return bytes;
    }
}
