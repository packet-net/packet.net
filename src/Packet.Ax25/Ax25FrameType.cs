namespace Packet.Ax25;

/// <summary>
/// The wire type of an AX.25 frame, read off the first control octet (§4.2, Fig 4.1a/b).
/// </summary>
/// <remarks>
/// <para>
/// This is the label a monitor puts on a frame, as distinct from the <see cref="Session.Ax25Event"/>
/// a session machine consumes: <see cref="Session.Ax25FrameClassifier"/> folds a frame with an
/// information field where none is permitted into an error event, because that is what the
/// state machine has to react to, whereas the frame on the air is still an RR. The type is what it
/// is regardless of what any session thinks of it.
/// </para>
/// <para>
/// Modulo-independent. The I/S/U discriminator (bit 0; bits 1-0), the S-frame subtype (bits 3-2)
/// and the U-frame modifier bits (7-5, 3-2) all sit in the first control octet in both modulo-8
/// and modulo-128, so the type of an extended frame reads without knowing the link's modulo.
/// </para>
/// </remarks>
public enum Ax25FrameType
{
    /// <summary>A control octet that matches no frame type the spec defines.</summary>
    Unknown = 0,

    /// <summary>Information (§4.3.1): numbered, acknowledged data.</summary>
    I,

    /// <summary>Receive Ready (§4.3.2.1).</summary>
    Rr,

    /// <summary>Receive Not Ready (§4.3.2.2).</summary>
    Rnr,

    /// <summary>Reject (§4.3.2.3): go back and resend from N(R).</summary>
    Rej,

    /// <summary>Selective Reject (§4.3.2.4): resend N(R) alone.</summary>
    Srej,

    /// <summary>Unnumbered Information (§4.3.3.6): unacknowledged data.</summary>
    Ui,

    /// <summary>Set Asynchronous Balanced Mode (§4.3.3.1): open a modulo-8 link.</summary>
    Sabm,

    /// <summary>Set Asynchronous Balanced Mode Extended (§4.3.3.2): open a modulo-128 link.</summary>
    Sabme,

    /// <summary>Disconnect (§4.3.3.3).</summary>
    Disc,

    /// <summary>Unnumbered Acknowledge (§4.3.3.4): the answer to SABM, SABME and DISC.</summary>
    Ua,

    /// <summary>Disconnected Mode (§4.3.3.5): "no link here".</summary>
    Dm,

    /// <summary>Frame Reject (§4.3.3.9): a protocol violation reported back.</summary>
    Frmr,

    /// <summary>Exchange Identification (§4.3.3.7): parameter negotiation.</summary>
    Xid,

    /// <summary>Test (§4.3.3.8): a loopback.</summary>
    Test,
}

/// <summary>Convenience queries over <see cref="Ax25FrameType"/>.</summary>
public static class Ax25FrameTypeExtensions
{
    /// <summary>True for <see cref="Ax25FrameType.I"/>.</summary>
    public static bool IsInformation(this Ax25FrameType type) => type == Ax25FrameType.I;

    /// <summary>True for RR, RNR, REJ and SREJ - the frames that carry N(R) and nothing else.</summary>
    public static bool IsSupervisory(this Ax25FrameType type)
        => type is Ax25FrameType.Rr or Ax25FrameType.Rnr or Ax25FrameType.Rej or Ax25FrameType.Srej;

    /// <summary>True for every unnumbered type, UI included, and for <see cref="Ax25FrameType.Unknown"/>
    /// (an unrecognised control octet is, by its discriminator bits, a U frame).</summary>
    public static bool IsUnnumbered(this Ax25FrameType type)
        => !type.IsInformation() && !type.IsSupervisory();

    /// <summary>True for the types that carry N(R): I frames and the four supervisory frames.</summary>
    public static bool CarriesNr(this Ax25FrameType type)
        => type.IsInformation() || type.IsSupervisory();

    /// <summary>
    /// The conventional upper-case mnemonic ("RR", "SABME", "UI"), as monitors have always
    /// printed it. <see cref="Ax25FrameType.Unknown"/> renders as "U".
    /// </summary>
    public static string Mnemonic(this Ax25FrameType type) => type switch
    {
        Ax25FrameType.I => "I",
        Ax25FrameType.Rr => "RR",
        Ax25FrameType.Rnr => "RNR",
        Ax25FrameType.Rej => "REJ",
        Ax25FrameType.Srej => "SREJ",
        Ax25FrameType.Ui => "UI",
        Ax25FrameType.Sabm => "SABM",
        Ax25FrameType.Sabme => "SABME",
        Ax25FrameType.Disc => "DISC",
        Ax25FrameType.Ua => "UA",
        Ax25FrameType.Dm => "DM",
        Ax25FrameType.Frmr => "FRMR",
        Ax25FrameType.Xid => "XID",
        Ax25FrameType.Test => "TEST",
        _ => "U",
    };
}
