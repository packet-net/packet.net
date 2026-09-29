using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// <see cref="Ax25Session.AllSentDataAcknowledged"/> (packet.net#850): the node's graceful close
/// sends DISC only when this is true, so it must be false while data sits on the I-frame queue
/// (here held back by a busy peer) and while an I-frame is out unacknowledged, and true once the
/// peer has acknowledged everything.
/// </summary>
public class Ax25SessionAllSentDataAcknowledgedTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    [Fact]
    public async Task Tracks_the_queue_and_the_unacknowledged_I_frames()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall }, time);
        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(5));
        await WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(5), "the SABM is accepted");
        session.AllSentDataAcknowledged.Should().BeTrue("nothing has been sent yet");

        // The peer is busy, so the data stays on the I-frame queue.
        modem.InjectInbound(Ax25Frame.Rnr(LocalCall, PeerCall, nr: 0, isCommand: false));
        await WaitFor(() => session.Context.PeerReceiverBusy, TimeSpan.FromSeconds(5), "the RNR is processed");
        listener.SendData(session, new byte[] { 0x41 });
        session.Context.IFrameQueue.Should().HaveCount(1, "a busy peer holds the I-frame on the queue");
        session.AllSentDataAcknowledged.Should().BeFalse("the queue still holds data");

        // The peer clears: the I-frame goes out, but is not yet acknowledged.
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: false));
        await WaitFor(() => session.Context.VS == 1, TimeSpan.FromSeconds(5), "the queued I-frame is sent");
        session.Context.IFrameQueue.Should().BeEmpty();
        session.AllSentDataAcknowledged.Should().BeFalse("I-frame 0 is out but unacknowledged");

        // The peer acknowledges it.
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 1, isCommand: false));
        await WaitFor(() => session.Context.VA == 1, TimeSpan.FromSeconds(5), "the RR acknowledges I-frame 0");
        session.AllSentDataAcknowledged.Should().BeTrue("everything sent is acknowledged");
    }
}
