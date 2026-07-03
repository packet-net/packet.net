using System.Globalization;
using System.Text;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The reply to a GETRSSI query (<see cref="NinoTncCommands.GetRssiSubcommand"/>):
/// ASCII <c>"RSSI:-62.54"</c> on the firmware reply command byte 0xE0.
/// </summary>
/// <remarks>
/// Despite the name, this is <b>not</b> an RF dBm figure: bench measurement
/// (2026-07-02, firmware 3.41 on the Tait rig) shows it is the RMS level of
/// the TNC's RX audio in dB — open-squelch flat-tap noise reads ≈ −33,
/// while a carrier quieting the channel with a 440 Hz CQBEEP tone reads
/// ≈ −62. It tracks what the modem actually hears, which makes it a remote
/// audio-level meter for deviation/level tuning. <b>Firmware 3.41 only:</b>
/// the GETRSSI query was removed in firmware 3.44 (no reply at all), so no
/// frame of this shape ever arrives from a 3.44 TNC.
/// </remarks>
/// <param name="LevelDb">RX-audio RMS level in dB (see remarks).</param>
public sealed record NinoTncRssiReading(float LevelDb)
{
    private const string Prefix = "RSSI:";

    /// <summary>
    /// Try to parse a GETRSSI reply out of a decoded KISS frame. Requires
    /// the firmware reply command byte (raw 0xE0 — see
    /// <see cref="NinoTncCommands.IsReply"/>) and the <c>RSSI:</c> prefix.
    /// </summary>
    public static bool TryParse(KissFrame frame, out NinoTncRssiReading? parsed)
    {
        parsed = null;
        if (!NinoTncCommands.IsReply(frame))
        {
            return false;
        }
        return TryParse(frame.Payload, out parsed);
    }

    /// <summary>
    /// Try to parse a GETRSSI reply out of raw reply-frame payload bytes
    /// (<c>"RSSI:"</c> + a decimal level).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out NinoTncRssiReading? parsed)
    {
        parsed = null;
        if (payload.Length <= Prefix.Length)
        {
            return false;
        }
        for (int i = 0; i < Prefix.Length; i++)
        {
            if (payload[i] != (byte)Prefix[i])
            {
                return false;
            }
        }

        string text = Encoding.ASCII.GetString(payload[Prefix.Length..]).Trim();
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float level))
        {
            return false;
        }
        parsed = new NinoTncRssiReading(level);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"NinoTncRssiReading {LevelDb:0.00} dB");
}
