using System.Diagnostics.CodeAnalysis;
using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// The INP3 <c>L3RTT</c> link-time-measurement frame - an <em>ordinary</em> L3
/// info datagram, not a new frame family. It is a <see cref="NetRomPacket"/> whose
/// destination node callsign is the literal <c>L3RTT-0</c>, whose transport opcode
/// nibble is <c>0x02</c>, and whose payload is space-padded ASCII carrying the
/// INP3 capability flags (<c>$N</c> = "I speak INP3", <c>$IX</c> = "I accept IP
/// version X"). The neighbour reflects the frame back verbatim; the originator
/// times the round trip (RTT ÷ 2 → SNTT) - that timing is a later slice; this type
/// is the codec only: a thin <b>builder + recogniser</b> over
/// <see cref="NetRomPacket"/>, reusing <see cref="NetRomNetworkHeader"/> (15 B) and
/// <see cref="NetRomTransportHeader"/> (5 B) unchanged.
/// </summary>
/// <remarks>
/// <para>Layout (a <see cref="NetRomPacket"/>, so it rides PID 0xCF like every L3 datagram):</para>
/// <code>
///   [15] NetRomNetworkHeader    origin = us; destination = LITERAL "L3RTT-0"; TTL = default (25)
///   [ 5] NetRomTransportHeader  opcode nibble = 0x02 (ConnectAcknowledge's value, but disambiguated by the dest)
///   [ N] payload                space-padded ASCII capability text ($N then optional $IX, right-padded)
/// </code>
/// <para>
/// The opcode value <c>0x02</c> collides numerically with
/// <see cref="NetRomOpcode.ConnectAcknowledge"/>; an L3RTT frame is disambiguated
/// by its <b>destination = <c>L3RTT-0</c></b>, never by opcode alone - see
/// <see cref="IsL3Rtt"/>. A frame is recognised as <em>our own</em> reflection
/// (vs. a peer's probe we must reflect) when its origin equals our node callsign,
/// because reflection is byte-for-byte echo (origin stays the original prober).
/// </para>
/// <para>
/// Parsing is total: arbitrary, truncated, or adversarial bytes return
/// <c>false</c>/<c>null</c>, never throw. The capability text is parsed by a
/// width-independent <c>$</c>-token scan, so the emitted pad width
/// (<see cref="DefaultCapabilityTextWidth"/>) is a cosmetic choice, not something
/// the recogniser depends on - unknown <c>$</c>-tokens are ignored (forward-compat).
/// </para>
/// </remarks>
public sealed record Inp3L3RttFrame
{
    /// <summary>The literal base callsign every L3RTT datagram is destined to.</summary>
    public const string L3RttBase = "L3RTT";

    /// <summary>The canonical SSID of the L3RTT destination (always 0).</summary>
    public const byte L3RttSsid = 0;

    /// <summary>The transport opcode nibble that marks an L3RTT datagram (0x02).
    /// Numerically equal to <see cref="NetRomOpcode.ConnectAcknowledge"/>; the
    /// destination callsign - not this value - is what disambiguates an L3RTT
    /// frame from a Connect Acknowledge.</summary>
    public const byte L3RttOpcode = 0x02;

    /// <summary>The <c>$N</c> capability token - "I speak INP3". Its presence
    /// anywhere in the trimmed payload is how a node advertises INP3 capability;
    /// its absence means fall back to vanilla NODES.</summary>
    public const string CapabilityInp3 = "$N";

    /// <summary>The <c>$I</c> prefix of the IP-accept token (<c>$IX</c>, where X is
    /// the IP version digit, e.g. <c>$I4</c> for IPv4).</summary>
    public const string CapabilityIpPrefix = "$I";

    /// <summary>The emitted capability-text field width: <c>$N</c> (+ optional
    /// <c>$IX</c>) right-padded with ASCII spaces to this many octets. The INP3 PDF
    /// does not fix the width (AMBIGUITY-L3RTT-3) - the recogniser is
    /// width-independent, so this is purely an emit-side default to be calibrated
    /// against a live peer in a later slice.</summary>
    public const int DefaultCapabilityTextWidth = 8;

    /// <summary>The <see cref="NetRomPacket"/> that <em>is</em> this L3RTT frame.</summary>
    public required NetRomPacket Packet { get; init; }

    /// <summary>Whether the trimmed payload carried the <c>$N</c> token - i.e. the
    /// far end advertised INP3 capability.</summary>
    public required bool Inp3Capable { get; init; }

    /// <summary>The IP version the far end accepts (the digit from a <c>$IX</c>
    /// token, e.g. 4 for IPv4), or <c>null</c> if no <c>$IX</c> token was present.</summary>
    public required int? IpAccept { get; init; }

    /// <summary>The raw, untrimmed capability-text payload as it appeared on the
    /// wire (the bytes after the 20-octet L3+L4 header, decoded as ASCII).</summary>
    public required string CapabilityText { get; init; }

    /// <summary>
    /// Build an L3RTT probe datagram: a <see cref="NetRomPacket"/> to
    /// <c>L3RTT-0</c> with opcode nibble 0x02 and a space-padded capability text
    /// payload (<c>$N</c>, then an optional <c>$IX</c>, right-padded to
    /// <paramref name="capabilityTextWidth"/>). Strict, like every encoder here: it
    /// never emits a malformed frame.
    /// </summary>
    /// <param name="origin">The probing node's own callsign (the frame's L3 origin).</param>
    /// <param name="ipAccept">If set (e.g. 4), append a <c>$IX</c> token advertising
    /// the accepted IP version. Must be a single decimal digit 0-9.</param>
    /// <param name="timeToLive">The L3 TTL. Defaults to the node's normal initial
    /// TTL (<see cref="NetRomNetworkHeader.DefaultTimeToLive"/>); any value ≥ 1
    /// works for this single-hop neighbour probe.</param>
    /// <param name="capabilityTextWidth">The total octet width to right-pad the
    /// capability text to (default <see cref="DefaultCapabilityTextWidth"/>). If the
    /// tokens are longer than this, no padding is added (the tokens are never
    /// truncated).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ipAccept"/> is
    /// not a single decimal digit, or <paramref name="capabilityTextWidth"/> is
    /// negative.</exception>
    public static Inp3L3RttFrame Build(
        Callsign origin,
        int? ipAccept = null,
        byte timeToLive = NetRomNetworkHeader.DefaultTimeToLive,
        int capabilityTextWidth = DefaultCapabilityTextWidth)
    {
        if (ipAccept is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(ipAccept), ipAccept, "IP-accept version must be a single decimal digit 0–9");
        }
        if (capabilityTextWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilityTextWidth), capabilityTextWidth, "capability text width must be non-negative");
        }

        string text = CapabilityInp3 + (ipAccept is int v ? CapabilityIpPrefix + (char)('0' + v) : string.Empty);
        if (text.Length < capabilityTextWidth)
        {
            text = text.PadRight(capabilityTextWidth, ' ');
        }

        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader
            {
                Origin = origin,
                Destination = new Callsign(L3RttBase, L3RttSsid),
                TimeToLive = timeToLive,
            },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 0,
                CircuitId = 0,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = (NetRomOpcode)L3RttOpcode,
                Flags = NetRomTransportFlags.None,
            },
            // ASCII-only by construction ($N / $IX / spaces), so a per-char cast is exact.
            Payload = AsciiBytes(text),
        };

        return new Inp3L3RttFrame
        {
            Packet = packet,
            Inp3Capable = true,
            IpAccept = ipAccept,
            CapabilityText = text,
        };
    }

    /// <summary>Allocate and return the full L3RTT datagram bytes (the I-frame
    /// information field to send with PID 0xCF) - just <see cref="NetRomPacket.ToBytes"/>.</summary>
    public byte[] ToBytes() => Packet.ToBytes();

    /// <summary>
    /// Whether an already-parsed <see cref="NetRomPacket"/> is an L3RTT frame: its
    /// destination decodes to base <c>L3RTT</c> (SSID ignored for the match) and its
    /// transport opcode nibble is <c>0x02</c>. The destination test comes first -
    /// opcode 0x02 alone is also <see cref="NetRomOpcode.ConnectAcknowledge"/>.
    /// </summary>
    public static bool IsL3Rtt(NetRomPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return string.Equals(packet.Network.Destination.Base, L3RttBase, StringComparison.Ordinal)
            && ((byte)packet.Transport.Opcode & NetRomTransportHeader.OpcodeMask) == L3RttOpcode;
    }

    /// <summary>
    /// Try to recognise and decode an L3RTT frame from an interlink I-frame's
    /// information field. Returns <c>false</c> (never throws) if the bytes are not a
    /// well-formed <see cref="NetRomPacket"/>, or are a packet that is not L3RTT
    /// (wrong destination or opcode). On success the capability flags
    /// (<c>$N</c> → <see cref="Inp3Capable"/>, <c>$IX</c> → <see cref="IpAccept"/>)
    /// are extracted by a width-independent token scan of the trimmed payload.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, [NotNullWhen(true)] out Inp3L3RttFrame? frame)
    {
        frame = null;
        if (!NetRomPacket.TryParse(info, out var packet))
        {
            return false;
        }
        return TryFrom(packet, out frame);
    }

    /// <summary>
    /// Try to recognise an already-parsed <see cref="NetRomPacket"/> as an L3RTT
    /// frame and extract its capability flags. Returns <c>false</c> (never throws)
    /// if the packet is not L3RTT. Useful when the caller already decoded the
    /// datagram on a shared receive path and only wants to classify it.
    /// </summary>
    public static bool TryFrom(NetRomPacket packet, [NotNullWhen(true)] out Inp3L3RttFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(packet);
        frame = null;
        if (!IsL3Rtt(packet))
        {
            return false;
        }

        string text = AsciiString(packet.Payload.Span);
        ScanCapabilities(text, out bool inp3Capable, out int? ipAccept);

        frame = new Inp3L3RttFrame
        {
            Packet = packet,
            Inp3Capable = inp3Capable,
            IpAccept = ipAccept,
            CapabilityText = text,
        };
        return true;
    }

    /// <summary>
    /// Whether this frame is a reflection of <em>our own</em> probe (vs. a peer's
    /// probe we are expected to reflect): reflection is verbatim echo, so the origin
    /// of a returning frame is unchanged - it equals our node callsign.
    /// </summary>
    /// <param name="ourNodeCallsign">This node's own L3 callsign.</param>
    public bool IsReflectionOf(Callsign ourNodeCallsign) =>
        Packet.Network.Origin.Equals(ourNodeCallsign);

    /// <summary>
    /// Scan a capability text for the <c>$</c>-prefixed tokens. Width-independent
    /// and total - it never throws. <c>$N</c> sets <paramref name="inp3Capable"/>;
    /// a <c>$IX</c> with a single decimal digit X sets <paramref name="ipAccept"/>.
    /// Unknown <c>$</c>-tokens are ignored (forward-compat).
    /// </summary>
    private static void ScanCapabilities(string text, out bool inp3Capable, out int? ipAccept)
    {
        inp3Capable = false;
        ipAccept = null;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$')
            {
                continue;
            }

            // Token = '$' + the following non-'$', non-space run. We classify by the
            // first character after the '$'.
            int t = i + 1;
            if (t >= text.Length)
            {
                break;
            }

            char kind = text[t];
            if (kind == 'N')
            {
                inp3Capable = true;
            }
            else if (kind == 'I' && t + 1 < text.Length && char.IsAsciiDigit(text[t + 1]) && ipAccept is null)
            {
                ipAccept = text[t + 1] - '0';
            }
            // Any other '$'-token (unknown capability) is silently ignored.
        }
    }

    /// <summary>Encode an ASCII-only string to bytes (one byte per char, low 7
    /// bits). Used only for the capability text the builder constructs, which is
    /// guaranteed ASCII (<c>$N</c> / <c>$IX</c> / spaces).</summary>
    private static byte[] AsciiBytes(string text)
    {
        var buf = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            buf[i] = (byte)(text[i] & 0x7F);
        }
        return buf;
    }

    /// <summary>Decode wire bytes to a string for token scanning, one char per byte
    /// (Latin-1-ish). Non-ASCII / high-bit octets become the corresponding char but
    /// never affect the <c>$</c>-token scan - they are not <c>$</c>, <c>N</c>, or a
    /// digit. Total: any bytes decode without throwing.</summary>
    private static string AsciiString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        Span<char> chars = bytes.Length <= 256 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }
        return new string(chars);
    }
}
