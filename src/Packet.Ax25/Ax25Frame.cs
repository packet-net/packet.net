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
public sealed class Ax25Frame
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

    /// <summary>Raw control byte (see <see cref="ControlUi"/>).</summary>
    public byte Control { get; }

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
    /// True if the P/F bit in the control byte is set.
    /// </summary>
    public bool PollFinal => (Control & 0x10) != 0;

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
        byte? pid,
        ReadOnlyMemory<byte> info)
    {
        Destination = destination;
        Source = source;
        Digipeaters = digipeaters;
        Control = control;
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

        return new Ax25Frame(dest, src, digiList, control, pid, infoBytes);
    }

    /// <summary>
    /// The number of bytes required by <see cref="WriteTo"/>.
    /// </summary>
    public int RequiredBytes =>
        Ax25Address.EncodedLength                       // destination
        + Ax25Address.EncodedLength                     // source
        + (Digipeaters.Count * Ax25Address.EncodedLength)
        + 1                                             // control
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
    /// flag, no FCS).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out Ax25Frame? frame)
    {
        frame = null;

        // Minimum frame: 14 bytes address (dest+source) + 1 byte control.
        if (bytes.Length < (2 * Ax25Address.EncodedLength) + 1)
        {
            return false;
        }

        int offset = 0;
        var destination = Ax25Address.Read(bytes[offset..]);
        offset += Ax25Address.EncodedLength;

        if (destination.ExtensionBit)
        {
            // E-bit set on destination — no source address present. Malformed.
            return false;
        }

        var source = Ax25Address.Read(bytes[offset..]);
        offset += Ax25Address.EncodedLength;

        var digipeaters = new List<Ax25Address>();
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
            lastAddress = Ax25Address.Read(bytes[offset..]);
            offset += Ax25Address.EncodedLength;
            digipeaters.Add(lastAddress);
        }

        if (bytes.Length < offset + 1)
        {
            return false;
        }

        byte control = bytes[offset++];
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
        else
        {
            // Other U / S frames — info is rare. We still capture any trailing
            // bytes as info for callers that care (FRMR / XID / TEST inspectors).
            if (offset < bytes.Length)
            {
                info = bytes[offset..].ToArray();
            }
        }

        frame = new Ax25Frame(destination, source, digipeaters, control, pid, info);
        return true;
    }
}
