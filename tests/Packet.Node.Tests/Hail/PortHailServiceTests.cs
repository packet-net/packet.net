using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hail;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Hail;

/// <summary>
/// The node's hail-over-link core (<see cref="PortHailService.HailOverLinkAsync"/>): the reply is
/// projected into a <see cref="Packet.Node.Core.Api.PortHailStatus"/> (including the derived mode
/// name), and the no-reply / link-failure outcomes map to the classified
/// <see cref="HailException"/> the API turns into 504 / 502. Driven over an in-memory link pair -
/// no port, no radio.
/// <para>
/// Plus the resident-responder retry discipline: a port whose responder cannot start logs its
/// reason ONCE (again only when the reason changes) and backs off on a widening per-port interval,
/// driven on a <see cref="FakeTimeProvider"/> with a capturing logger so the assertions are on the
/// RENDERED line, not merely that a call happened.
/// </para>
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

    // ─── Resident-responder retry discipline (reconcile seam, fake clock) ───

    private const string SdmDisabled =
        "SDM is disabled in the radio's programming - enable SDM + auto-acknowledgements with the Tait programming app";

    // A bare node config: these tests construct a host with no supervisor (nothing running) and
    // drive the resident failure/backoff seam directly, rather than racing the reconcile loop.
    private static TestConfigProvider NewConfig() =>
        new(new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" }, Ports = [] });

    [Fact]
    public void A_standing_resident_failure_is_logged_once_not_every_reconcile()
    {
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger<PortHailService>();
        var config = NewConfig();
        using var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);
        using var svc = new PortHailService(host, config, log, clock);

        // Five reconcile cycles' worth of the same refusal (the radio's SDM is off).
        for (int i = 0; i < 5; i++)
        {
            svc.NoteResidentSkipped("vhf", SdmDisabled);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        var lines = log.Messages.Where(m => m.Text.Contains("not armed", StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle("a standing fault logs on the transition, not once per cycle");
        lines[0].Level.Should().Be(LogLevel.Warning);
        lines[0].Text.Should().Be($"hail[vhf] resident responder not armed: {SdmDisabled} (retrying in 10s)");
    }

    [Fact]
    public void A_changed_resident_failure_reason_logs_again()
    {
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger<PortHailService>();
        var config = NewConfig();
        using var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);
        using var svc = new PortHailService(host, config, log, clock);

        svc.NoteResidentSkipped("vhf", SdmDisabled);
        svc.NoteResidentSkipped("vhf", SdmDisabled);
        svc.NoteResidentSkipped("vhf", "the radio stopped answering CCDI");

        var lines = log.Messages.Where(m => m.Text.Contains("not armed", StringComparison.Ordinal)).ToList();
        lines.Should().HaveCount(2, "a NEW reason is news; a repeat is not");
        lines[1].Text.Should().Be("hail[vhf] resident responder not armed: the radio stopped answering CCDI (retrying in 40s)");
    }

    [Fact]
    public void A_resident_start_fault_is_logged_once_with_its_retry_interval()
    {
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger<PortHailService>();
        var config = NewConfig();
        using var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);
        using var svc = new PortHailService(host, config, log, clock);

        svc.NoteResidentStartFailed("vhf", new IOException("serial port closed"));
        clock.Advance(TimeSpan.FromMinutes(10));
        svc.NoteResidentStartFailed("vhf", new IOException("serial port closed"));

        var lines = log.Messages.Where(m => m.Text.Contains("failed to start", StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle();
        lines[0].Level.Should().Be(LogLevel.Error);
        lines[0].Text.Should().Be("hail[vhf] resident responder failed to start (retrying in 10s)");
    }

    [Fact]
    public void A_failing_port_backs_off_and_is_retried_when_the_interval_elapses()
    {
        var clock = new FakeTimeProvider();
        var config = NewConfig();
        using var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);
        using var svc = new PortHailService(host, config, new CapturingLogger<PortHailService>(), clock);

        svc.ResidentAttemptDue("vhf").Should().BeTrue("a port with no failure history is always due");

        // First failure: retried one reconcile interval later, not on the very next cycle boundary
        // before it (and certainly not forever at 10 s once the interval has grown).
        svc.NoteResidentSkipped("vhf", SdmDisabled);
        svc.ResidentAttemptDue("vhf").Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(10));
        svc.ResidentAttemptDue("vhf").Should().BeTrue("the first retry is one reconcile interval out");

        // Second consecutive failure: the wait doubles to 20 s.
        svc.NoteResidentSkipped("vhf", SdmDisabled);
        clock.Advance(TimeSpan.FromSeconds(10));
        svc.ResidentAttemptDue("vhf").Should().BeFalse("the retry interval doubled");
        clock.Advance(TimeSpan.FromSeconds(10));
        svc.ResidentAttemptDue("vhf").Should().BeTrue();

        // A responder that finally arms clears the history, so the next fault starts fresh.
        svc.NoteResidentSkipped("vhf", SdmDisabled);
        svc.ResidentAttemptDue("vhf").Should().BeFalse();
        svc.ClearResidentFailure("vhf");
        svc.ResidentAttemptDue("vhf").Should().BeTrue();
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
