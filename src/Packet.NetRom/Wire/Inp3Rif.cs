using System.Diagnostics.CodeAnalysis;
using System.Net;
using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// Per-call configuration for the INP3 RIF wire-parse path
/// (<see cref="Inp3Rif.TryParse(ReadOnlySpan{byte}, Inp3ParseOptions, out Inp3Rif?)"/>).
/// Mirrors <see cref="NetRomParseOptions"/> one-for-one: each tolerance of a
/// real-world peer's divergence from the canonical INP3 wire format is a named,
/// individually-toggleable flag with the same preset surface
/// (<c>Strict</c> / <c>Lenient</c> / <c>Bpq</c> / <c>Xrouter</c>).
/// </summary>
/// <remarks>
/// A RIF is the connected-mode analogue of a NODES broadcast - both lead with the
/// <c>0xFF</c> signature, both are a self-delimited sequence of fixed-prefix
/// entries - so the strict-by-default / lenient-on-promiscuous-ingest discipline
/// is identical. The two currently-known divergences are about <em>tolerance of
/// the entry list</em> (an empty list, a clipped trailing RIP), not the field
/// layout, exactly as for NODES.
/// </remarks>
public sealed record Inp3ParseOptions
{
    /// <summary>
    /// Accept a RIF body carrying <em>zero</em> RIPs (just the <c>0xFF</c>
    /// signature). The connected-mode analogue of
    /// <see cref="NetRomParseOptions.AllowEmptyDestinationList"/>.
    /// </summary>
    /// <remarks>
    /// A neighbour with nothing new to advertise can in principle send a
    /// signature-only RIF. Default <c>true</c> (lenient); a strict caller can
    /// treat a contentless RIF as malformed.
    /// </remarks>
    public bool AllowEmptyRipList { get; init; } = true;

    /// <summary>
    /// Accept a RIF whose final RIP is truncated (the body ends mid-RIP, or a
    /// TLV's claimed length runs off the end of the body): keep every whole RIP
    /// parsed so far and drop the clipped tail. The RIF analogue of
    /// <see cref="NetRomParseOptions.AllowTrailingPartialEntry"/>.
    /// </summary>
    /// <remarks>
    /// Driver: a noisy RF interlink can clip the tail of an I-frame. Dropping
    /// every learned route because the <em>last</em> RIP is short would be
    /// hostile; we keep the whole RIPs we did parse. Default <c>true</c>
    /// (lenient). Under <c>Strict</c> any leftover byte that does not complete a
    /// RIP rejects the whole frame.
    /// </remarks>
    public bool AllowTrailingPartialRip { get; init; } = true;

    /// <summary>
    /// Strict canonical INP3 - every accommodation disabled. A RIF is accepted
    /// only if every byte after the signature forms a whole RIP and there is at
    /// least one RIP.
    /// </summary>
    public static Inp3ParseOptions Strict { get; } = new()
    {
        AllowEmptyRipList = false,
        AllowTrailingPartialRip = false,
    };

    /// <summary>
    /// Accept-everything mode. All currently-known accommodations enabled. The
    /// parameterless <see cref="Inp3Rif.TryParse(ReadOnlySpan{byte}, out Inp3Rif?)"/>
    /// overload uses this - read-only promiscuous ingest wants to be forgiving.
    /// </summary>
    public static Inp3ParseOptions Lenient { get; } = new();

    /// <summary>
    /// BPQ / LinBPQ-flavoured leniency. Today the same instance as
    /// <see cref="Lenient"/>; kept named so a future BPQ-specific INP3 quirk lands
    /// here without churning call sites (the <see cref="NetRomParseOptions.Bpq"/>
    /// pattern).
    /// </summary>
    public static Inp3ParseOptions Bpq { get; } = Lenient;

    /// <summary>
    /// XRouter-flavoured leniency (Paula G8PZT). Today identical to
    /// <see cref="Lenient"/>; kept named for symmetry with
    /// <see cref="NetRomParseOptions.Xrouter"/>.
    /// </summary>
    public static Inp3ParseOptions Xrouter { get; } = Lenient;
}

/// <summary>
/// One INP3 type/length/value record carried inside a RIP (an
/// <see cref="Inp3Rip"/>). Encoded on the wire as <c>[type][len][value…]</c>
/// where <c>len</c> is a single octet equal to <see cref="Value"/>'s length
/// (0..255).
/// </summary>
/// <remarks>
/// <para>
/// Two types have defined meaning (INP3 spec / plan §4.2):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="AliasType"/> (<c>0x00</c>) - the destination's
///   ASCII alias / mnemonic. Decode with <see cref="AsAlias"/>.</description></item>
///   <item><description><see cref="IpType"/> (<c>0x01</c>) - an IP address;
///   <see cref="Value"/> length 4 = IPv4, 16 = IPv6. Decode with
///   <see cref="AsIpAddress"/>.</description></item>
/// </list>
/// <para>
/// <b>Unknown types are retained verbatim.</b> Any TLV whose type is neither of
/// the above is preserved exactly (type + value bytes) and re-emitted unchanged
/// when the RIP is forwarded - a RIP is never dropped for carrying a TLV we don't
/// understand (forward-compat, plan §4.2/§4.3). <see cref="IsKnown"/> reports
/// whether the type is one we interpret.
/// </para>
/// </remarks>
public sealed record Inp3Tlv
{
    /// <summary>TLV type: the destination's ASCII alias / mnemonic.</summary>
    public const byte AliasType = 0x00;

    /// <summary>TLV type: an IP address (value length 4 = IPv4, 16 = IPv6).</summary>
    public const byte IpType = 0x01;

    /// <summary>The TLV type octet.</summary>
    public required byte Type { get; init; }

    /// <summary>The TLV value bytes (0..255). Retained verbatim for unknown types.</summary>
    public required ReadOnlyMemory<byte> Value { get; init; }

    /// <summary>Octets this TLV occupies on the wire: <c>1 (type) + 1 (len) + Value.Length</c>.</summary>
    public int EncodedLength => 2 + Value.Length;

    /// <summary><c>true</c> if <see cref="Type"/> is a type this codec interprets
    /// (<see cref="AliasType"/> or <see cref="IpType"/>); <c>false</c> for an
    /// unknown type retained verbatim.</summary>
    public bool IsKnown => Type is AliasType or IpType;

    /// <summary>
    /// Build an alias TLV (<see cref="AliasType"/>) from a mnemonic string. The
    /// printable-ASCII characters of <paramref name="alias"/> are written verbatim
    /// (no padding, no shift) - the alias is variable-length inside a TLV, unlike
    /// the fixed 6-byte NODES alias field.
    /// </summary>
    public static Inp3Tlv Alias(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        var bytes = new byte[alias.Length];
        for (int i = 0; i < alias.Length; i++)
        {
            char c = alias[i];
            bytes[i] = (byte)(c is >= ' ' and <= '~' ? c : ' ');
        }
        return new Inp3Tlv { Type = AliasType, Value = bytes };
    }

    /// <summary>Build an IP TLV (<see cref="IpType"/>) from an address (4 or 16 value bytes).</summary>
    public static Inp3Tlv Ip(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new Inp3Tlv { Type = IpType, Value = address.GetAddressBytes() };
    }

    /// <summary>
    /// Decode <see cref="Value"/> as a trimmed ASCII alias string. Returns the
    /// printable characters only (a corrupted octet is dropped, never rendered as
    /// mojibake) with trailing spaces stripped - the same discipline as
    /// <see cref="NetRomCallsign.ReadAlias"/>. Meaningful only when
    /// <see cref="Type"/> is <see cref="AliasType"/>, but works on any value.
    /// </summary>
    public string AsAlias()
    {
        var span = Value.Span;
        Span<char> chars = span.Length <= 256 ? stackalloc char[span.Length] : new char[span.Length];
        int len = 0;
        for (int i = 0; i < span.Length; i++)
        {
            char c = (char)span[i];
            if (c is >= ' ' and <= '~')
            {
                chars[len++] = c;
            }
        }
        return new string(chars[..len]).TrimEnd();
    }

    /// <summary>
    /// Decode <see cref="Value"/> as an <see cref="IPAddress"/> when it is a
    /// 4-octet (IPv4) or 16-octet (IPv6) value; returns <c>null</c> for any other
    /// length (never throws). Meaningful only when <see cref="Type"/> is
    /// <see cref="IpType"/>, but works on any value of the right length.
    /// </summary>
    public IPAddress? AsIpAddress()
    {
        int n = Value.Length;
        if (n is not (4 or 16))
        {
            return null;
        }
        return new IPAddress(Value.Span);
    }

    /// <summary>Encode this TLV (<c>[type][len][value…]</c>) into
    /// <paramref name="destination"/> (≥ <see cref="EncodedLength"/> octets).</summary>
    /// <exception cref="InvalidOperationException">The value is longer than 255 octets
    /// (cannot be length-prefixed by a single byte) - a construction bug; we never
    /// emit a malformed TLV.</exception>
    public void Write(Span<byte> destination)
    {
        if (Value.Length > 255)
        {
            throw new InvalidOperationException($"TLV value must be 0..255 octets to length-prefix (got {Value.Length})");
        }
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"TLV needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }
        destination[0] = Type;
        destination[1] = (byte)Value.Length;
        Value.Span.CopyTo(destination[2..]);
    }
}

/// <summary>
/// One INP3 Routing Information Packet - a single routing entry inside a
/// <see cref="Inp3Rif"/>: "destination <see cref="Destination"/> is reachable in
/// <see cref="HopCount"/> hops with a measured target time of
/// <see cref="TargetTimeMs"/> ms," plus zero or more <see cref="Inp3Tlv"/>
/// records.
/// </summary>
/// <remarks>
/// <para>Wire layout (plan §4.2):</para>
/// <code>
///   [7] destination callsign  (AX.25 shifted form; reuse NetRomCallsign)
///   [1] hop count
///   [2] target time           MSB-first, 10 ms units (0..65535 → 0..655.35 s)
///   [*] TLV fields            zero or more [type][len][value] records (Inp3Tlv)
///   [1] 0x00                  EOP (end-of-packet) terminator
/// </code>
/// <para>
/// <b>The horizon.</b> A target time at or above <see cref="HorizonMs"/>
/// (<c>0xEA60</c> units = 600.000 s) marks the destination unreachable; a RIP at
/// the horizon is a route <em>withdrawal</em> (plan §5.3). This codec decodes the
/// value faithfully and exposes <see cref="IsHorizon"/> so the routing layer need
/// not re-derive the constant; the act of withdrawing the route is out of scope
/// here (INP3 slice I-3).
/// </para>
/// <para>
/// <b>Alias TLV vs EOP.</b> An alias TLV has type <c>0x00</c>, identical to the
/// EOP byte; they are distinguished positionally (spec §2.3, AMBIGUITY-RIF-2,
/// locked reading (a)): a <c>0x00</c> followed by a length byte and that many
/// value bytes still inside the body is an alias TLV; a <c>0x00</c> that cannot be
/// satisfied as a TLV is the EOP. <see cref="TryParse"/> implements exactly that.
/// </para>
/// </remarks>
public sealed record Inp3Rip
{
    /// <summary>Octets of fixed prefix before the TLV region: 7 callsign + 1 hop + 2 target-time.</summary>
    public const int PrefixLength = NetRomCallsign.ShiftedLength + 1 + 2;   // 10

    /// <summary>Target-time units (10 ms each) at the routing horizon - destination unreachable.</summary>
    public const int HorizonUnits = 0xEA60;   // 60000

    /// <summary>The routing horizon in milliseconds (600.000 s). A target time at or above this is a withdrawal.</summary>
    public const int HorizonMs = HorizonUnits * 10;   // 600_000

    /// <summary>The EOP (end-of-packet) terminator byte that closes a RIP on the wire.</summary>
    public const byte EndOfPacket = 0x00;

    /// <summary>The destination node this RIP advertises a route to.</summary>
    public required Callsign Destination { get; init; }

    /// <summary>Hop count to <see cref="Destination"/>.</summary>
    public required byte HopCount { get; init; }

    /// <summary>
    /// Target time to the destination, in milliseconds. On the wire this is a
    /// MSB-first 16-bit count of 10 ms units, so the stored value is always a
    /// multiple of 10 in the range 0..655350.
    /// </summary>
    public required int TargetTimeMs { get; init; }

    /// <summary>The TLV records carried by this RIP (alias / IP / unknown), in wire order. May be empty.</summary>
    public required IReadOnlyList<Inp3Tlv> Tlvs { get; init; }

    /// <summary><c>true</c> if <see cref="TargetTimeMs"/> is at or above the routing
    /// horizon (<see cref="HorizonMs"/>) - i.e. this RIP withdraws the route.</summary>
    public bool IsHorizon => TargetTimeMs >= HorizonMs;

    /// <summary>
    /// The first alias TLV's decoded string, or <c>null</c> if this RIP carries no
    /// alias TLV. Convenience over scanning <see cref="Tlvs"/>.
    /// </summary>
    public string? Alias
    {
        get
        {
            foreach (var tlv in Tlvs)
            {
                if (tlv.Type == Inp3Tlv.AliasType)
                {
                    return tlv.AsAlias();
                }
            }
            return null;
        }
    }

    /// <summary>Octets this RIP occupies on the wire: prefix + every TLV + the EOP byte.</summary>
    public int EncodedLength
    {
        get
        {
            int len = PrefixLength;
            foreach (var tlv in Tlvs)
            {
                len += tlv.EncodedLength;
            }
            return len + 1;   // EOP
        }
    }

    /// <summary>Encode this RIP into <paramref name="destination"/> (≥ <see cref="EncodedLength"/> octets).</summary>
    /// <exception cref="InvalidOperationException">A field is out of encodable range
    /// (target time, or a TLV value over 255 octets) - a construction bug; we never
    /// emit a malformed RIP.</exception>
    public void Write(Span<byte> destination)
    {
        int units = TargetTimeMs / 10;
        if (TargetTimeMs < 0 || units > 0xFFFF)
        {
            throw new InvalidOperationException($"target time must be 0..655350 ms to encode (got {TargetTimeMs})");
        }
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"RIP needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }

        int offset = 0;
        NetRomCallsign.WriteShifted(Destination, destination);
        offset += NetRomCallsign.ShiftedLength;

        destination[offset++] = HopCount;
        destination[offset++] = (byte)((units >> 8) & 0xFF);   // MSB first
        destination[offset++] = (byte)(units & 0xFF);

        foreach (var tlv in Tlvs)
        {
            tlv.Write(destination[offset..]);
            offset += tlv.EncodedLength;
        }

        destination[offset] = EndOfPacket;
    }

    /// <summary>Allocate and return this RIP's wire encoding.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[EncodedLength];
        Write(buf);
        return buf;
    }

    /// <summary>
    /// Try to decode one RIP from the front of <paramref name="source"/>, reporting
    /// how many octets it consumed (prefix + TLVs + EOP). Returns <c>false</c>
    /// (never throws) on any input that is too short or cannot be framed as a whole
    /// RIP - a truncated prefix, a callsign field that fails to decode, a TLV whose
    /// claimed length runs off the end of <paramref name="source"/>, or a RIP with
    /// no terminating EOP.
    /// </summary>
    /// <param name="source">The RIF body at this RIP's start (it may contain
    /// further RIPs after this one - only the consumed prefix is parsed here).</param>
    /// <param name="rip">The decoded RIP on success.</param>
    /// <param name="consumed">Octets consumed from <paramref name="source"/> on success; 0 on failure.</param>
    public static bool TryParse(ReadOnlySpan<byte> source, [NotNullWhen(true)] out Inp3Rip? rip, out int consumed)
    {
        rip = null;
        consumed = 0;

        if (source.Length < PrefixLength)
        {
            return false;
        }

        if (!NetRomCallsign.TryReadShifted(source, out var dest))
        {
            return false;
        }
        int offset = NetRomCallsign.ShiftedLength;

        byte hop = source[offset++];
        int units = (source[offset] << 8) | source[offset + 1];   // MSB first
        offset += 2;

        // Walk the TLV region. The EOP is a 0x00 that cannot be satisfied as a
        // TLV; an alias TLV (type 0x00) is a 0x00 followed by [len][value] that
        // still fits inside the body (AMBIGUITY-RIF-2, locked reading (a)).
        var tlvs = new List<Inp3Tlv>();
        while (true)
        {
            if (offset >= source.Length)
            {
                // Ran out of bytes before an EOP - the RIP is truncated.
                return false;
            }

            byte type = source[offset];

            if (type == EndOfPacket)
            {
                // Could be EOP, or the start of an alias TLV (type 0x00). It is a
                // TLV iff a length byte follows AND that many value bytes still fit
                // inside the source before its end. Otherwise it is the EOP.
                //
                // This "fits → alias, else → EOP" rule is forced by AMBIGUITY-RIF-2
                // (alias type == EOP == 0x00) and is exactly what lets a multi-RIP
                // RIF find its boundaries: a real EOP is followed by the next RIP's
                // shifted callsign, whose first octet (≈0x80+) frames as an alias
                // length that overruns the remaining body, so it reads as EOP. The
                // unavoidable consequence: a *truncated* trailing alias is
                // indistinguishable from EOP-plus-partial, so it degrades to a RIP
                // that keeps its route but drops the malformed alias (the residual
                // flagged for I-5 interop validation; alias *emission* stays gated
                // off until then). Never panics either way - the fuzz contract holds.
                bool isTlv =
                    offset + 1 < source.Length                                   // room for a len byte
                    && offset + 2 + source[offset + 1] <= source.Length;          // room for len value bytes

                if (!isTlv)
                {
                    // EOP - RIP ends here.
                    offset += 1;
                    break;
                }
            }
            else
            {
                // Non-zero type must have a length byte.
                if (offset + 1 >= source.Length)
                {
                    return false;
                }
            }

            int len = source[offset + 1];
            int valueStart = offset + 2;
            if (valueStart + len > source.Length)
            {
                // TLV claims more value bytes than remain - truncated.
                return false;
            }

            var value = source.Slice(valueStart, len).ToArray();
            tlvs.Add(new Inp3Tlv { Type = type, Value = value });
            offset = valueStart + len;
        }

        rip = new Inp3Rip
        {
            Destination = dest,
            HopCount = hop,
            TargetTimeMs = units * 10,
            Tlvs = tlvs,
        };
        consumed = offset;
        return true;
    }
}

/// <summary>
/// A parsed INP3 Routing Information Frame - the <c>0xFF</c>-signed body carried
/// in the information field of a connected-mode interlink I-frame (PID 0xCF). It
/// is the connected-mode analogue of a <see cref="NodesBroadcast"/>: a signature
/// byte followed by a self-delimited sequence of routing entries
/// (<see cref="Inp3Rip"/>), each closed by its own EOP.
/// </summary>
/// <remarks>
/// <para>Body layout (plan §4.2):</para>
/// <code>
///   [1]  0xFF  signature (gates the whole body; non-0xFF → not a RIF → null)
///   then 1..N RIPs, each self-delimited by its 0x00 EOP
/// </code>
/// <para>
/// This type models the I-frame's <em>info-field body</em>, exactly as
/// <see cref="NodesBroadcast"/> models a UI info field - not the surrounding AX.25
/// frame. RIF and NODES are <b>never confused</b> despite both leading with
/// <c>0xFF</c>: they arrive on different carriers (RIF on a connected I-frame,
/// NODES on a UI frame to dest <c>NODES</c>), so the caller selects the codec by
/// carrier - there is no content-sniffing (AMBIGUITY-RIF-1).
/// </para>
/// <para>
/// Parsing is read-only and total: arbitrary, truncated or adversarial bytes
/// never throw - they return <c>false</c>/<c>null</c>. Divergence tolerance
/// (empty RIP list, a clipped trailing RIP) is gated by
/// <see cref="Inp3ParseOptions"/> - strict by default, lenient on the
/// parameterless overload used for promiscuous ingest.
/// </para>
/// </remarks>
public sealed record Inp3Rif
{
    /// <summary>The INP3 RIF signature byte that opens the info-field body (shared with NODES; disambiguated by carrier).</summary>
    public const byte Signature = 0xFF;

    /// <summary>The RIPs carried in this RIF, in wire order. May be empty (lenient) but never <c>null</c>.</summary>
    public required IReadOnlyList<Inp3Rip> Rips { get; init; }

    /// <summary>Octets this RIF occupies on the wire: the signature byte + every RIP.</summary>
    public int EncodedLength
    {
        get
        {
            int len = 1;   // signature
            foreach (var rip in Rips)
            {
                len += rip.EncodedLength;
            }
            return len;
        }
    }

    /// <summary>Encode this RIF into <paramref name="destination"/> (≥ <see cref="EncodedLength"/> octets).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"RIF needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }

        destination[0] = Signature;
        int offset = 1;
        foreach (var rip in Rips)
        {
            rip.Write(destination[offset..]);
            offset += rip.EncodedLength;
        }
    }

    /// <summary>Allocate and return this RIF's wire encoding (the I-frame info field).</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[EncodedLength];
        Write(buf);
        return buf;
    }

    /// <summary>
    /// Try to parse a RIF body from an interlink I-frame's information field, using
    /// lenient options (the promiscuous-ingest default - see
    /// <see cref="Inp3ParseOptions.Lenient"/>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, [NotNullWhen(true)] out Inp3Rif? rif)
        => TryParse(info, Inp3ParseOptions.Lenient, out rif);

    /// <summary>
    /// Try to parse a RIF body, applying <paramref name="options"/> for the
    /// strict-vs-lenient divergence choices. Returns <c>false</c> (never throws) on
    /// any malformed input - empty, wrong signature, truncated, or adversarial.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, Inp3ParseOptions options, [NotNullWhen(true)] out Inp3Rif? rif)
    {
        ArgumentNullException.ThrowIfNull(options);
        rif = null;

        // Need at least the signature byte.
        if (info.Length < 1)
        {
            return false;
        }

        // Signature gates the whole body - a non-0xFF first octet means this is
        // not a RIF (the same "wrong signature → ignore" heuristic NODES uses).
        if (info[0] != Signature)
        {
            return false;
        }

        var rips = new List<Inp3Rip>();
        int offset = 1;
        while (offset < info.Length)
        {
            if (!Inp3Rip.TryParse(info[offset..], out var rip, out int consumed))
            {
                // A RIP that doesn't frame cleanly (truncated, bad callsign, a TLV
                // running off the end). Under lenient, keep the whole RIPs already
                // parsed and drop the clipped tail (RF-clip tolerance). Under
                // strict, any leftover that doesn't complete a RIP rejects the
                // whole frame.
                if (!options.AllowTrailingPartialRip)
                {
                    return false;
                }
                break;
            }

            rips.Add(rip);
            // Defensive: a zero-consumed RIP would loop forever. TryParse always
            // consumes at least the prefix + EOP on success, but guard anyway.
            if (consumed <= 0)
            {
                break;
            }
            offset += consumed;
        }

        if (rips.Count == 0 && !options.AllowEmptyRipList)
        {
            return false;
        }

        rif = new Inp3Rif { Rips = rips };
        return true;
    }
}
