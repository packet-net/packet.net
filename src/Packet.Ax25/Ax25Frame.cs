using System.Diagnostics.CodeAnalysis;
using Packet.Core;

namespace Packet.Ax25;

/// <summary>
/// One AX.25 frame, in the form delivered by KISS (no opening / closing flags,
/// no FCS — the TNC handles HDLC framing and the frame check sequence).
/// </summary>
/// <remarks>
/// <para>Layout per AX.25 v2.2 §3:</para>
/// <code>
///   [destination 7B] [source 7B] [digipeaters 0..8 × 7B] [control 1B]
///   [pid 0..1B] [info 0..N B]
/// </code>
/// <para>
/// The <c>pid</c> octet is present only on I frames and UI frames. The
/// <c>info</c> field is present on I and UI frames; some other frame types
/// (FRMR, XID, TEST) also carry information but we don't model their
/// internals yet — Phase 1 focuses on UI.
/// </para>
/// </remarks>
public sealed partial class Ax25Frame
{
    /// <summary>Control byte for a UI frame with the P bit cleared.</summary>
    public const byte ControlUi = 0x03;

    /// <summary>Control byte for a UI frame with the P/F bit set.</summary>
    public const byte ControlUiPollFinal = 0x13;

    /// <summary>PID 0xF0 — no Layer 3 protocol implemented (per §3.4).</summary>
    public const byte PidNoLayer3 = 0xF0;

    /// <summary>PID 0xCF — NET/ROM.</summary>
    public const byte PidNetRom = 0xCF;

    /// <summary>PID 0x08 — segmented frame (per §6.6).</summary>
    public const byte PidSegmented = 0x08;

    /// <summary>Maximum number of Layer-2 repeater (digipeater) entries (§3.12.5).</summary>
    public const int MaxDigipeaters = 8;

    /// <summary>Destination address slot.</summary>
    public Ax25Address Destination { get; }

    /// <summary>Source address slot.</summary>
    public Ax25Address Source { get; }

    /// <summary>Zero or more digipeater (repeater) slots, in path order.</summary>
    public IReadOnlyList<Ax25Address> Digipeaters { get; }

    /// <summary>
    /// Raw control byte — the first (low-order) control octet (see
    /// <see cref="ControlUi"/>). For an extended (modulo-128) I or S frame
    /// this is the first of two octets; <see cref="ControlExtension"/> holds
    /// the second. The frame-type discriminator bits (bit 0; bits 1-0) live
    /// here in both modes, so classification reads this octet regardless of
    /// modulo.
    /// </summary>
    public byte Control { get; }

    /// <summary>
    /// Second control octet of an extended (modulo-128) I or S frame, carrying
    /// the 7-bit N(R) (bits 7-1) and the P/F bit (bit 0) per AX.25 v2.2
    /// Fig 4.1b. <c>null</c> for U frames and for every modulo-8 frame — those
    /// have a 1-octet control field. Its presence is what makes a frame
    /// "extended" (see <see cref="IsExtendedControl"/>).
    /// </summary>
    public byte? ControlExtension { get; }

    /// <summary>
    /// True when this frame carries the 2-octet (modulo-128) control field.
    /// Equivalent to <see cref="ControlExtension"/> being present.
    /// </summary>
    public bool IsExtendedControl => ControlExtension.HasValue;

    /// <summary>PID byte, present on I and UI frames only.</summary>
    public byte? Pid { get; }

    /// <summary>Information field. Empty memory if absent.</summary>
    public ReadOnlyMemory<byte> Info { get; }

    /// <summary>
    /// True if this frame's control byte identifies it as a UI frame
    /// (ignoring the P/F bit).
    /// </summary>
    public bool IsUi => (Control & 0xEF) == ControlUi;

    /// <summary>
    /// True if the P/F bit is set. In a modulo-8 frame (and any U frame, which
    /// is 1 octet in both modes) the P/F bit is bit 4 of the control octet; in
    /// an extended (modulo-128) I or S frame it migrates to bit 0 of the second
    /// control octet (Fig 4.1b).
    /// </summary>
    public bool PollFinal => ControlExtension is byte ext
        ? (ext & 0x01) != 0
        : (Control & 0x10) != 0;

    /// <summary>
    /// N(R), the receive sequence number carried by I and S frames. 3-bit in
    /// modulo-8 (control bits 7-5); 7-bit in extended modulo-128 (second
    /// control octet bits 7-1) per Fig 4.1b. Meaningless on U frames — the
    /// caller must know the frame type before relying on this.
    /// </summary>
    public byte Nr => ControlExtension is byte ext
        ? (byte)((ext >> 1) & 0x7F)
        : (byte)((Control >> 5) & 0x07);

    /// <summary>
    /// N(S), the send sequence number carried by I frames. 3-bit in modulo-8
    /// (control bits 3-1); 7-bit in extended modulo-128 (first control octet
    /// bits 7-1) per Fig 4.1b. Meaningful only for I frames — on an S frame the
    /// same bits encode the supervisory type, so the caller must check the
    /// frame type first.
    /// </summary>
    public byte Ns => IsExtendedControl
        ? (byte)((Control >> 1) & 0x7F)
        : (byte)((Control >> 1) & 0x07);

    /// <summary>
    /// True if the address-field C-bits encode a command per §6.1.2
    /// (destination C=1, source C=0).
    /// </summary>
    public bool IsCommand => Destination.CrhBit && !Source.CrhBit;

    /// <summary>
    /// True if the address-field C-bits encode a response per §6.1.2
    /// (destination C=0, source C=1).
    /// </summary>
    public bool IsResponse => !Destination.CrhBit && Source.CrhBit;

    private Ax25Frame(
        Ax25Address destination,
        Ax25Address source,
        IReadOnlyList<Ax25Address> digipeaters,
        byte control,
        byte? controlExtension,
        byte? pid,
        ReadOnlyMemory<byte> info)
    {
        Destination = destination;
        Source = source;
        Digipeaters = digipeaters;
        Control = control;
        ControlExtension = controlExtension;
        Pid = pid;
        Info = info;
    }

    /// <summary>
    /// Construct a UI frame. Sets address-field C-bits per §6.1.2 according
    /// to <paramref name="isCommand"/>, sets reserved bits to "11" (v2.2
    /// default), and pads the digipeater chain into the source/last slot's
    /// E-bit appropriately.
    /// </summary>
    public static Ax25Frame Ui(
        Callsign destination,
        Callsign source,
        ReadOnlySpan<byte> info,
        byte pid = PidNoLayer3,
        bool isCommand = true,
        bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null)
    {
        var digiList = digipeaters?.Select(c => new Ax25Address(c, CrhBit: false, ExtensionBit: false)).ToList()
                       ?? new List<Ax25Address>();
        if (digiList.Count > MaxDigipeaters)
        {
            throw new ArgumentException($"AX.25 allows at most {MaxDigipeaters} digipeaters (got {digiList.Count})", nameof(digipeaters));
        }

        bool noDigipeaters = digiList.Count == 0;

        // §6.1.2 command/response encoding via address C-bits.
        // Command:  dest C = 1, source C = 0
        // Response: dest C = 0, source C = 1
        bool destC = isCommand;
        bool srcC = !isCommand;

        var dest = new Ax25Address(destination, CrhBit: destC, ExtensionBit: false);

        // Source E-bit is set only if there are no digipeaters; otherwise the
        // E-bit migrates to the last digipeater slot.
        var src = new Ax25Address(source, CrhBit: srcC, ExtensionBit: noDigipeaters);

        // Mark the last digipeater's E-bit.
        if (!noDigipeaters)
        {
            var last = digiList[^1];
            digiList[^1] = new Ax25Address(last.Callsign, CrhBit: last.CrhBit, ExtensionBit: true);
        }

        byte control = pollFinal ? ControlUiPollFinal : ControlUi;
        byte[] infoBytes = info.ToArray();

        // UI is a U frame — 1-octet control in both modulo-8 and modulo-128.
        return new Ax25Frame(dest, src, digiList, control, controlExtension: null, pid, infoBytes);
    }

    /// <summary>
    /// The number of bytes required by <see cref="WriteTo"/>.
    /// </summary>
    public int RequiredBytes =>
        Ax25Address.EncodedLength                       // destination
        + Ax25Address.EncodedLength                     // source
        + (Digipeaters.Count * Ax25Address.EncodedLength)
        + 1                                             // control (first octet)
        + (ControlExtension.HasValue ? 1 : 0)           // extended control (second octet, mod-128 I/S)
        + (Pid.HasValue ? 1 : 0)
        + Info.Length;

    /// <summary>
    /// Serialise this frame in KISS form (no flags, no FCS) into a fresh
    /// byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[RequiredBytes];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Number of bytes <see cref="WriteToWithFcs"/> / <see cref="ToBytesWithFcs"/>
    /// will write — the frame body plus the 2-octet FCS trailer.
    /// </summary>
    public int RequiredBytesWithFcs => RequiredBytes + 2;

    /// <summary>
    /// Serialise this frame with its CRC-16-CCITT FCS appended, in the byte
    /// order used by AX.25 on the wire (low byte first, then high byte).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the standard RFC-1226 AXIP / AXUDP wire form — what every real peer
    /// expects. Settled by a citation survey (see
    /// <c>docs/strict-vs-pragmatic-audit.md</c>): RFC 1226 + rfc1226-bis mandate the
    /// FCS ("is included", no FCS-less option); ax25ipd appends/checks it
    /// unconditionally (<c>process.c</c>); LinBPQ's BPQAXIP over UDP REQUIRES it (its
    /// UDP receive path drops anything whose CRC residue isn't <c>0xf0b8</c> —
    /// <c>bpqaxip.c</c>, confirmed on the wire); XRouter requires it; and real-on-air
    /// HDLC frames carry it. The FCS-less <see cref="ToBytes"/> form is non-standard —
    /// no surveyed real implementation accepts it; it exists only for a symmetric
    /// pdn↔pdn tunnel that opts out of the FCS on both ends.
    /// </para>
    /// <para>
    /// FCS byte-order note: AX.25 v2.2 §3.8 says "the FCS shall be transmitted
    /// most-significant bit first" — that refers to the bit-stream order on
    /// the radio, not the byte order of the serialised octets. In byte form
    /// (and in AXUDP payloads) the FCS is octet-pair low-byte-first, then
    /// high-byte. This has been verified empirically against XRouter (which
    /// rejects high-byte-first as "non-AXUDP").
    /// </para>
    /// </remarks>
    public byte[] ToBytesWithFcs()
    {
        var buffer = new byte[RequiredBytesWithFcs];
        WriteToWithFcs(buffer);
        return buffer;
    }

    /// <summary>
    /// Serialise this frame with FCS into <paramref name="destination"/>.
    /// Returns the number of bytes written.
    /// </summary>
    public int WriteToWithFcs(Span<byte> destination)
    {
        int bodyLength = WriteTo(destination);
        if (destination.Length < bodyLength + 2)
        {
            throw new ArgumentException($"destination too short (need {bodyLength + 2} bytes, got {destination.Length})", nameof(destination));
        }
        ushort fcs = Crc16Ccitt.Compute(destination[..bodyLength]);
        destination[bodyLength]     = (byte)(fcs & 0xFF);
        destination[bodyLength + 1] = (byte)((fcs >> 8) & 0xFF);
        return bodyLength + 2;
    }

    /// <summary>
    /// Serialise this frame into <paramref name="destination"/>. Returns the
    /// number of bytes written.
    /// </summary>
    public int WriteTo(Span<byte> destination)
    {
        if (destination.Length < RequiredBytes)
        {
            throw new ArgumentException($"destination too short (need {RequiredBytes} bytes, got {destination.Length})", nameof(destination));
        }

        int offset = 0;
        Destination.Write(destination[offset..]);
        offset += Ax25Address.EncodedLength;
        Source.Write(destination[offset..]);
        offset += Ax25Address.EncodedLength;

        foreach (var digi in Digipeaters)
        {
            digi.Write(destination[offset..]);
            offset += Ax25Address.EncodedLength;
        }

        destination[offset++] = Control;

        // Extended (modulo-128) I/S frames have a 2-octet control field,
        // transmitted first octet first (Fig 4.1b: bit 0 of the first octet is
        // the first bit sent). Control is the first octet; ControlExtension the
        // second.
        if (ControlExtension is byte controlExt)
        {
            destination[offset++] = controlExt;
        }

        if (Pid.HasValue)
        {
            destination[offset++] = Pid.Value;
        }

        Info.Span.CopyTo(destination[offset..]);
        offset += Info.Length;

        return offset;
    }

    /// <summary>
    /// Try to parse a frame from its KISS-form bytes (no opening / closing
    /// flag, no FCS), using the default lenient parser options.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out Ax25Frame? frame)
        => TryParse(bytes, Ax25ParseOptions.Lenient, out frame);

    /// <summary>
    /// Try to parse a frame from its KISS-form bytes, applying the supplied
    /// <see cref="Ax25ParseOptions"/> for strict-vs-lenient parser choices.
    /// Assumes modulo-8 (1-octet control); use the
    /// <see cref="TryParse(ReadOnlySpan{byte}, Ax25ParseOptions, bool, out Ax25Frame?)"/>
    /// overload to decode an extended (modulo-128) link.
    /// </summary>
    /// <remarks>
    /// Options control acceptance of: all-space callsign slots
    /// (<see cref="Ax25ParseOptions.AllowEmptyCallsignBase"/>), trailing
    /// bytes on frames §3.5 doesn't permit an info field on
    /// (<see cref="Ax25ParseOptions.AllowInfoOnSupervisoryFrames"/>).
    /// See <c>docs/strict-vs-pragmatic-audit.md</c> for the full inventory.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> bytes, Ax25ParseOptions options, [NotNullWhen(true)] out Ax25Frame? frame)
        => TryParse(bytes, options, extended: false, out frame);

    /// <summary>
    /// Try to parse a frame from its KISS-form bytes for a link operating at a
    /// known modulo. An I or S frame's control field is 1 octet under modulo-8
    /// and 2 octets under modulo-128 (Fig 4.1b), and the width is <em>not</em>
    /// derivable from the octets alone — so the caller (the receive path, which
    /// knows the session's negotiated modulo) passes <paramref name="extended"/>.
    /// U frames are 1 octet in both modes, so <paramref name="extended"/> only
    /// affects I and S frames.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, Ax25ParseOptions options, bool extended, [NotNullWhen(true)] out Ax25Frame? frame)
    {
        ArgumentNullException.ThrowIfNull(options);
        frame = null;

        // Minimum frame: 14 bytes address (dest+source) + 1 byte control.
        if (bytes.Length < (2 * Ax25Address.EncodedLength) + 1)
        {
            return false;
        }

        int offset = 0;
        Ax25Address destination;
        Ax25Address source;
        var digipeaters = new List<Ax25Address>();

        // Ax25Address.Read throws ArgumentException when the bytes don't
        // shape up as a valid address (non-A-Z/0-9 chars in the callsign,
        // spaces interleaved with non-spaces, etc.). For TryParse's
        // contract that has to translate to a "false" return, not a throw —
        // arbitrary KISS payloads land here without a higher framing layer
        // having vetted them.
        try
        {
            destination = Ax25Address.Read(bytes[offset..], options);
            offset += Ax25Address.EncodedLength;

            if (destination.ExtensionBit)
            {
                // E-bit set on destination — no source address present. Malformed.
                return false;
            }

            source = Ax25Address.Read(bytes[offset..], options);
            offset += Ax25Address.EncodedLength;

            var lastAddress = source;
            while (!lastAddress.ExtensionBit)
            {
                if (digipeaters.Count >= MaxDigipeaters)
                {
                    // E-bit never reached in the allowed digipeater range. Malformed.
                    return false;
                }
                if (bytes.Length < offset + Ax25Address.EncodedLength)
                {
                    return false;
                }
                lastAddress = Ax25Address.Read(bytes[offset..], options);
                offset += Ax25Address.EncodedLength;
                digipeaters.Add(lastAddress);
            }
        }
        catch (ArgumentException)
        {
            // Bytes were not a valid AX.25 address chain.
            return false;
        }

        if (bytes.Length < offset + 1)
        {
            return false;
        }

        byte control = bytes[offset++];
        byte? controlExtension = null;

        // Extended (modulo-128) I and S frames carry a 2-octet control field
        // (Fig 4.1b); U frames are 1 octet in both modes. The width can't be
        // told from the first octet alone, so the caller supplies the link's
        // modulo via `extended`. Frame-type discriminator: bit 0 = 0 → I;
        // bits 1-0 = 01 → S; bits 1-0 = 11 → U.
        bool isUFrame = (control & 0x03) == 0x03;
        if (extended && !isUFrame)
        {
            if (bytes.Length < offset + 1)
            {
                return false;
            }
            controlExtension = bytes[offset++];
        }

        byte? pid = null;
        ReadOnlyMemory<byte> info = ReadOnlyMemory<byte>.Empty;

        bool isUi = (control & 0xEF) == ControlUi;
        bool isI = (control & 0x01) == 0;

        if (isUi || isI)
        {
            if (bytes.Length < offset + 1)
            {
                return false;
            }
            pid = bytes[offset++];
            info = bytes[offset..].ToArray();
        }
        else if (offset < bytes.Length)
        {
            // Per §3.5, the information field appears on I, UI, FRMR, XID,
            // and TEST frames only. Other S frames (RR/RNR/REJ/SREJ) and
            // unnumbered command/response frames (SABM/SABME/DISC/UA/DM)
            // must have no trailing bytes.
            byte uBase = (byte)(control & 0xEF);
            bool isInfoAllowedUFrame =
                uBase == 0x87 ||   // FRMR
                uBase == 0xAF ||   // XID
                uBase == 0xE3;     // TEST
            if (!isInfoAllowedUFrame && !options.AllowInfoOnSupervisoryFrames)
            {
                return false;
            }
            info = bytes[offset..].ToArray();
        }

        frame = new Ax25Frame(destination, source, digipeaters, control, controlExtension, pid, info);
        return true;
    }
}
