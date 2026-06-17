using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Packet.Ax25.Xid;

/// <summary>
/// Codec for the AX.25 v2.2 XID (Exchange Identification) <em>information
/// field</em> — the TLV parameter-negotiation payload carried inside an XID
/// U-frame (§4.3.3.7 "Exchange Identification (XID) Frame", parameter table
/// Figure 4.5, worked example Figure 4.6). This is the wire format the MDL
/// (Management Data-Link, App. C5) exchange negotiates over; the codec is
/// transport-agnostic — the resulting bytes go into <see cref="Ax25Frame.Xid"/>'s
/// <c>info</c> argument, and bytes pulled off a received XID frame's
/// <see cref="Ax25Frame.Info"/> come back here.
/// </summary>
/// <remarks>
/// <para>Layout (§4.3.3.7 ¶1017–1024):</para>
/// <code>
///   FI (1)  Format Identifier  = 0x82 (general-purpose XID information)
///   GI (1)  Group Identifier   = 0x80 (parameter-negotiation identifier)
///   GL (2)  Group Length       = length of the parameter field that follows,
///                                big-endian, NOT counting FI/GI/GL themselves
///   parameter field: a run of PI/PL/PV triples in ascending PI order
///     PI (1)  Parameter Identifier
///     PL (1)  Parameter Length  = length of PV in octets (excludes PI and PL)
///     PV (PL) Parameter Value
/// </code>
/// <para>
/// A <c>PL</c> of zero means the PV is absent and the parameter takes its
/// default; an omitted PI/PL/PV triple means "use the currently-negotiated
/// value"; an unrecognised PI is ignored (§4.3.3.7 ¶1024). We model "absent"
/// as a <c>null</c> field on <see cref="XidParameters"/>, distinct from a
/// present-but-default value.
/// </para>
/// <para>
/// <b>Strict by construction.</b> <see cref="Encode"/> emits exactly the
/// fields set on <see cref="XidParameters"/>, in ascending-PI order, with the
/// fixed/reserved bits of the bit-fields (Classes of Procedures, HDLC Optional
/// Functions) forced to their spec-mandated constants. It never produces a
/// malformed field. Parser leniency for real-world peers lives behind named
/// flags on <see cref="XidParseOptions"/>; the default is spec-strict.
/// </para>
/// </remarks>
public static class XidInfoField
{
    /// <summary>Format Identifier for general-purpose XID information (§4.3.3.7 ¶1019).</summary>
    public const byte FormatIdentifier = 0x82;

    /// <summary>Group Identifier for the parameter-negotiation group (§4.3.3.7 ¶1020).</summary>
    public const byte GroupIdentifier = 0x80;

    /// <summary>Minimum encoded length: FI + GI + GL with an empty parameter field.</summary>
    public const int HeaderLength = 4;

    // ─── Parameter identifiers (Figure 4.5 "PI" column) ──────────────────

    /// <summary>PI=2 — Classes of Procedures (half/full duplex, ABM). Figure 4.5.</summary>
    public const byte PiClassesOfProcedures = 0x02;

    /// <summary>PI=3 — HDLC Optional Functions (REJ/SREJ, modulo, segmenter, …). Figure 4.5.</summary>
    public const byte PiHdlcOptionalFunctions = 0x03;

    /// <summary>PI=6 — I Field Length Receive, in <b>bits</b> (N1×8). Figure 4.5.</summary>
    public const byte PiIFieldLengthRx = 0x06;

    /// <summary>PI=8 — Window Size Receive (k frames). Figure 4.5.</summary>
    public const byte PiWindowSizeRx = 0x08;

    /// <summary>PI=9 — Acknowledge Timer T1, in milliseconds. Figure 4.5.</summary>
    public const byte PiAckTimer = 0x09;

    /// <summary>
    /// PI=10 (0x0A) — Retries (N2). Figure 4.6 labels this "Retries (N2)".
    /// (The §4.3.3.7 / §6.3.2 prose miscalls the retry count "N1"; the table
    /// name "Retries" and Fig 4.6 are authoritative — this is N2, the link's
    /// retry limit, not N1, the I-field length.)
    /// </summary>
    public const byte PiRetries = 0x0A;

    // ── PI=5 (I Field Length Transmit) and PI=7 (Window Size Transmit) are
    //    defined in Figure 4.5 but flagged "*" — ISO 8885, not needed to
    //    negotiate this version of AX.25. We parse them through (so they
    //    round-trip on a received frame) but do not synthesise them.
    /// <summary>PI=5 — I Field Length Transmit (bits). ISO 8885; not negotiated by AX.25.</summary>
    public const byte PiIFieldLengthTx = 0x05;

    /// <summary>PI=7 — Window Size Transmit. ISO 8885; not negotiated by AX.25.</summary>
    public const byte PiWindowSizeTx = 0x07;

    /// <summary>
    /// Encode a set of negotiation parameters into the XID information-field
    /// bytes (FI + GI + GL + ordered PI/PL/PV). Only the non-<c>null</c>
    /// fields of <paramref name="parameters"/> are emitted, in ascending PI
    /// order per §4.3.3.7 ¶1024. The result is suitable as the <c>info</c>
    /// argument to <see cref="Ax25Frame.Xid"/>.
    /// </summary>
    public static byte[] Encode(XidParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Build the parameter field first so we know the group length.
        var pf = new List<byte>(32);

        if (parameters.ClassesOfProcedures is { } cop)
        {
            // PI=2, PL=2, PV = 16-bit field, big-endian (octet0 first). The
            // field is defined LSB-first within each octet (verified against
            // Fig 4.6: PV 0x22 0x00 ⇒ bit0 ABM + bit5 half-duplex).
            WriteParameter(pf, PiClassesOfProcedures, cop.ToOctets());
        }

        if (parameters.HdlcOptionalFunctions is { } hof)
        {
            // PI=3, PL=3, PV = 24-bit field transmitted most-significant octet
            // first (AX.25 v2.2 Fig 4.6 + direwolf + LinBPQ; see ToOctets). The
            // historical LSB-first layout was an interop bug — BPQ silently drops
            // it and never negotiates SREJ (proven on the wire, SrejXidViaNetsim).
            WriteParameter(pf, PiHdlcOptionalFunctions, hof.ToOctets());
        }

        if (parameters.IFieldLengthRxBits is { } n1bits)
        {
            WriteParameter(pf, PiIFieldLengthRx, EncodeUnsigned(n1bits));
        }

        if (parameters.WindowSizeRx is { } k)
        {
            // Window size is a single-octet count 0..127 (Figure 4.5: bits
            // 0–6 = 0..127). One octet is the canonical encoding (Fig 4.6
            // uses PL=1), which is what we emit.
            WriteParameter(pf, PiWindowSizeRx, new[] { (byte)(k & 0x7F) });
        }

        if (parameters.AckTimerMillis is { } t1)
        {
            WriteParameter(pf, PiAckTimer, EncodeUnsigned(t1));
        }

        if (parameters.Retries is { } n2)
        {
            WriteParameter(pf, PiRetries, EncodeUnsigned(n2));
        }

        var result = new byte[HeaderLength + pf.Count];
        result[0] = FormatIdentifier;
        result[1] = GroupIdentifier;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), checked((ushort)pf.Count));
        pf.CopyTo(result, HeaderLength);
        return result;
    }

    /// <summary>
    /// Parse an XID information field (the bytes from an XID frame's
    /// <see cref="Ax25Frame.Info"/>) into a <see cref="XidParameters"/>.
    /// Returns <c>false</c> (without throwing) on a malformed buffer — a bad
    /// FI/GI, a truncated header, a Group Length that overruns the buffer, or
    /// (under the strict default) a PI/PL whose PV runs past the parameter
    /// field. Unrecognised PIs are skipped per §4.3.3.7 ¶1024.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, [NotNullWhen(true)] out XidParameters? parameters)
        => TryParse(info, XidParseOptions.Strict, out parameters);

    /// <inheritdoc cref="TryParse(System.ReadOnlySpan{byte},out Packet.Ax25.Xid.XidParameters?)"/>
    public static bool TryParse(ReadOnlySpan<byte> info, XidParseOptions options,
        [NotNullWhen(true)] out XidParameters? parameters)
    {
        parameters = null;
        ArgumentNullException.ThrowIfNull(options);

        if (info.Length < HeaderLength) return false;
        if (info[0] != FormatIdentifier) return false;
        if (info[1] != GroupIdentifier) return false;

        int groupLength = BinaryPrimitives.ReadUInt16BigEndian(info.Slice(2, 2));
        int available = info.Length - HeaderLength;

        if (groupLength > available)
        {
            // GL claims more parameter bytes than the buffer holds.
            if (!options.AllowGroupLengthOverrun) return false;
            groupLength = available; // lenient: clamp to what we actually have
        }

        var pf = info.Slice(HeaderLength, groupLength);

        byte? classesRaw0 = null, classesRaw1 = null;
        ClassesOfProcedures? classes = null;
        HdlcOptionalFunctions? hdlc = null;
        int? n1Bits = null, ackTimer = null;
        int? window = null, retries = null;

        int pos = 0;
        while (pos < pf.Length)
        {
            byte pi = pf[pos++];
            if (pos >= pf.Length)
            {
                // A trailing PI with no room for a PL octet.
                if (!options.AllowTruncatedParameter) return false;
                break;
            }

            int pl = pf[pos++];
            if (pos + pl > pf.Length)
            {
                // PV runs past the end of the parameter field.
                if (!options.AllowTruncatedParameter) return false;
                pl = pf.Length - pos; // lenient: take what remains
            }

            var pv = pf.Slice(pos, pl);
            pos += pl;

            switch (pi)
            {
                case PiClassesOfProcedures:
                    // PL=0 ⇒ absent ⇒ leave as null (default applies elsewhere).
                    if (pl >= 1) classesRaw0 = pv[0];
                    if (pl >= 2) classesRaw1 = pv[1];
                    if (pl >= 1) classes = ClassesOfProcedures.FromOctets(classesRaw0!.Value, classesRaw1 ?? 0);
                    break;

                case PiHdlcOptionalFunctions:
                    if (pl >= 1) hdlc = HdlcOptionalFunctions.FromOctets(pv);
                    break;

                case PiIFieldLengthRx:
                    if (pl >= 1) n1Bits = DecodeUnsigned(pv);
                    break;

                case PiWindowSizeRx:
                    if (pl >= 1) window = pv[0] & 0x7F;
                    break;

                case PiAckTimer:
                    if (pl >= 1) ackTimer = DecodeUnsigned(pv);
                    break;

                case PiRetries:
                    if (pl >= 1) retries = DecodeUnsigned(pv);
                    break;

                // PI=5 / PI=7 (Tx variants) and any unrecognised PI are
                // ignored per §4.3.3.7 ¶1024.
                default:
                    break;
            }
        }

        parameters = new XidParameters
        {
            ClassesOfProcedures = classes,
            HdlcOptionalFunctions = hdlc,
            IFieldLengthRxBits = n1Bits,
            WindowSizeRx = window,
            AckTimerMillis = ackTimer,
            Retries = retries,
        };
        return true;
    }

    private static void WriteParameter(List<byte> sink, byte pi, ReadOnlySpan<byte> pv)
    {
        sink.Add(pi);
        sink.Add(checked((byte)pv.Length));
        foreach (byte b in pv) sink.Add(b);
    }

    /// <summary>
    /// Encode a non-negative integer as the minimum number of big-endian
    /// octets (most-significant first), with at least one octet. Type-B
    /// numeric fields (Figure 4.5: N1, T1, N2) are variable-length big-endian
    /// numbers; we emit the shortest faithful representation, matching the
    /// 1- or 2-octet widths in the Fig 4.6 worked example.
    /// </summary>
    internal static byte[] EncodeUnsigned(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "XID numeric parameters are non-negative");
        if (value == 0) return new byte[] { 0 };

        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tmp, (uint)value);
        int firstNonZero = 0;
        while (firstNonZero < 3 && tmp[firstNonZero] == 0) firstNonZero++;
        return tmp[firstNonZero..].ToArray();
    }

    /// <summary>Decode a big-endian Type-B numeric field of arbitrary octet width.</summary>
    internal static int DecodeUnsigned(ReadOnlySpan<byte> pv)
    {
        long acc = 0;
        foreach (byte b in pv)
        {
            acc = (acc << 8) | b;
            if (acc > int.MaxValue) acc = int.MaxValue; // saturate; pathological widths
        }
        return (int)acc;
    }
}
