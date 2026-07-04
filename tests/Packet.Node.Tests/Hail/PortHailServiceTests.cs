using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Node.Core.Hail;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Hail;

/// <summary>
/// The node's hail-over-link core (<see cref="PortHailService.HailOverLinkAsync"/>): the reply is
/// projected into a <see cref="Packet.Node.Core.Api.PortHailStatus"/> (including the derived mode
/// name), and the no-reply / link-failure outcomes map to the classified
/// <see cref="HailException"/> the API turns into 504 / 502. Driven over an in-memory link pair —
/// no port, no radio.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortHailServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static StationHailerOptions FastOptions => new()
    {
        MaxAttempts = 1,
        ReplyTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task An_answered_hail_projects_the_peer_status()
    {
        var (a, b) = InMemoryLink.CreatePair();
        var provider = new FakeProvider(new StationStatus
        {
            Callsign = "GB7RDG-1",
            Mode = 8,
            BitRateHz = 300,
            Channel = "0",
            SupportedModes = [6, 8],
            Capabilities = ["hail", "tune"],
            RssiOfHailDbm = -102.5,
        });
        var responder = new StationHailResponder(b, provider);
        using var cts = new CancellationTokenSource();
        var run = responder.RunAsync(cts.Token);

        var status = await PortHailService
            .HailOverLinkAsync(a, "M0ABC-7", FastOptions, log: null, CancellationToken.None)
            .WaitAsync(Timeout);

        status.Callsign.Should().Be("GB7RDG-1");
        status.Mode.Should().Be(8);
        status.ModeName.Should().Be("300 BPSK IL2P+CRC", "the node projects the catalog name from the mode number");
        status.BitRateHz.Should().Be(300);
        status.Channel.Should().Be("0");
        status.SupportedModes.Should().Equal([6, 8]);
        status.Capabilities.Should().Equal(["hail", "tune"]);
        status.RssiOfHailDbm.Should().Be(-102.5);

        cts.Cancel();
        try
        {
            await run.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task A_hail_with_no_responder_maps_to_a_timeout_error()
    {
        var (a, _) = InMemoryLink.CreatePair();

        var act = async () => await PortHailService
            .HailOverLinkAsync(a, "M0ABC", FastOptions, log: null, CancellationToken.None)
            .WaitAsync(Timeout);

        (await act.Should().ThrowAsync<HailException>()).Which.Error.Should().Be(HailError.Timeout);
    }

    private sealed class FakeProvider(StationStatus status) : IStationStatusProvider
    {
        public Task<StationStatus> GetStatusAsync(StationHail hail, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);
    }

    private sealed class InMemoryLink : ITuningLink
    {
        private readonly Channel<TuningTelegram> outbound;
        private readonly Channel<TuningTelegram> inbound;

        private InMemoryLink(Channel<TuningTelegram> outbound, Channel<TuningTelegram> inbound)
        {
            this.outbound = outbound;
            this.inbound = inbound;
        }

        public static (InMemoryLink A, InMemoryLink B) CreatePair()
        {
            var aToB = Channel.CreateUnbounded<TuningTelegram>();
            var bToA = Channel.CreateUnbounded<TuningTelegram>();
            return (new InMemoryLink(aToB, bToA), new InMemoryLink(bToA, aToB));
        }

        public Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default) =>
            outbound.Writer.WriteAsync(telegram, cancellationToken).AsTask();

        public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var telegram in inbound.Reader.ReadAllAsync(cancellationToken))
            {
                yield return telegram;
            }
        }

        public ValueTask DisposeAsync()
        {
            outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
