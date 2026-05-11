using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Properties;

public class Ax25FrameRoundTripProperties
{
    /// <summary>
    /// Encoding then decoding a UI frame returns an equivalent frame for
    /// any well-formed destination / source / digipeater path / info /
    /// PID / poll-final flag combination.
    /// </summary>
    [Property]
    public void Ui_Encode_Then_Decode_Roundtrips(
        NonEmptyString destBase,
        byte destSsidRaw,
        NonEmptyString srcBase,
        byte srcSsidRaw,
        byte pid,
        bool pollFinal,
        bool isCommand,
        byte[] info)
    {
        var dest = SanitiseCallsign(destBase.Get, destSsidRaw);
        var src = SanitiseCallsign(srcBase.Get, srcSsidRaw);
        info ??= Array.Empty<byte>();

        var original = Ax25Frame.Ui(
            destination: dest,
            source: src,
            info: info,
            pid: pid,
            isCommand: isCommand,
            pollFinal: pollFinal);

        var bytes = original.ToBytes();
        Ax25Frame.TryParse(bytes, out var decoded).ShouldBeTrue();

        decoded!.Destination.Callsign.ShouldBe(dest);
        decoded.Source.Callsign.ShouldBe(src);
        decoded.Control.ShouldBe(original.Control);
        decoded.Pid.ShouldBe(original.Pid);
        decoded.Info.ToArray().ShouldBe(info);
        decoded.IsCommand.ShouldBe(isCommand);
        decoded.IsResponse.ShouldBe(!isCommand);
        decoded.PollFinal.ShouldBe(pollFinal);
    }

    /// <summary>
    /// Map an arbitrary FsCheck-generated string to a valid AX.25 callsign
    /// (1–6 chars, uppercase A–Z / 0–9). We don't want the property's
    /// shrinking to spend time chasing input-validation rejection paths.
    /// </summary>
    private static Callsign SanitiseCallsign(string raw, byte ssidRaw)
    {
        var chars = raw.ToUpperInvariant()
            .Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            .Take(6)
            .ToArray();
        if (chars.Length == 0)
        {
            chars = new[] { 'X' };
        }
        return new Callsign(new string(chars), (byte)(ssidRaw & 0x0F));
    }
}
