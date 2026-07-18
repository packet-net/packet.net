using System.Globalization;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;

namespace Packet.Node.Core.Transports;

/// <summary>Parses a soundmodem PTT spec string into an <see cref="IPttControl"/>. Shared by the
/// soundmodem transport and the ARDOP/POCSAG audio services so the spec grammar lives in one place.</summary>
internal static class SoundModemPtt
{
    /// <summary>
    /// empty ⇒ VOX (<c>NullPtt</c>); <c>serial:&lt;device&gt;[:rts|:dtr]</c>;
    /// <c>cm108:&lt;hidraw&gt;[:gpio]</c>.
    /// </summary>
    public static IPttControl Create(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return new NullPtt();
        }

        string[] parts = spec.Split(':');
        return parts switch
        {
            ["serial", var device] => new SerialPtt(device),
            ["serial", var device, var line] => new SerialPtt(device, useRts: line != "dtr", useDtr: line == "dtr"),
            ["cm108", var device] => new Cm108Ptt(device),
            ["cm108", var device, var gpio] => new Cm108Ptt(device, int.Parse(gpio, CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException($"unknown ptt spec '{spec}'"),
        };
    }
}
