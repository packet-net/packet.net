using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A numeric <c>=II:</c> diagnostic-register report received from the
/// NinoTNC — the periodic (SETBCNINT-paced) status frame, or the GETALL
/// response on firmware that answers GETALL numerically. See
/// <see cref="NinoTncStatusFrame"/> for the shape and firmware notes.
/// </summary>
public sealed record NinoTncStatusFrameReceivedEvent(KissFrame Raw, NinoTncStatusFrame Status) : KissInboundEvent(Raw);
