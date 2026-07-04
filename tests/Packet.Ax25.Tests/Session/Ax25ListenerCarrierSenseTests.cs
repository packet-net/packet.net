using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The native carrier-sense CSMA gate (OQ-012) wired into a live <see cref="Ax25Listener"/>:
/// an injected <see cref="ICarrierSense"/> holds the listener's keyups while the channel is
/// busy and releases them when it clears — without altering the data-link SDL (the SABM still
/// drives figc4.1 t14 → UA + Connected; only the <em>physical</em> UA is deferred). With no
/// source injected the listener is byte-for-byte its prior self, which every other
/// <c>Ax25Listener*Tests</c> file already covers; the baseline here pins that explicitly.
/// </summary>
public class Ax25ListenerCarrierSenseTests
{
    private static readonly Callsign LocalCall = new("M9YYY", 0);
    private static readonly Callsign PeerCall = new("GB7BPQ", 1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>A scripted carrier-sense source the test flips between busy and clear.</summary>
    private sealed class FakeCarrierSense : ICarrierSense
    {
        public bool? ChannelBusy { get; set; }
    }

    [Fact]
    public async Task Reply_is_deferred_while_the_channel_is_busy_and_sent_when_it_clears()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        var carrier = new FakeCarrierSense { ChannelBusy = true };   // channel busy at SABM time
        await using var listener = new Ax25Listener(
            modem, new Ax25ListenerOptions { MyCall = LocalCall, CarrierSense = carrier }, time);

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        // Peer opens the link. The SDL emits the UA (figc4.1 t14) and reaches Connected, but the
        // keyup is held by the medium-access gate because the channel is busy.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(Budget);

        await Task.Delay(75);   // real settle: the UA has reached the gate and is being held
        session.CurrentState.Should().Be("Connected", "the SDL transition ran — only the keyup is deferred");
        modem.SentFrames.Count.Should().Be(0, "the busy channel holds the UA off the air (native CSMA)");

        // Channel clears; one slot later the gate re-samples and keys up.
        carrier.ChannelBusy = false;
        time.Advance(TimeSpan.FromMilliseconds(100));
        await modem.SentFrames.WaitForCountAsync(1, Budget);

        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var ua).Should().BeTrue();
        IsUa(ua!).Should().BeTrue("the deferred frame that finally keyed up is the connect UA");
    }

    [Fact]
    public async Task With_no_carrier_sense_source_the_reply_is_sent_immediately()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        // No ICarrierSense — the always-clear degenerate gate. Behaviour must be unchanged.
        await using var listener = new Ax25Listener(
            modem, new Ax25ListenerOptions { MyCall = LocalCall }, time);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));

        // No clock advance needed: with no carrier-sense the UA keys up immediately.
        await modem.SentFrames.WaitForCountAsync(1, Budget);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var ua).Should().BeTrue();
        IsUa(ua!).Should().BeTrue();
    }

    [Fact]
    public async Task A_clear_channel_does_not_defer()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        var carrier = new FakeCarrierSense { ChannelBusy = false };   // source present but clear
        await using var listener = new Ax25Listener(
            modem, new Ax25ListenerOptions { MyCall = LocalCall, CarrierSense = carrier }, time);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));

        // A definite "clear" keys up immediately — only a definite "busy" defers.
        await modem.SentFrames.WaitForCountAsync(1, Budget);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var ua).Should().BeTrue();
        IsUa(ua!).Should().BeTrue();
    }

    /// <summary>UA U-frame test (§4.3.3): control 0x63 with the P/F bit masked off.</summary>
    private static bool IsUa(Ax25Frame frame) => (frame.Control & 0xEF) == 0x63;
}
