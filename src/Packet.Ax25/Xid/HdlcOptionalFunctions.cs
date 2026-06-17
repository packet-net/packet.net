namespace Packet.Ax25.Xid;

/// <summary>The reject scheme negotiated by the HDLC Optional Functions field.</summary>
public enum RejectMode
{
    /// <summary>Implicit reject (REJ) — bit 1 set, bit 2 reset (§6.3.2 ¶1086).</summary>
    ImplicitReject,

    /// <summary>Selective reject (SREJ) — bit 1 reset, bit 2 set (§6.3.2 ¶1087).</summary>
    SelectiveReject,
}

/// <summary>
/// The XID "HDLC Optional Functions" parameter (PI=3, PL=3 — a 24-bit field),
/// per AX.25 v2.2 §4.3.3.7 (Figure 4.5) and the negotiation rules in §6.3.2
/// ¶1082–1090. For AX.25 this carries the two genuinely-negotiated selections
/// — the reject scheme (REJ vs SREJ) and the modulo (8 vs 128) — plus the
/// segmenter/reassembler bit; every other bit is fixed.
/// </summary>
/// <remarks>
/// <para>Bit layout (logical bits 0–23; bit 0 = the low bit of the low-order octet.
/// Wire order is most-significant octet first per §3.8 — see "Octet order" below):</para>
/// <list type="bullet">
/// <item>bit 0 — Reserved: 0.</item>
/// <item>bit 1 — REJ command/response (set ⇒ implicit reject selected).</item>
/// <item>bit 2 — SREJ command/response (set ⇒ selective reject selected).</item>
/// <item>bits 3–6, 8, 9, 12, 14, 16, 18–20 — fixed 0 (ISO-8885 functions AX.25 doesn't use).</item>
/// <item>bit 7 — Extended address: always 1.</item>
/// <item>bit 10 — Modulo 8 (set ⇒ modulo-8 selected).</item>
/// <item>bit 11 — Modulo 128 (set ⇒ modulo-128 selected).</item>
/// <item>bit 13 — TEST command/response: always 1.</item>
/// <item>bit 15 — 16-bit FCS: always 1.</item>
/// <item>bit 17 — Synchronous transmit: always 1.</item>
/// <item>bit 21 — SREJ multiframe.</item>
/// <item>bit 22 — Segmenter/reassembler.</item>
/// <item>bit 23 — Reserved: 0.</item>
/// </list>
/// <para>
/// Exactly one of bit 1 / bit 2 must be set (clearing both is illegal per
/// ¶1088); exactly one of bit 10 / bit 11 must be set. <see cref="ToOctets"/>
/// enforces both invariants and forces the always-1 bits (7, 13, 15, 17).
/// </para>
/// <para>
/// <b>Octet order.</b> The bit positions above are the logical field; the 3-octet
/// PV goes on the wire <b>most-significant octet first</b> per AX.25 v2.2 §3.8
/// ("high-order octet first"). See <see cref="ToOctets"/>. Figure 4.6 prints the
/// PV least-significant-octet first (<c>82 A8 22</c>; §3.8-correct it is
/// <c>22 A8 82</c>) — a figure-rendering error that contradicts §3.8; we follow
/// §3.8, matching direwolf and LinBPQ on the wire.
/// </para>
/// <para>
/// <b>Spec worked-example note.</b> Figure 4.6 shows PV 0x82 0xA8 0x22 (LSB-octet
/// first), whose
/// caption says "SREJ/REJ … Modulo 128 …". Decoded against this (prose-correct)
/// bit map, those bytes are REJ (bit 1) + Modulo 128 (bit 11) + the always-1
/// bits + SREJ-multiframe (bit 21) — i.e. the figure selects REJ, not SREJ,
/// contradicting its own caption. We encode/decode per the normative §6.3.2
/// prose, not the figure caption; see the codec's round-trip tests.
/// </para>
/// </remarks>
public sealed record HdlcOptionalFunctions
{
    private const int BitRej = 1;
    private const int BitSrej = 2;
    private const int BitExtendedAddress = 7;   // always 1
    private const int BitModulo8 = 10;
    private const int BitModulo128 = 11;
    private const int BitTest = 13;             // always 1
    private const int BitFcs16 = 15;            // always 1
    private const int BitSyncTx = 17;           // always 1
    private const int BitSrejMultiframe = 21;
    private const int BitSegmenter = 22;

    /// <summary>The reject scheme — implicit (REJ) or selective (SREJ).</summary>
    public RejectMode Reject { get; init; } = RejectMode.SelectiveReject;

    /// <summary>True ⇒ modulo-128 selected; false ⇒ modulo-8.</summary>
    public bool Modulo128 { get; init; } = true;

    /// <summary>True ⇒ the SREJ-multiframe option (bit 21) is asserted.</summary>
    public bool SrejMultiframe { get; init; }

    /// <summary>True ⇒ the segmenter/reassembler option (bit 22) is asserted.</summary>
    public bool SegmenterReassembler { get; init; }

    /// <summary>
    /// The AX.25 v2.2 default per §6.3.2 ¶1090: selective reject, modulo 128,
    /// no segmenter (the figure's absent-field default).
    /// </summary>
    public static HdlcOptionalFunctions Default { get; } = new()
    {
        Reject = RejectMode.SelectiveReject,
        Modulo128 = true,
        SrejMultiframe = false,
        SegmenterReassembler = false,
    };

    /// <summary>
    /// Encode to the 3-octet PV. Forces the always-1 bits (extended address, TEST,
    /// 16-bit FCS, synchronous Tx) and sets exactly one reject bit and exactly one
    /// modulo bit. Octet order is governed by <paramref name="lsbOctetFirst"/>
    /// (default = spec-correct most-significant octet first).
    /// </summary>
    /// <param name="lsbOctetFirst">
    /// When <c>false</c> (the default, spec-correct) the 3-octet value is
    /// transmitted <b>most-significant octet first</b> (big-endian), as mandated by
    /// AX.25 v2.2 <b>§3.8</b> ("Order of Octet and Bit Transmission"): multiple-octet
    /// fields are sent "high-order octet first, the next lower octet next". This is
    /// also the order every real peer uses (Dire Wolf <c>xid.c</c> writes
    /// <c>(x&gt;&gt;16),(x&gt;&gt;8),x</c>; LinBPQ <c>L2Code.c</c> writes
    /// <c>xidval&gt;&gt;16</c> first and parses <c>value = (value&lt;&lt;8) + *p++</c>).
    /// NOTE: Figure 4.6's printed PV <c>82 A8 22</c> is the <i>least</i>-significant
    /// octet first (it only decodes to the captioned selection read LSB-first) and so
    /// <b>contradicts §3.8</b> — a figure-rendering error (the same worked example also
    /// carries the documented Classes-of-Procedures ABM off-by-one). The §3.8-correct
    /// serialisation of that selection is <c>22 A8 82</c>. When <c>true</c>, this
    /// reproduces the repo's historical (and §3.8-violating) least-significant-octet-first
    /// layout — kept only for regression study and never put on the wire by the production
    /// connect path. See <c>docs/strict-vs-pragmatic-audit.md</c> (HDLC-Optional-Functions
    /// octet order) and the <c>SrejXidViaNetsim</c> interop proof: BPQ accepts the
    /// MSB-first PV and negotiates SREJ, and silently drops the LSB-first one.
    /// </param>
    public byte[] ToOctets(bool lsbOctetFirst = false)
    {
        int field = (1 << BitExtendedAddress)
                  | (1 << BitTest)
                  | (1 << BitFcs16)
                  | (1 << BitSyncTx);

        field |= Reject == RejectMode.ImplicitReject ? (1 << BitRej) : (1 << BitSrej);
        field |= Modulo128 ? (1 << BitModulo128) : (1 << BitModulo8);

        if (SrejMultiframe) field |= 1 << BitSrejMultiframe;
        if (SegmenterReassembler) field |= 1 << BitSegmenter;

        return lsbOctetFirst
            ? new[]   // legacy (incorrect) least-significant octet first
            {
                (byte)(field & 0xFF),
                (byte)((field >> 8) & 0xFF),
                (byte)((field >> 16) & 0xFF),
            }
            : new[]   // spec-correct most-significant octet first (§3.8; direwolf / BPQ)
            {
                (byte)((field >> 16) & 0xFF),
                (byte)((field >> 8) & 0xFF),
                (byte)(field & 0xFF),
            };
    }

    /// <summary>
    /// Decode from the (up to) 3-octet PV. Reads the reject scheme from
    /// bits 1/2 and the modulo from bits 10/11; if a selection is ambiguous or
    /// absent we fall back to the spec defaults (SREJ, modulo 128). The fixed
    /// always-1/always-0 bits are not validated on receive — only the
    /// negotiable selections are meaningful.
    /// </summary>
    /// <param name="lsbOctetFirst">
    /// The on-the-wire octet order of <paramref name="pv"/>; must match the order
    /// the peer used. <c>false</c> (default, spec-correct) reads the first octet as
    /// the high byte (§3.8 "high-order octet first"; direwolf / BPQ); <c>true</c> reads
    /// the legacy least-significant-octet-first layout. See <see cref="ToOctets(bool)"/>.
    /// </param>
    public static HdlcOptionalFunctions FromOctets(ReadOnlySpan<byte> pv, bool lsbOctetFirst = false)
    {
        int field = 0;
        int n = Math.Min(pv.Length, 3);
        for (int i = 0; i < n; i++)
        {
            int shift = lsbOctetFirst ? 8 * i : 8 * (n - 1 - i);
            field |= pv[i] << shift;
        }

        bool rej = (field & (1 << BitRej)) != 0;
        bool srej = (field & (1 << BitSrej)) != 0;
        // SREJ takes precedence if both are (illegally) set; default SREJ if neither.
        RejectMode reject = srej ? RejectMode.SelectiveReject
                          : rej ? RejectMode.ImplicitReject
                          : RejectMode.SelectiveReject;

        bool mod128 = (field & (1 << BitModulo128)) != 0;
        bool mod8 = (field & (1 << BitModulo8)) != 0;
        // Default modulo 128 if neither (the spec default); mod-8 only if it
        // alone is asserted.
        bool isMod128 = !(mod8 && !mod128);

        return new HdlcOptionalFunctions
        {
            Reject = reject,
            Modulo128 = isMod128,
            SrejMultiframe = (field & (1 << BitSrejMultiframe)) != 0,
            SegmenterReassembler = (field & (1 << BitSegmenter)) != 0,
        };
    }
}
