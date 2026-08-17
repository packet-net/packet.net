using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The has-been-repeated (H) bit on the digipeater slots (§3.12.4 / §4.2.2). A
/// frame whose last repeater slot still has H=0 is in transit to that digipeater;
/// hearing it directly does not make it ours to answer. The listener used to
/// filter on destination alone, so it answered the unrepeated copy and then
/// processed the digi's repeat as a second frame - one SABM heard both ways drew
/// two UAs (packet-net/packet.net#696). It is now monitor-only until every
/// repeater slot is marked repeated.
/// </summary>
public class Ax25ListenerDigipeaterHBitTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);
    private static readonly Callsign DigiCall = new("MB7UR", 0);

    // The digipeater's SSID octet: 2 address slots (destination, source) of 7
    // octets each, then the repeater slot, whose 7th octet carries the H bit.
    private const int DigiSsidOctet = (2 * Ax25Address.EncodedLength) + 6;

    private static byte[] SabmViaDigi(bool repeated)
    {
        var bytes = Ax25Frame.Sabm(LocalCall, PeerCall, digipeaters: [DigiCall]).ToBytes().ToArray();
        (bytes[DigiSsidOctet] & 0x80).Should().Be(0, "the factory builds an unrepeated path");
        if (repeated)
        {
            bytes[DigiSsidOctet] |= 0x80;
        }
        return bytes;
    }

    [Fact]
    public async Task An_unrepeated_frame_is_monitor_only()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = 0;
        var traced = 0;
        listener.SessionAccepted += (_, _) => Interlocked.Increment(ref accepted);
        listener.FrameTraced += (_, _) => Interlocked.Increment(ref traced);

        await listener.StartAsync();
        modem.InjectInboundRaw(SabmViaDigi(repeated: false));

        await Task.Delay(500);
        accepted.Should().Be(0, "the frame is still on its way to MB7UR - it is not ours to answer yet");
        modem.SentFrames.Count.Should().Be(0, "answering would put a UA on the air for an undelivered SABM");
        traced.Should().Be(1, "a monitor consumer still sees the frame");
    }

    [Fact]
    public async Task The_repeated_copy_is_answered_exactly_once()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();

        // Both copies of one SABM, as a station within earshot of both the sender
        // and its digipeater hears them.
        modem.InjectInboundRaw(SabmViaDigi(repeated: false));
        modem.InjectInboundRaw(SabmViaDigi(repeated: true));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await Task.Delay(300);

        session.CurrentState.Should().Be("Connected");
        modem.SentFrames.Count.Should().Be(1, "exactly one UA for one SABM");
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var ua).Should().BeTrue();
        Ax25FrameClassifier.Classify(ua!).Should().BeOfType<UaReceived>();
    }

    [Fact]
    public async Task A_frame_with_no_digipeaters_is_unaffected()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
    }
}
