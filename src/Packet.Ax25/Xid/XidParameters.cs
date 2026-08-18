namespace Packet.Ax25.Xid;

/// <summary>
/// The decoded, semantic view of an XID information field's parameter set
/// (AX.25 v2.2 §4.3.3.7, Figure 4.5). Each field is <c>null</c> when the
/// corresponding PI/PL/PV triple is <em>absent</em> from the frame - which,
/// per §4.3.3.7 ¶1024, means "use the currently-negotiated value" rather than
/// any particular default. The negotiation FSM (MDL, App. C5) is responsible
/// for turning a command + response pair into the agreed link parameters;
/// this type is just the wire payload, decoded.
/// </summary>
/// <remarks>
/// Unit conventions, chosen to match both the wire format and
/// <c>Ax25SessionContext</c>:
/// <list type="bullet">
/// <item><see cref="IFieldLengthRxBits"/> is in <b>bits</b> (the wire unit:
/// Figure 4.5 says "N1×8"); <see cref="IFieldLengthRxOctets"/> converts to the
/// N1 octet count the session uses.</item>
/// <item><see cref="AckTimerMillis"/> is in milliseconds (the wire unit);
/// <see cref="AckTimer"/> exposes it as a <see cref="TimeSpan"/>.</item>
/// <item><see cref="WindowSizeRx"/> and <see cref="Retries"/> are plain counts.</item>
/// </list>
/// </remarks>
public sealed record XidParameters
{
    /// <summary>Classes of Procedures (PI=2) - duplex selection. <c>null</c> if absent.</summary>
    public ClassesOfProcedures? ClassesOfProcedures { get; init; }

    /// <summary>HDLC Optional Functions (PI=3) - reject scheme + modulo + segmenter. <c>null</c> if absent.</summary>
    public HdlcOptionalFunctions? HdlcOptionalFunctions { get; init; }

    /// <summary>
    /// I Field Length Receive (PI=6), in <b>bits</b> (the wire unit, N1×8).
    /// <c>null</c> if absent. Default 2048 bits (256 octets) per §6.3.2 ¶1092.
    /// </summary>
    public int? IFieldLengthRxBits { get; init; }

    /// <summary>Window Size Receive k (PI=8), in frames. <c>null</c> if absent.</summary>
    public int? WindowSizeRx { get; init; }

    /// <summary>Acknowledge Timer T1 (PI=9), in milliseconds. <c>null</c> if absent.</summary>
    public int? AckTimerMillis { get; init; }

    /// <summary>Retries N2 (PI=10), the retry count. <c>null</c> if absent.</summary>
    public int? Retries { get; init; }

    /// <summary>
    /// <see cref="IFieldLengthRxBits"/> converted to octets (N1). <c>null</c>
    /// if the field is absent. The wire value is bits; N1 in the session is
    /// octets, so we divide by 8.
    /// </summary>
    public int? IFieldLengthRxOctets => IFieldLengthRxBits is { } bits ? bits / 8 : null;

    /// <summary><see cref="AckTimerMillis"/> as a <see cref="TimeSpan"/>, or <c>null</c> if absent.</summary>
    public TimeSpan? AckTimer => AckTimerMillis is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>
    /// Build an N1 (I-field length, octets) parameter from an octet count,
    /// converting to the wire's bit unit. Convenience for callers that think
    /// in octets (as the session does).
    /// </summary>
    public static int OctetsToBits(int octets) => octets * 8;
}
