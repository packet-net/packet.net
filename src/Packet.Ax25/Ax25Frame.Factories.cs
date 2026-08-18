using Packet.Core;

namespace Packet.Ax25;

/// <summary>
/// Factories for non-UI frame types. Each method mirrors the address /
/// digipeater / C-bit / E-bit handling of <see cref="Ui"/> and selects
/// the appropriate control byte per §4.3.2 (S-frames) or §4.3.3
/// (U-frames). I and S frames take an <c>extended</c> flag selecting the
/// 2-octet modulo-128 control field (Fig 4.1b); U frames are 1 octet in
/// both modes, so the U-frame factories have no <c>extended</c> parameter.
/// </summary>
public sealed partial class Ax25Frame
{
    // ─── U-frame control-byte bases (§4.3.3, P/F bit at 0x10) ──────────
    private const byte ControlSabm = 0x2F;
    private const byte ControlSabme = 0x6F;
    private const byte ControlDisc = 0x43;
    private const byte ControlUa = 0x63;
    private const byte ControlDm = 0x0F;
    private const byte ControlFrmr = 0x87;
    private const byte ControlXid = 0xAF;
    private const byte ControlTest = 0xE3;
    private const byte ControlPfBit = 0x10;

    // ─── S-frame control-byte bases (§4.3.2, P/F bit at 0x10) ──────────
    // S-frame control = (N(R) << 5) | (P/F << 4) | base.
    private const byte ControlRr = 0x01;
    private const byte ControlRnr = 0x05;
    private const byte ControlRej = 0x09;
    private const byte ControlSrej = 0x0D;

    /// <summary>
    /// Construct a Set Asynchronous Balanced Mode (SABM) command frame per
    /// §4.3.3.1. Mod-8 connection establishment from the originator.
    /// </summary>
    public static Ax25Frame Sabm(Callsign destination, Callsign source,
        bool pollBit = true, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlSabm | (pollBit ? ControlPfBit : 0)), isCommand: true,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);

    /// <summary>
    /// Construct a Set Asynchronous Balanced Mode Extended (SABME) command
    /// frame per §4.3.3.2. Mod-128 connection establishment.
    /// </summary>
    public static Ax25Frame Sabme(Callsign destination, Callsign source,
        bool pollBit = true, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlSabme | (pollBit ? ControlPfBit : 0)), isCommand: true,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);

    /// <summary>
    /// Construct a DISConnect (DISC) command frame per §4.3.3.3.
    /// </summary>
    public static Ax25Frame Disc(Callsign destination, Callsign source,
        bool pollBit = true, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlDisc | (pollBit ? ControlPfBit : 0)), isCommand: true,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);

    /// <summary>
    /// Construct an Unnumbered Acknowledge (UA) response frame per §4.3.3.4.
    /// </summary>
    public static Ax25Frame Ua(Callsign destination, Callsign source,
        bool finalBit = true, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlUa | (finalBit ? ControlPfBit : 0)), isCommand: false,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);

    /// <summary>
    /// Construct a Disconnected Mode (DM) response frame per §4.3.3.5.
    /// </summary>
    public static Ax25Frame Dm(Callsign destination, Callsign source,
        bool finalBit = false, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlDm | (finalBit ? ControlPfBit : 0)), isCommand: false,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);

    /// <summary>
    /// Construct a Frame Reject (FRMR) response frame per §4.3.3.6. The
    /// 3-byte info field carrying the rejection cause must be supplied by
    /// the caller - this factory doesn't construct it.
    /// </summary>
    public static Ax25Frame Frmr(Callsign destination, Callsign source,
        ReadOnlySpan<byte> info,
        bool finalBit = false, IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlFrmr | (finalBit ? ControlPfBit : 0)), isCommand: false,
            destination, source, info, pid: null, digipeaters);

    /// <summary>
    /// Construct an Exchange Identification (XID) frame per §4.3.4.1. XID
    /// can be sent as command or response.
    /// </summary>
    public static Ax25Frame Xid(Callsign destination, Callsign source,
        ReadOnlySpan<byte> info,
        bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlXid | (pollFinal ? ControlPfBit : 0)), isCommand,
            destination, source, info, pid: null, digipeaters);

    /// <summary>
    /// Construct a TEST frame per §4.3.4.2. TEST can be sent as command or
    /// response; the response echoes the command's information field.
    /// </summary>
    public static Ax25Frame Test(Callsign destination, Callsign source,
        ReadOnlySpan<byte> info,
        bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null)
        => UFrameAt((byte)(ControlTest | (pollFinal ? ControlPfBit : 0)), isCommand,
            destination, source, info, pid: null, digipeaters);

    /// <summary>
    /// Construct a Receive Ready (RR) supervisory frame per §4.3.2.1.
    /// Set <paramref name="extended"/> for the modulo-128 2-octet control field.
    /// </summary>
    public static Ax25Frame Rr(Callsign destination, Callsign source,
        byte nr, bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null, bool extended = false)
        => SFrameAt(ControlRr, destination, source, nr, isCommand, pollFinal, extended, digipeaters);

    /// <summary>
    /// Construct a Receive Not Ready (RNR) supervisory frame per §4.3.2.2.
    /// Set <paramref name="extended"/> for the modulo-128 2-octet control field.
    /// </summary>
    public static Ax25Frame Rnr(Callsign destination, Callsign source,
        byte nr, bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null, bool extended = false)
        => SFrameAt(ControlRnr, destination, source, nr, isCommand, pollFinal, extended, digipeaters);

    /// <summary>
    /// Construct a REJect (REJ) supervisory frame per §4.3.2.3.
    /// Set <paramref name="extended"/> for the modulo-128 2-octet control field.
    /// </summary>
    public static Ax25Frame Rej(Callsign destination, Callsign source,
        byte nr, bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null, bool extended = false)
        => SFrameAt(ControlRej, destination, source, nr, isCommand, pollFinal, extended, digipeaters);

    /// <summary>
    /// Construct a Selective REJect (SREJ) supervisory frame per §4.3.2.4.
    /// Set <paramref name="extended"/> for the modulo-128 2-octet control field.
    /// </summary>
    public static Ax25Frame Srej(Callsign destination, Callsign source,
        byte nr, bool isCommand, bool pollFinal = false,
        IEnumerable<Callsign>? digipeaters = null, bool extended = false)
        => SFrameAt(ControlSrej, destination, source, nr, isCommand, pollFinal, extended, digipeaters);

    /// <summary>
    /// Construct an Information (I) frame per §4.3.1. Always a command;
    /// carries N(R), N(S), the P bit, the PID octet, and a Layer-3 payload.
    /// Set <paramref name="extended"/> for the modulo-128 2-octet control field.
    /// </summary>
    public static Ax25Frame I(Callsign destination, Callsign source,
        byte nr, byte ns, ReadOnlySpan<byte> info, byte pid = PidNoLayer3,
        bool pollBit = false, IEnumerable<Callsign>? digipeaters = null, bool extended = false)
    {
        if (extended)
        {
            // I-frame control (mod-128, Fig 4.1b): octet0 = (N(S) << 1) | 0
            // (7-bit N(S), bit 0 = 0); octet1 = (N(R) << 1) | P (7-bit N(R),
            // bit 0 = P).
            byte first = (byte)((ns & 0x7F) << 1);
            byte second = (byte)(((nr & 0x7F) << 1) | (pollBit ? 0x01 : 0));
            return UFrameAt(first, isCommand: true, destination, source, info, pid, digipeaters, controlExtension: second);
        }

        // I-frame control (mod-8): (N(R) << 5) | (P << 4) | (N(S) << 1) | 0.
        byte control = (byte)(((nr & 0x07) << 5) | (pollBit ? ControlPfBit : 0) | ((ns & 0x07) << 1));
        return UFrameAt(control, isCommand: true, destination, source, info, pid, digipeaters);
    }

    /// <summary>
    /// S-frame mod-8 control byte: <c>(N(R) &lt;&lt; 5) | (P/F &lt;&lt; 4) | base</c>.
    /// </summary>
    private static byte SFrameControl(byte baseControl, byte nr, bool pollFinal)
        => (byte)(((nr & 0x07) << 5) | (pollFinal ? ControlPfBit : 0) | baseControl);

    /// <summary>
    /// Build a supervisory frame in either modulo. Mod-8 packs N(R)/P-F into
    /// the single control octet; mod-128 (Fig 4.3b) keeps the base octet
    /// (SS bits + "01", high nibble zero) as octet0 and puts
    /// <c>(N(R) &lt;&lt; 1) | P/F</c> in octet1.
    /// </summary>
    private static Ax25Frame SFrameAt(byte baseControl, Callsign destination, Callsign source,
        byte nr, bool isCommand, bool pollFinal, bool extended, IEnumerable<Callsign>? digipeaters)
    {
        if (extended)
        {
            byte second = (byte)(((nr & 0x7F) << 1) | (pollFinal ? 0x01 : 0));
            return UFrameAt(baseControl, isCommand, destination, source,
                info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters, controlExtension: second);
        }

        return UFrameAt(SFrameControl(baseControl, nr, pollFinal), isCommand,
            destination, source, info: ReadOnlySpan<byte>.Empty, pid: null, digipeaters);
    }

    /// <summary>
    /// Core frame-assembly helper. Builds the address fields (C-bits per
    /// §6.1.2 command/response, E-bit migration onto the last digipeater
    /// or the source slot) and stitches in the supplied control byte +
    /// optional PID + info field.
    /// </summary>
    private static Ax25Frame UFrameAt(byte control, bool isCommand,
        Callsign destination, Callsign source,
        ReadOnlySpan<byte> info, byte? pid,
        IEnumerable<Callsign>? digipeaters,
        byte? controlExtension = null)
    {
        var digiList = digipeaters?.Select(c => new Ax25Address(c, CrhBit: false, ExtensionBit: false)).ToList()
                       ?? new List<Ax25Address>();
        if (digiList.Count > MaxDigipeaters)
        {
            throw new ArgumentException($"AX.25 allows at most {MaxDigipeaters} digipeaters (got {digiList.Count})", nameof(digipeaters));
        }

        bool noDigipeaters = digiList.Count == 0;

        // §6.1.2: command sets dest C=1, source C=0; response sets dest C=0, source C=1.
        var dest = new Ax25Address(destination, CrhBit: isCommand, ExtensionBit: false);
        var src = new Ax25Address(source, CrhBit: !isCommand, ExtensionBit: noDigipeaters);

        if (!noDigipeaters)
        {
            var last = digiList[^1];
            digiList[^1] = new Ax25Address(last.Callsign, CrhBit: last.CrhBit, ExtensionBit: true);
        }

        byte[] infoBytes = info.ToArray();
        return new Ax25Frame(dest, src, digiList, control, controlExtension, pid, infoBytes);
    }
}
