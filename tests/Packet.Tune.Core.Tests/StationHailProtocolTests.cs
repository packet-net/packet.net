using System.Runtime.CompilerServices;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The hailer/responder state machine over the in-memory link pair with a fake status
/// provider: a hail is answered with the peer's status, a hail with no responder times out,
/// an undeliverable hail reports a link failure, and — the node property — a resident
/// responder and an on-demand hailer coexist on one <see cref="FanOutTuningLink"/> without
/// stealing each other's telegrams.
/// </summary>
public class StationHailProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static StationHailerOptions FastOptions => new()
    {
        MaxAttempts = 2,
        ReplyTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task A_hail_is_answered_with_the_peer_status()
    {
        var (a, b) = InMemoryTuningLink.CreatePair();
        var provider = new FakeStatusProvider(new StationStatus
        {
            Callsign = "GB7RDG-1",
            Mode = 2,
            BitRateHz = 9600,
            Channel = "1",
            SupportedModes = [0, 2, 6],
            Capabilities = ["hail"],
            RssiOfHailDbm = -88.2,
        });
        var responder = new StationHailResponder(b, provider);
        using var responderCts = new CancellationTokenSource();
        var responderRun = responder.RunAsync(responderCts.Token);

        await using var hailer = new StationHailer(a, "M0ABC-7", FastOptions);
        var result = await hailer.HailAsync().WaitAsync(Timeout);

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be(StationHailOutcome.Answered);
        result.Status!.Callsign.Should().Be("GB7RDG-1");
        result.Status.Mode.Should().Be(2);
        result.Status.BitRateHz.Should().Be(9600);
        result.Status.Channel.Should().Be("1");
        result.Status.RssiOfHailDbm.Should().Be(-88.2);
        provider.LastHail!.RequesterCallsign.Should().Be("M0ABC-7", "the responder learns who hailed it");

        await StopAsync(responderCts, responderRun);
    }

    [Fact]
    public async Task A_hail_with_no_responder_times_out_as_no_reply()
    {
        var (a, _) = InMemoryTuningLink.CreatePair();
        await using var hailer = new StationHailer(a, "M0ABC", FastOptions);

        var result = await hailer.HailAsync().WaitAsync(Timeout);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(StationHailOutcome.NoReply);
        result.Status.Should().BeNull();
    }

    [Fact]
    public async Task An_undeliverable_hail_reports_a_link_failure()
    {
        await using var link = new SendThrowsLink();
        await using var hailer = new StationHailer(link, "M0ABC", FastOptions);

        var result = await hailer.HailAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(StationHailOutcome.LinkFailed);
        result.Detail.Should().Contain("undelivered", "the send never got a receipt");
    }

    [Fact]
    public async Task A_resident_responder_and_a_hailer_coexist_on_a_shared_link()
    {
        // Station A shares ONE side channel between a resident responder and an on-demand
        // hailer via the fan-out link; station B is the peer being hailed.
        var (a, b) = InMemoryTuningLink.CreatePair();
        await using var fanA = new FanOutTuningLink(a);

        var providerA = new FakeStatusProvider(new StationStatus { Callsign = "A-STATION", Mode = 6 });
        var responderA = new StationHailResponder(fanA, providerA);
        using var respACts = new CancellationTokenSource();
        var respARun = responderA.RunAsync(respACts.Token);

        var providerB = new FakeStatusProvider(new StationStatus { Callsign = "B-STATION", Mode = 2, BitRateHz = 9600 });
        var responderB = new StationHailResponder(b, providerB);
        using var respBCts = new CancellationTokenSource();
        var respBRun = responderB.RunAsync(respBCts.Token);

        await using var hailerA = new StationHailer(fanA, "A-STATION", FastOptions);
        var result = await hailerA.HailAsync().WaitAsync(Timeout);

        result.Success.Should().BeTrue("A's hailer got B's reply even though A's resident responder shares the link");
        result.Status!.Callsign.Should().Be("B-STATION");
        result.Status.Mode.Should().Be(2);
        providerA.LastHail.Should().BeNull("A's resident responder must not answer A's own outbound hail");

        await StopAsync(respACts, respARun);
        await StopAsync(respBCts, respBRun);
    }

    [Fact]
    public async Task FanOut_broadcasts_to_every_consumer_and_restamps_outbound_sequence()
    {
        var (a, b) = InMemoryTuningLink.CreatePair();
        await using var fan = new FanOutTuningLink(a);

        using var cts = new CancellationTokenSource();
        ITuningLink shared = fan;
        var first = CollectFirstAsync(shared, cts.Token);
        var second = CollectFirstAsync(shared, cts.Token);
        await Task.Delay(50, cts.Token); // let both subscriptions register

        await b.SendAsync(new TuningTelegram(5, TuningVerb.Hail, "PEER"));

        (await first.WaitAsync(Timeout)).Args.Should().Be("PEER");
        (await second.WaitAsync(Timeout)).Args.Should().Be("PEER", "both consumers see every inbound telegram");

        // An outbound send is re-stamped from the fan-out's own counter, not the caller's seq.
        await fan.SendAsync(new TuningTelegram(999, TuningVerb.Status, "cs:A"));
        var atPeer = await b.ReceiveAsync().GetAsyncEnumerator(cts.Token).MoveNextThenCurrentAsync();
        atPeer.Sequence.Should().NotBe(999);
        atPeer.Args.Should().Be("cs:A");

        cts.Cancel();
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        cts.Cancel();
        try
        {
            await run.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<TuningTelegram> CollectFirstAsync(ITuningLink link, CancellationToken cancellationToken)
    {
        await foreach (var telegram in link.ReceiveAsync(cancellationToken))
        {
            return telegram;
        }
        throw new InvalidOperationException("link closed before a telegram arrived");
    }

    private sealed class FakeStatusProvider(StationStatus status) : IStationStatusProvider
    {
        public StationHail? LastHail { get; private set; }

        public Task<StationStatus> GetStatusAsync(StationHail hail, CancellationToken cancellationToken = default)
        {
            LastHail = hail;
            return Task.FromResult(status);
        }
    }

    /// <summary>A link whose sends always fail delivery (and which never delivers a reply).</summary>
    private sealed class SendThrowsLink : ITuningLink
    {
        public Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default) =>
            throw new TuningLinkException("no receipt (fake)");

        public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class HailTestEnumeratorExtensions
{
    public static async Task<TuningTelegram> MoveNextThenCurrentAsync(this IAsyncEnumerator<TuningTelegram> enumerator)
    {
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        return enumerator.Current;
    }
}
