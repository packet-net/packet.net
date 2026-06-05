using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// Codec for the information field carried by a NET/ROM L4 <b>Connect Request</b>
/// (opcode 0x01) — the one transport message whose info field has a defined
/// structure (the others carry user data or are empty). It conveys the
/// <em>proposed send-window</em> and the <em>originating user + originating node</em>
/// callsigns end-to-end, so the accepting node knows who is calling and can
/// negotiate the window down.
/// </summary>
/// <remarks>
/// <para>Wire layout (the de-facto NET/ROM form), 15 octets at the front of the
/// Connect Request info field:</para>
/// <code>
///   [1] proposed send-window size (1..127)
///   [7] originating user callsign  (AX.25 shifted form)
///   [7] originating node callsign  (AX.25 shifted form)
///   (any trailing octets are an implementation extension — e.g. LinBPQ appends a
///    timeout/flags pair — and are ignored on parse)
/// </code>
/// <para>
/// <b>Why the window lives here, not in the transport header.</b> The 5-octet
/// transport header's TX/RX-sequence slots are 0 on a Connect Request; the proposed
/// window is the <em>first info octet</em>. This was verified on the wire against a
/// real LinBPQ 6.0.25.23 over the interop stack (#308 follow-up): BPQ both
/// <em>originates</em> its Connect Request as <c>[window][user][node][bpq-extra…]</c>
/// and <em>accepts</em> ours in the same shape; the earlier placement of the window
/// in the transport-header TX byte was a divergence that mis-set the negotiated
/// window. The originating callsigns being in the info field is the canonical NET/ROM
/// appendix behaviour. Construction is strict (we always emit the canonical
/// 15-octet form); parsing is total and tolerant of trailing extension octets.
/// </para>
/// </remarks>
public static class ConnectRequestInfo
{
    /// <summary>Octets the canonical Connect Request info field occupies (window +
    /// two shifted callsigns). A peer may append extension octets after these.</summary>
    public const int Length = 1 + NetRomCallsign.ShiftedLength + NetRomCallsign.ShiftedLength; // 15

    /// <summary>Build the Connect Request info field: proposed window then the
    /// originating user + node callsigns (both AX.25 shifted).</summary>
    public static byte[] Build(byte proposedWindow, Callsign originatingUser, Callsign originatingNode)
    {
        var buf = new byte[Length];
        buf[0] = proposedWindow;
        NetRomCallsign.WriteShifted(originatingUser, buf.AsSpan(1));
        NetRomCallsign.WriteShifted(originatingNode, buf.AsSpan(1 + NetRomCallsign.ShiftedLength));
        return buf;
    }

    /// <summary>
    /// Parse the proposed window + originating user/node from a Connect Request info
    /// field. Returns <c>false</c> (never throws) if the field is shorter than the
    /// 15-octet canonical layout or a callsign field is undecodable. Trailing octets
    /// beyond the 15 (a peer's extension) are ignored.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, out byte proposedWindow, out Callsign originatingUser, out Callsign originatingNode)
    {
        proposedWindow = 0;
        originatingUser = default;
        originatingNode = default;
        if (info.Length < Length)
        {
            return false;
        }

        proposedWindow = info[0];
        if (!NetRomCallsign.TryReadShifted(info[1..], out originatingUser))
        {
            return false;
        }
        if (!NetRomCallsign.TryReadShifted(info[(1 + NetRomCallsign.ShiftedLength)..], out originatingNode))
        {
            return false;
        }
        return true;
    }
}
