using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Tune.Core;

/// <summary>
/// The stimulus frames of a deviation-tuning burst: short UI frames the
/// tuned end transmits on <c>RQ</c> and the meter end counts. The info text
/// starts with a fixed marker so the meter can tell burst frames from
/// anything else on the channel (status beacons, third parties).
/// </summary>
public static class TuningBurst
{
    /// <summary>The info-text marker every burst frame starts with.</summary>
    public const string Marker = "PTUNE";

    /// <summary>The destination address burst frames are sent to.</summary>
    public const string Destination = "TUNE";

    /// <summary>Info-field length — short on purpose (bursts should be cheap
    /// in airtime) but long enough to exercise the modem.</summary>
    public const int InfoLength = 40;

    /// <summary>Build burst frame <paramref name="index"/> of <paramref name="count"/>.</summary>
    /// <param name="source">Source callsign for the frame.</param>
    /// <param name="index">1-based frame index within the burst.</param>
    /// <param name="count">Total frames in the burst.</param>
    public static Ax25Frame BuildFrame(Callsign source, int index, int count)
    {
        string text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{Marker} {index}/{count} ");
        return Ax25Frame.Ui(
            destination: new Callsign(Destination),
            source: source,
            info: Encoding.ASCII.GetBytes(text.PadRight(InfoLength, '.')));
    }

    /// <summary>True when an inbound KISS frame is a burst frame (a data
    /// frame whose payload contains the marker).</summary>
    public static bool IsBurstFrame(KissFrame frame) =>
        frame.Command == KissCommand.Data && ContainsMarker(frame.Payload);

    private static bool ContainsMarker(ReadOnlySpan<byte> payload) =>
        payload.IndexOf("PTUNE"u8) >= 0;
}
