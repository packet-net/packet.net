using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// A request to send an information (I) frame. The dispatcher emits
/// these in response to the figure-canonical <c>I_command</c>
/// signal_lower verb (figc4.4 t19/t20 and the SREJ retransmit paths).
/// </summary>
/// <param name="IsCommand">
/// Always <c>true</c> — I-frames are commands per §4.3.1. Modelled as
/// a parameter for symmetry with the other frame specs.
/// </param>
/// <param name="PBit">
/// P bit value. Read from <see cref="PendingFrame.PfBit"/> (default
/// <c>false</c> — figc4.4 sets <c>p := 0</c> explicitly before
/// <c>I_command</c>). I-frames carry only a P bit (commands); they're
/// never responses, so no F bit semantics.
/// </param>
/// <param name="Nr">
/// N(R) field — the acknowledgement sequence number. Read from
/// <see cref="PendingFrame.Nr"/>, defaulting to
/// <see cref="Ax25SessionContext.VR"/>.
/// </param>
/// <param name="Ns">
/// N(S) field — the send sequence number of this I-frame. Read from
/// <see cref="PendingFrame.Ns"/>, defaulting to
/// <see cref="Ax25SessionContext.VS"/>.
/// </param>
/// <param name="Info">
/// Information field payload, sourced from the I-frame queue entry
/// that just popped.
/// </param>
/// <param name="Pid">
/// PID octet identifying the Layer-3 protocol carried (§3.4).
/// Sourced from the queue entry alongside <see cref="Info"/>.
/// </param>
/// <param name="Path">
/// Optional digipeater chain override. See <see cref="UFrameSpec.Path"/>.
/// Most I-frame emissions are triggered by an upper-layer DL request
/// rather than a frame, so this is usually <c>null</c> and the wire
/// path uses the session context's chain.
/// </param>
public readonly record struct IFrameSpec(
    bool IsCommand,
    bool PBit,
    byte Nr,
    byte Ns,
    ReadOnlyMemory<byte> Info,
    byte Pid,
    IReadOnlyList<Callsign>? Path = null);
