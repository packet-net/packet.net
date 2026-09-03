using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Monitor;
using Packet.Core;

namespace Packet.Ax25.Tests.Monitor;

public class Ax25LinkObserverTests
{
    private static readonly Callsign Node = new("GB7RDG", 2);
    private static readonly Callsign User = new("M0LTE", 9);
    private static readonly Callsign Digi = new("MB7UXX", 0);
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly Ax25LinkObserver _observer = new();

    private Ax25LinkEvent See(Ax25Frame frame, int seconds = 0, string port = "1", bool transmitted = false)
        => _observer.Observe(port, frame.ToBytes(), T0.AddSeconds(seconds), transmitted)!;

    private Ax25LinkSnapshot Link(Callsign a, Callsign b, string port = "1")
        => _observer.Snapshot(Ax25LinkObserver.LinkIdFor(port, a, b))!;

    private void Connect(int at = 0)
    {
        See(Ax25Frame.Sabm(Node, User), at);
        See(Ax25Frame.Ua(User, Node), at + 1);
    }

    [Fact]
    public void Bytes_That_Are_Not_A_Frame_Produce_No_Event_And_No_Link()
    {
        _observer.Observe("1", [0x01, 0x02, 0x03], T0).Should().BeNull();
        _observer.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Both_Directions_Land_On_One_Link_Whose_A_Is_The_First_Station_Heard()
    {
        var call = See(Ax25Frame.Sabm(Node, User));
        var answer = See(Ax25Frame.Ua(User, Node), 1);

        answer.LinkId.Should().Be(call.LinkId);
        call.LinkId.Should().Be("1|GB7RDG-2<>M0LTE-9");
        var link = Link(Node, User);
        link.A.Should().Be(User);
        link.B.Should().Be(Node);
        link.AtoB.Frames.Should().Be(1);
        link.BtoA.Frames.Should().Be(1);
    }

    [Fact]
    public void The_Same_Pair_On_Another_Port_Is_Another_Link()
    {
        See(Ax25Frame.Sabm(Node, User), port: "1");
        See(Ax25Frame.Sabm(Node, User), port: "2");

        _observer.Snapshot().Should().HaveCount(2);
        Link(Node, User, "1").AtoB.CallsUnanswered.Should().Be(1);
        Link(Node, User, "2").AtoB.CallsUnanswered.Should().Be(1);
    }

    [Fact]
    public void A_Call_Answered_Then_Hung_Up_Walks_The_States()
    {
        var call = See(Ax25Frame.Sabm(Node, User));
        call.State.Should().Be(Ax25LinkState.Calling);
        call.Narration.Should().Be("calls GB7RDG-2");
        call.IsCommand.Should().BeTrue();
        call.PollFinal.Should().BeTrue();

        var up = See(Ax25Frame.Ua(User, Node), 1);
        up.State.Should().Be(Ax25LinkState.Connected);
        up.Flags.Should().Be(Ax25LinkFlags.LinkUp);
        up.Narration.Should().Be("accepts the call; link up");
        Link(Node, User).Inferred.Should().BeFalse();

        var bye = See(Ax25Frame.Disc(Node, User), 2);
        bye.State.Should().Be(Ax25LinkState.Disconnecting);
        bye.Narration.Should().Be("hangs up");

        var down = See(Ax25Frame.Ua(User, Node), 3);
        down.State.Should().Be(Ax25LinkState.Disconnected);
        down.Flags.Should().Be(Ax25LinkFlags.LinkDown);
        down.Narration.Should().Be("confirms; link down");
    }

    [Fact]
    public void Repeated_Calls_Count_Attempts_Until_Something_Answers()
    {
        See(Ax25Frame.Sabm(Node, User), 0);
        var second = See(Ax25Frame.Sabm(Node, User), 10);
        var third = See(Ax25Frame.Sabm(Node, User), 20);

        second.Flags.Should().Be(Ax25LinkFlags.Repeat);
        second.Count.Should().Be(2);
        third.Count.Should().Be(3);
        third.Narration.Should().Be("calls GB7RDG-2 again (attempt 3)");
        Link(Node, User).Concern.Should().Be("M0LTE-9 has called 3 times with no answer");

        var refused = See(Ax25Frame.Dm(User, Node, finalBit: true), 22);
        refused.Flags.Should().Be(Ax25LinkFlags.Refused);
        refused.State.Should().Be(Ax25LinkState.Disconnected);
        refused.Narration.Should().Be("refuses the call");
        Link(Node, User).Concern.Should().BeNull();
        Link(Node, User).AtoB.CallsUnanswered.Should().Be(0);
    }

    [Fact]
    public void A_Restart_On_A_Live_Link_Says_So_And_Forgets_The_Sequence_Numbers()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8), 2);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "b"u8), 3);

        var restart = See(Ax25Frame.Sabm(Node, User), 4);
        restart.Narration.Should().Be("restarts the link with GB7RDG-2");
        restart.State.Should().Be(Ax25LinkState.Calling);
        See(Ax25Frame.Ua(User, Node), 5);

        var fresh = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "c"u8), 6);
        fresh.Flags.Should().Be(Ax25LinkFlags.None);
        fresh.Narration.Should().Be("sends #0");
    }

    [Fact]
    public void Data_Is_Numbered_Acknowledged_And_Resends_Are_Called_Out()
    {
        Connect();
        var first = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "hello"u8), 2);
        first.Narration.Should().Be("sends #0");
        first.Text.Should().Be("hello");
        first.Ns.Should().Be(0);
        first.Nr.Should().Be(0);

        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "world"u8), 3);

        var ack = See(Ax25Frame.Rr(User, Node, nr: 2, isCommand: false), 4);
        ack.Narration.Should().Be("acknowledges #0-#1");
        ack.Flags.Should().Be(Ax25LinkFlags.None);

        var again = See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "world"u8), 5);
        again.Flags.Should().Be(Ax25LinkFlags.Resend);
        again.Narration.Should().Be("resends #1");

        var link = Link(Node, User);
        link.AtoB.DataFrames.Should().Be(2);
        link.AtoB.DataBytes.Should().Be(10);
        link.AtoB.Resends.Should().Be(1);
        link.AtoB.AwaitingAck.Should().Be(0);
    }

    [Fact]
    public void A_Jump_In_Sequence_Is_Frames_The_Observer_Missed_Not_A_Resend()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8), 2);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "b"u8), 3);

        var gap = See(Ax25Frame.I(Node, User, nr: 0, ns: 4, "e"u8), 4);
        gap.Flags.Should().Be(Ax25LinkFlags.Missed);
        gap.Narration.Should().Be("sends #4 (missed #2-#3)");

        var next = See(Ax25Frame.I(Node, User, nr: 0, ns: 5, "f"u8), 5);
        next.Flags.Should().Be(Ax25LinkFlags.None);
    }

    [Fact]
    public void Sequence_Numbers_Wrap_At_The_Modulo()
    {
        Connect();
        for (byte ns = 0; ns < 8; ns++)
        {
            See(Ax25Frame.I(Node, User, nr: 0, ns: ns, "x"u8), 2 + ns);
        }

        var wrapped = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "y"u8), 20);
        wrapped.Flags.Should().Be(Ax25LinkFlags.None);
        wrapped.Narration.Should().Be("sends #0");

        var ack = See(Ax25Frame.Rr(User, Node, nr: 1, isCommand: false), 21);
        ack.Narration.Should().Be("acknowledges #0");
    }

    [Fact]
    public void A_Poll_After_A_Timeout_Is_The_Retry_That_Matters()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "hello"u8), 2);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "there"u8), 3);

        var poll = See(Ax25Frame.Rr(Node, User, nr: 0, isCommand: true, pollFinal: true), 10);
        poll.Flags.Should().Be(Ax25LinkFlags.Poll);
        poll.Narration.Should().Be("asks GB7RDG-2 what it has received (no acknowledgement heard for #0-#1)");
        Link(Node, User).Concern.Should().Be("M0LTE-9 timed out waiting and is polling");

        var second = See(Ax25Frame.Rr(Node, User, nr: 0, isCommand: true, pollFinal: true), 20);
        second.Flags.Should().Be(Ax25LinkFlags.Poll | Ax25LinkFlags.Repeat);
        second.Count.Should().Be(2);
        second.Narration.Should().Be("asks GB7RDG-2 again what it has received (no acknowledgement heard for #0-#1) (poll 2)");
        Link(Node, User).Concern.Should().Be("M0LTE-9 has polled 2 times with no answer");
        Link(Node, User).AtoB.Polls.Should().Be(2);

        var answer = See(Ax25Frame.Rr(User, Node, nr: 2, isCommand: false, pollFinal: true), 21);
        answer.Flags.Should().Be(Ax25LinkFlags.Final);
        answer.Narration.Should().Be("answers the poll: all received through #1");
        Link(Node, User).Concern.Should().BeNull();
        Link(Node, User).AtoB.PollsUnanswered.Should().Be(0);
        Link(Node, User).AtoB.AwaitingAck.Should().Be(0);
    }

    [Fact]
    public void A_Poll_With_Nothing_Outstanding_Is_A_Link_Check()
    {
        Connect();
        var poll = See(Ax25Frame.Rr(Node, User, nr: 0, isCommand: true, pollFinal: true), 300);
        poll.Narration.Should().Be("checks that GB7RDG-2 is still there");

        var answer = See(Ax25Frame.Rr(User, Node, nr: 0, isCommand: false, pollFinal: true), 301);
        answer.Narration.Should().Be("answers the poll: nothing received, ready");
    }

    [Fact]
    public void An_Answer_That_Is_Behind_Says_What_It_Is_Waiting_For()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8), 2);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "b"u8), 3);
        See(Ax25Frame.Rr(Node, User, nr: 0, isCommand: true, pollFinal: true), 10);

        var answer = See(Ax25Frame.Rr(User, Node, nr: 1, isCommand: false, pollFinal: true), 11);
        answer.Narration.Should().Be("answers the poll: still waiting for #1");
        Link(Node, User).AtoB.AwaitingAck.Should().Be(1);
    }

    [Fact]
    public void Reject_Asks_For_A_Resend_And_The_Resend_Is_Recognised()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8), 2);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "b"u8), 3);
        See(Ax25Frame.I(Node, User, nr: 0, ns: 2, "c"u8), 4);

        var rej = See(Ax25Frame.Rej(User, Node, nr: 1, isCommand: false), 5);
        rej.Flags.Should().Be(Ax25LinkFlags.Reject);
        rej.Narration.Should().Be("asks M0LTE-9 to resend from #1");

        var srej = See(Ax25Frame.Srej(User, Node, nr: 2, isCommand: false), 6);
        srej.Narration.Should().Be("asks M0LTE-9 to resend #2");
        Link(Node, User).BtoA.Rejects.Should().Be(2);

        var resend = See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "b"u8), 7);
        resend.Flags.Should().Be(Ax25LinkFlags.Resend);
    }

    [Fact]
    public void Busy_Is_Flagged_Until_The_Station_Is_Ready_Again()
    {
        Connect();
        See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8), 2);

        var busy = See(Ax25Frame.Rnr(User, Node, nr: 1, isCommand: false), 3);
        busy.Flags.Should().Be(Ax25LinkFlags.Busy);
        busy.Narration.Should().Be("busy, hold on; acknowledges #0");
        Link(Node, User).Concern.Should().Be("GB7RDG-2 is busy");
        Link(Node, User).BtoA.Busy.Should().BeTrue();

        var ready = See(Ax25Frame.Rr(User, Node, nr: 1, isCommand: false), 8);
        ready.Flags.Should().Be(Ax25LinkFlags.None);
        ready.Narration.Should().Be("ready again");
        Link(Node, User).Concern.Should().BeNull();
    }

    [Fact]
    public void A_Digipeaters_Copy_Is_Not_A_Second_Call()
    {
        var viaDigi = Ax25Frame.Sabm(Node, User, digipeaters: [Digi]).ToBytes();
        var repeated = (byte[])viaDigi.Clone();
        repeated[14 + 6] |= 0x80; // H bit on the digipeater slot

        var heardDirect = _observer.Observe("1", viaDigi, T0)!;
        var heardFromDigi = _observer.Observe("1", repeated, T0.AddSeconds(1))!;

        heardDirect.Via.Should().Equal("MB7UXX");
        heardFromDigi.Via.Should().Equal("MB7UXX*");
        heardFromDigi.Flags.Should().Be(Ax25LinkFlags.Digipeated);
        heardFromDigi.Narration.Should().Be("repeated by MB7UXX");
        heardFromDigi.Count.Should().BeNull();

        var link = Link(Node, User);
        link.AtoB.Frames.Should().Be(1);
        link.AtoB.CallsUnanswered.Should().Be(1);
        link.Concern.Should().BeNull();

        // The originator trying again, direct copy first, is a real repeat.
        var again = _observer.Observe("1", viaDigi, T0.AddSeconds(10))!;
        again.Flags.Should().Be(Ax25LinkFlags.Repeat);
        again.Count.Should().Be(2);
    }

    [Fact]
    public void Only_Digipeated_Frames_Are_Checked_For_Copies()
    {
        Connect();
        var i = Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8);
        See(i, 2);
        var same = See(i, 3);
        same.Flags.Should().Be(Ax25LinkFlags.Resend);
    }

    [Fact]
    public void After_Sabme_Numbered_Frames_Read_Modulo_128()
    {
        var call = See(Ax25Frame.Sabme(Node, User));
        call.Narration.Should().Be("calls GB7RDG-2 (extended mode, modulo 128)");
        See(Ax25Frame.Ua(User, Node), 1);
        Link(Node, User).Modulo.Should().Be(128);

        // Read modulo-8, a two-octet control field would give the wrong numbers and
        // swallow the PID; read modulo-128 the text and the sequence numbers come out.
        var data = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "wide"u8, extended: true), 2);
        data.Ns.Should().Be(0);
        data.Text.Should().Be("wide");
        data.Flags.Should().Be(Ax25LinkFlags.None);

        var ack = See(Ax25Frame.Rr(User, Node, nr: 1, isCommand: false, extended: true), 3);
        ack.Nr.Should().Be(1);
        ack.Narration.Should().Be("acknowledges #0");

        var next = See(Ax25Frame.I(Node, User, nr: 0, ns: 1, "x"u8, extended: true), 4);
        next.Flags.Should().Be(Ax25LinkFlags.None);

        // A jump forward of less than half the modulo is frames we missed; modulo 8 it
        // would have wrapped and read as a resend.
        var far = See(Ax25Frame.I(Node, User, nr: 0, ns: 60, "y"u8, extended: true), 5);
        far.Flags.Should().Be(Ax25LinkFlags.Missed);
        far.Narration.Should().Be("sends #60 (missed #2-#59)");
    }

    [Fact]
    public void Numbered_Traffic_With_No_Call_Heard_Means_The_Link_Was_Already_Up()
    {
        var data = See(Ax25Frame.I(Node, User, nr: 3, ns: 5, "mid"u8));
        data.State.Should().Be(Ax25LinkState.Connected);
        data.Flags.Should().Be(Ax25LinkFlags.None);
        Link(Node, User).Inferred.Should().BeTrue();

        // Joining late, the observer does not know what came before.
        var next = See(Ax25Frame.I(Node, User, nr: 3, ns: 6, "next"u8), 1);
        next.Flags.Should().Be(Ax25LinkFlags.None);
    }

    [Fact]
    public void Acknowledgements_Alone_Mean_The_Link_Was_Already_Up()
    {
        // The station joined late and has heard no data yet, only the receiver's side of the
        // exchange: an acknowledgement, then a request to go back. Both are numbered traffic,
        // and numbered traffic means a link.
        var ack = See(Ax25Frame.Rr(Node, User, nr: 3, isCommand: false));
        ack.State.Should().Be(Ax25LinkState.Connected);
        ack.Flags.Should().Be(Ax25LinkFlags.None);
        Link(Node, User).Inferred.Should().BeTrue();

        var rej = See(Ax25Frame.Rej(Node, User, nr: 2, isCommand: false), 1);
        rej.State.Should().Be(Ax25LinkState.Connected);
        rej.Flags.Should().Be(Ax25LinkFlags.Reject);
    }

    [Fact]
    public void Traffic_Crossing_A_Hang_Up_Does_Not_Bring_The_Link_Back()
    {
        // The other side had not heard the DISC when it sent these; the UA that follows
        // finishes the hang-up. Neither frame is a disagreement and neither reopens the link.
        Connect();
        See(Ax25Frame.Disc(Node, User), 2);
        var ack = See(Ax25Frame.Rr(User, Node, nr: 0, isCommand: false), 3);
        ack.State.Should().Be(Ax25LinkState.Disconnecting);
        ack.Flags.Should().Be(Ax25LinkFlags.None);
        var data = See(Ax25Frame.I(User, Node, nr: 0, ns: 0, "late"u8), 4);
        data.State.Should().Be(Ax25LinkState.Disconnecting);
        data.Flags.Should().Be(Ax25LinkFlags.None);

        var ua = See(Ax25Frame.Ua(User, Node), 5);
        ua.State.Should().Be(Ax25LinkState.Disconnected);
        ua.Flags.Should().Be(Ax25LinkFlags.LinkDown);
    }

    [Fact]
    public void An_Acknowledgement_While_Still_Calling_Means_The_Answer_Was_Missed()
    {
        See(Ax25Frame.Sabm(Node, User));
        Link(Node, User).Concern.Should().BeNull();
        See(Ax25Frame.Sabm(Node, User), 5);
        Link(Node, User).Concern.Should().Be("M0LTE-9 has called 2 times with no answer");

        // The UA went by unheard; the node is already acknowledging data.
        var ack = See(Ax25Frame.Rr(User, Node, nr: 1, isCommand: false), 8);
        ack.State.Should().Be(Ax25LinkState.Connected);
        ack.Flags.Should().Be(Ax25LinkFlags.Unexpected);
        var link = Link(Node, User);
        link.Inferred.Should().BeTrue();
        link.Concern.Should().BeNull();
    }

    [Fact]
    public void Numbered_Traffic_On_A_Link_That_Went_Down_Is_Unexpected()
    {
        Connect();
        See(Ax25Frame.Disc(Node, User), 2);
        See(Ax25Frame.Ua(User, Node), 3);

        var stray = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, "?"u8), 4);
        stray.Flags.Should().Be(Ax25LinkFlags.Unexpected);
        stray.State.Should().Be(Ax25LinkState.Connected);

        var dm = See(Ax25Frame.Dm(User, Node, finalBit: true), 5);
        dm.Flags.Should().Be(Ax25LinkFlags.LinkDown);
        dm.Narration.Should().Be("says there is no link; link down");
    }

    [Fact]
    public void Ui_Frames_Are_Narrated_By_What_They_Are_For()
    {
        var id = See(Ax25Frame.Ui(new Callsign("ID"), Node, "GB7RDG-2 BBS in IO91"u8));
        id.Narration.Should().Be("beacon");
        id.Text.Should().Be("GB7RDG-2 BBS in IO91");
        id.State.Should().Be(Ax25LinkState.Unconnected);

        var nodes = See(Ax25Frame.Ui(new Callsign("NODES"), Node, new byte[] { 0xFF, 0x41, 0x42 }, pid: Ax25Frame.PidNetRom), 1);
        nodes.Narration.Should().Be("NET/ROM routing broadcast, 3 bytes");
        nodes.Text.Should().BeNull();

        var chat = See(Ax25Frame.Ui(User, Node, "hi tom"u8), 2);
        chat.Narration.Should().Be("unconnected");

        var binary = See(Ax25Frame.Ui(User, Node, new byte[] { 0x00, 0x01 }, pid: 0xCC), 3);
        binary.Narration.Should().Be("unconnected, 2 bytes of IP");
    }

    [Fact]
    public void Binary_Data_Frames_Say_How_Much_And_Of_What()
    {
        Connect();
        var netrom = See(Ax25Frame.I(Node, User, nr: 0, ns: 0, new byte[] { 0x00, 0x01, 0x02 }, pid: Ax25Frame.PidNetRom), 2);
        netrom.Narration.Should().Be("sends #0, 3 bytes of NET/ROM");
        netrom.Text.Should().BeNull();

        var raw = See(Ax25Frame.I(Node, User, nr: 0, ns: 1, new byte[] { 0x00, 0x01 }), 3);
        raw.Narration.Should().Be("sends #1, 2 bytes");
    }

    [Fact]
    public void Protocol_Errors_And_Odd_Frames_Are_Flagged()
    {
        Connect();
        var frmr = See(Ax25Frame.Frmr(Node, User, new byte[] { 0x01, 0x00, 0x00 }), 2);
        frmr.Flags.Should().Be(Ax25LinkFlags.ProtocolError);

        var stray = See(Ax25Frame.Ua(Node, User), 3);
        stray.Flags.Should().Be(Ax25LinkFlags.Unexpected);

        var header = Ax25Frame.Ua(Node, User).ToBytes()[..14];
        var unknown = _observer.Observe("1", header.Append((byte)0x07).ToArray(), T0.AddSeconds(4))!;
        unknown.FrameType.Should().Be(Ax25FrameType.Unknown);
        unknown.Flags.Should().Be(Ax25LinkFlags.ProtocolError);
        unknown.Narration.Should().Be("sends an unrecognised frame (control 0x07)");
    }

    [Fact]
    public void Transmitted_Frames_Are_Marked_As_Ours()
    {
        var ours = See(Ax25Frame.Sabm(User, Node), transmitted: true);
        ours.Transmitted.Should().BeTrue();
        ours.From.Should().Be(Node);
    }

    [Fact]
    public void Snapshot_Lists_Links_Most_Recent_First_With_Recent_Frames_Oldest_First()
    {
        var observer = new Ax25LinkObserver(new Ax25LinkObserverOptions { RecentPerLink = 2 });
        var other = new Callsign("G7XYZ", 1);
        observer.Observe("1", Ax25Frame.Sabm(Node, User).ToBytes(), T0);
        observer.Observe("1", Ax25Frame.Sabm(Node, other).ToBytes(), T0.AddSeconds(5));
        observer.Observe("1", Ax25Frame.Ua(User, Node).ToBytes(), T0.AddSeconds(10));
        observer.Observe("1", Ax25Frame.Disc(Node, User).ToBytes(), T0.AddSeconds(11));

        var links = observer.Snapshot();
        links.Select(l => l.A).Should().Equal(User, other);
        links[0].Recent.Should().HaveCount(2);
        links[0].Recent.Select(e => e.FrameType).Should().Equal(Ax25FrameType.Ua, Ax25FrameType.Disc);
        links[0].FirstSeen.Should().Be(T0);
        links[0].LastSeen.Should().Be(T0.AddSeconds(11));
    }

    [Fact]
    public void The_Quietest_Link_Goes_When_There_Are_Too_Many()
    {
        var observer = new Ax25LinkObserver(new Ax25LinkObserverOptions { MaxLinks = 2 });
        observer.Observe("1", Ax25Frame.Sabm(new Callsign("A1AA"), User).ToBytes(), T0);
        observer.Observe("1", Ax25Frame.Sabm(new Callsign("B1BB"), User).ToBytes(), T0.AddSeconds(1));
        observer.Observe("1", Ax25Frame.Sabm(new Callsign("A1AA"), User).ToBytes(), T0.AddSeconds(2));
        observer.Observe("1", Ax25Frame.Sabm(new Callsign("C1CC"), User).ToBytes(), T0.AddSeconds(3));

        observer.Snapshot().Select(l => l.B.Base).Should().BeEquivalentTo("A1AA", "C1CC");
    }

    [Fact]
    public void Links_Idle_Longer_Than_The_Lifetime_Are_Forgotten()
    {
        var observer = new Ax25LinkObserver(new Ax25LinkObserverOptions { Lifetime = TimeSpan.FromMinutes(10) });
        observer.Observe("1", Ax25Frame.Sabm(Node, User).ToBytes(), T0);
        observer.Observe("1", Ax25Frame.Sabm(new Callsign("G7XYZ"), User).ToBytes(), T0.AddMinutes(11));

        observer.Snapshot().Should().ContainSingle().Which.B.Base.Should().Be("G7XYZ");
    }

    [Fact]
    public void An_Unanswered_Call_Is_Given_Up_On_When_The_Wait_Runs_Out()
    {
        See(Ax25Frame.Sabm(Node, User));

        _observer.Expire(T0.AddMinutes(2)).Should().BeEmpty();
        Link(Node, User).State.Should().Be(Ax25LinkState.Calling);

        var gaveUp = _observer.Expire(T0.AddMinutes(10)).Should().ContainSingle().Subject;
        gaveUp.LinkId.Should().Be("1|GB7RDG-2<>M0LTE-9");
        gaveUp.From.Should().Be(User);
        gaveUp.To.Should().Be(Node);
        gaveUp.FrameType.Should().BeNull();
        gaveUp.Flags.Should().Be(Ax25LinkFlags.Timeout);
        gaveUp.State.Should().Be(Ax25LinkState.Disconnected);
        gaveUp.Narration.Should().Be("got no answer in 3 minutes; the call has failed");
        // Timed at the moment the wait ran out, not at whenever the clock was next looked at.
        gaveUp.At.Should().Be(T0.AddMinutes(3));

        var link = Link(Node, User);
        link.State.Should().Be(Ax25LinkState.Disconnected);
        link.Concern.Should().BeNull();
        link.AtoB.CallsUnanswered.Should().Be(0);
        link.Recent.Should().HaveCount(2).And.Subject.Last().Should().BeSameAs(gaveUp);

        // Once is enough.
        _observer.Expire(T0.AddMinutes(20)).Should().BeEmpty();
    }

    [Fact]
    public void Every_Retry_Heard_Restarts_The_Wait()
    {
        See(Ax25Frame.Sabm(Node, User));
        See(Ax25Frame.Sabm(Node, User), 100);

        _observer.Expire(T0.AddSeconds(200)).Should().BeEmpty();
        _observer.Expire(T0.AddSeconds(280)).Should().ContainSingle()
            .Which.At.Should().Be(T0.AddSeconds(280));
    }

    [Fact]
    public void An_Unanswered_Hang_Up_Takes_The_Link_Down()
    {
        Connect();
        See(Ax25Frame.Disc(Node, User), 60);

        var gaveUp = _observer.Expire(T0.AddMinutes(4)).Should().ContainSingle().Subject;
        gaveUp.From.Should().Be(User);
        gaveUp.Flags.Should().Be(Ax25LinkFlags.Timeout | Ax25LinkFlags.LinkDown);
        gaveUp.Narration.Should().Be("got no answer to the hang-up in 3 minutes; link down");
        Link(Node, User).State.Should().Be(Ax25LinkState.Disconnected);
    }

    [Fact]
    public void A_Call_After_One_Given_Up_On_Is_A_New_Call()
    {
        See(Ax25Frame.Sabm(Node, User));
        _observer.Expire(T0.AddMinutes(5));

        var again = See(Ax25Frame.Sabm(Node, User), 400);
        again.Narration.Should().Be("calls GB7RDG-2");
        again.Flags.Should().Be(Ax25LinkFlags.None);
        again.State.Should().Be(Ax25LinkState.Calling);
        Link(Node, User).AtoB.CallsUnanswered.Should().Be(1);
    }

    [Fact]
    public void Links_That_Are_Up_Or_Down_Are_Left_Alone_By_Expire()
    {
        Connect();
        See(Ax25Frame.Sabm(new Callsign("G7XYZ"), User), 5);
        See(Ax25Frame.Dm(User, new Callsign("G7XYZ"), finalBit: true), 6);
        See(Ax25Frame.Ui(new Callsign("ID"), new Callsign("2E0ABC"), "beacon"u8), 7);

        _observer.Expire(T0.AddHours(1)).Should().BeEmpty();
        Link(Node, User).State.Should().Be(Ax25LinkState.Connected);
    }

    [Fact]
    public void Expire_Forgets_Links_Past_Their_Lifetime_Too()
    {
        var observer = new Ax25LinkObserver(new Ax25LinkObserverOptions { Lifetime = TimeSpan.FromMinutes(10) });
        observer.Observe("1", Ax25Frame.Sabm(Node, User).ToBytes(), T0);

        observer.Expire(T0.AddMinutes(11)).Should().BeEmpty();
        observer.Snapshot().Should().BeEmpty();
    }

    [Theory]
    [InlineData(60, "1 minute")]
    [InlineData(90, "90 seconds")]
    [InlineData(600, "10 minutes")]
    public void The_Wait_Is_Said_As_A_Person_Would(int seconds, string said)
    {
        var observer = new Ax25LinkObserver(new Ax25LinkObserverOptions { CallTimeout = TimeSpan.FromSeconds(seconds) });
        observer.Observe("1", Ax25Frame.Sabm(Node, User).ToBytes(), T0);

        observer.Expire(T0.AddSeconds(seconds)).Should().ContainSingle()
            .Which.Narration.Should().Be($"got no answer in {said}; the call has failed");
    }

    [Fact]
    public void Observe_Is_Safe_From_Two_Threads()
    {
        var observer = new Ax25LinkObserver();
        var rx = Ax25Frame.I(Node, User, nr: 0, ns: 0, "a"u8).ToBytes();
        var tx = Ax25Frame.Rr(User, Node, nr: 1, isCommand: false).ToBytes();

        Parallel.For(0, 2000, i =>
        {
            observer.Observe("1", i % 2 == 0 ? rx : tx, T0.AddMilliseconds(i), transmitted: i % 2 == 1);
        });

        var link = observer.Snapshot().Should().ContainSingle().Subject;
        (link.AtoB.Frames + link.BtoA.Frames).Should().Be(2000);
    }
}
