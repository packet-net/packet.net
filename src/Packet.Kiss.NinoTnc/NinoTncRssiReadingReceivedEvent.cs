using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A GETRSSI reply received from the NinoTNC (the <c>RSSI:</c> ASCII frame
/// on the firmware reply command byte 0xE0). The value is the TNC's
/// RX-audio RMS level in dB — see <see cref="NinoTncRssiReading"/> for the
/// bench-verified interpretation. Usually consumed via
/// <c>NinoTncSerialPort.GetRssiAsync</c>, which correlates it with the
/// query; this event exists for passive listeners.
/// </summary>
public sealed record NinoTncRssiReadingReceivedEvent(KissFrame Raw, NinoTncRssiReading Reading) : KissInboundEvent(Raw);
