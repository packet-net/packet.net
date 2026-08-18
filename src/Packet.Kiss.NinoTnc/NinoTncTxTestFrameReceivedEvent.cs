using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The on-demand TX-Test diagnostic frame the NinoTNC emits when the
/// operator presses the front-panel TX-Test button. The over-air frame
/// (the modem-generated test signal - see
/// <see cref="NinoTncAirTestFrameReceivedEvent"/>) is *not* this; this
/// is the synthetic KISS-Data frame the firmware delivers to its USB
/// host at the same moment.
/// </summary>
public sealed record NinoTncTxTestFrameReceivedEvent(KissFrame Raw, NinoTncTxTestFrame Diagnostic) : KissInboundEvent(Raw);
