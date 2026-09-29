using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// #867: the peer's first frame to a callsign we are just starting to dial from. The pump looks
/// the (local, remote) session up, finds none, and builds one to answer the frame (the
/// pre-session XID responder, or the SABM(E) accept); meanwhile the dial, on another thread,
/// creates the session for the same pair. The pump's cache write used to overwrite the dial's
/// session, so the dial held a session that no inbound frame ever reached: its XID probe went
/// unanswered, it went on to SABME and never completed, and the link the peer's call set up on
/// the pump's session acknowledged the peer's I frames with nobody attached. Seen by DAPPS's
/// crossed-call tests on pdn 0.59.0, about one run in five.
/// </summary>
/// <remarks>
/// The interleaving is forced, not hoped for: a logger holds the pump inside the inbound path,
/// after its cache lookup missed and before it caches its own session, until the dial has
/// created the session and put its XID probe on the air.
/// </remarks>
public sealed class Ax25ListenerInboundDuringDialTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign B = new("N0BBB", 3);
    private static readonly Callsign A = new("N0AAA", 3);

    // Log events the pump raises in HandleNoCachedSession, after the lookup and before the cache write.
    private const int PreSessionXid = 5222;
    private const int InboundAccept = 5210;

    // Holds the logging thread on the first event with the given id until released.
    private sealed class HoldingLogger(int holdOn) : ILogger
    {
        private int held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == holdOn && Interlocked.Exchange(ref held, 1) == 0)
            {
                Entered.TrySetResult();
                Release.Wait(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static List<Ax25Frame> Sent(LoopbackModem modem) =>
        modem.SentFrames.SnapshotList()
            .Select(b => Ax25Frame.TryParse(b.Span, Ax25ParseOptions.Lenient, out var f) ? f : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

    private static bool IsEstablish(Ax25Frame f) => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme;

    // A's own pre-connect XID command: what a v2.2 station offers.
    private static Ax25Frame XidCommandFromA() => Ax25Frame.Xid(
        B, A,
        XidInfoField.Encode(Ax25ManagementDataLink.DefaultOfferFor(new Ax25SessionContext { Local = A, Remote = B, IsExtended = true, SrejEnabled = true })),
        isCommand: true, pollFinal: true);

    private static async Task<(Ax25Listener listener, LoopbackModem modem, HoldingLogger logger)> StationBAsync(int holdOn)
    {
        var modem = new LoopbackModem();
        var logger = new HoldingLogger(holdOn);
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall }, TimeProvider.System, logger);
        listener.AddLocalAlias(B);
        await listener.StartAsync();
        return (listener, modem, logger);
    }

    // Start B's dial while the pump is held, and wait until its probe is on the air, so the
    // dial's session exists before the pump caches its own.
    private static async Task<(Task<Ax25Session> dial, Ax25Frame probe)> DialWhileThePumpIsHeldAsync(
        Ax25Listener listener, LoopbackModem modem, HoldingLogger logger)
    {
        await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dial = listener.ConnectAsync(A, B, extended: true, preConnectXidNegotiatesSrej: true);
        await WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Xid && f.IsCommand), TimeSpan.FromSeconds(5),
            "B's dial has created its session and sent its XID probe");
        var probe = Sent(modem).First(f => f.FrameType == Ax25FrameType.Xid && f.IsCommand);
        logger.Release.Set();
        return (dial, probe);
    }

    private static async Task AssertTheDialOwnsTheOneLinkAsync(Ax25Listener listener, LoopbackModem modem, Task<Ax25Session> dial)
    {
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        session.CurrentState.Should().Be("Connected");
        listener.ActiveSessions.Where(s => s.Context.Local.Equals(B) && s.Context.Remote.Equals(A))
            .Should().ContainSingle().Which.Should().BeSameAs(session, "one session per (local, remote), and it is the dial's");
        Sent(modem).Count(IsEstablish).Should().Be(0, "the peer's call set the link up; the dial had nothing to establish (#854)");

        // The peer's data reaches whoever holds the link the dial returned, not nobody.
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AttachConsumerWithReplay((_, s) =>
        {
            if (s is DataLinkDataIndication di)
            {
                received.TrySetResult(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });
        modem.InjectInbound(Ax25Frame.I(B, A, nr: 0, ns: 0, "exchange\r"u8, extended: true));
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("exchange\r");
    }

    [Fact]
    public async Task The_peer_s_XID_arriving_as_our_dial_starts_leaves_the_dial_with_the_one_session()
    {
        // The #867 trace: A's XID probe reaches B just as B's own dial starts, then A's SABME
        // and A's answer to B's probe.
        var (listener, modem, logger) = await StationBAsync(PreSessionXid);
        await using var _ = listener;

        modem.InjectInbound(XidCommandFromA());
        var (dial, probe) = await DialWhileThePumpIsHeldAsync(listener, modem, logger);
        await WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Xid && !f.IsCommand), TimeSpan.FromSeconds(5),
            "A's XID command is answered");

        modem.InjectInbound(Ax25Frame.Sabme(B, A));
        modem.InjectInbound(Ax25Frame.Xid(B, A, probe.Info.Span, isCommand: false, pollFinal: true));

        await AssertTheDialOwnsTheOneLinkAsync(listener, modem, dial);
    }

    [Fact]
    public async Task The_peer_s_SABME_arriving_as_our_dial_starts_leaves_the_dial_with_the_one_session()
    {
        // The same race on the accept path: A's call reaches B as B's dial starts.
        var (listener, modem, logger) = await StationBAsync(InboundAccept);
        await using var _ = listener;

        modem.InjectInbound(Ax25Frame.Sabme(B, A));
        var (dial, probe) = await DialWhileThePumpIsHeldAsync(listener, modem, logger);
        await WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Ua), TimeSpan.FromSeconds(5),
            "A's call is answered");

        modem.InjectInbound(Ax25Frame.Xid(B, A, probe.Info.Span, isCommand: false, pollFinal: true));

        await AssertTheDialOwnsTheOneLinkAsync(listener, modem, dial);
    }
}
