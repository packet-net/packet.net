using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Tuning;
using Packet.Node.Tests.Support;
using M0LTE.Radio.Tait;
using M0LTE.Radio.Tait.Ccdi;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Tuning;

/// <summary>
/// The arm / pause / restore orchestration of <see cref="PortTuningService"/>: the part that stops
/// a live port's AX.25 listener and therefore <b>must</b> put the port back on every failure path.
/// Driven through the internal port-gateway seam (a fake port, radio and SDM link), so the whole
/// path runs with no supervisor, no listener and no hardware; the three public <c>Start…</c> verbs
/// are that same path plus a session factory, and the 404 / 400 / 409 refusals below go through the
/// public <see cref="PortTuningService.StartAsync"/> to prove the classification the API maps.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortTuningServiceTests
{
    private const string Port = "vhf-1";
    private const string Peer = "PEER1234";

    [Fact]
    public async Task Arming_pauses_the_port_before_it_touches_the_radio()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);

        var info = await h.ArmAsync(Port);

        h.Gateway.Timeline.Should().Equal(
            "pause:vhf-1", "progress:on", "sdm-probe", "link:PEER1234", "session:vhf-1", "start:vhf-1");
        port.Paused.Should().BeTrue("the session owns the paused port until it stops");
        port.Pauses.Should().Be(1);
        h.Gateway.Restores.Should().BeEmpty("a successful arm hands restore to the session");
        h.Service.Get(Port).Should().NotBeNull();
        info.PortId.Should().Be(Port);
        h.Logger.Rendered(LogLevel.Information).Should()
            .Contain("tuning[vhf-1] session armed - role=tuned peer=PEER1234 burst=7");
    }

    [Fact]
    public async Task The_sdm_enabled_check_failing_restores_the_port_exactly_once()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);
        // The radio's programming has short data messages disabled: ERROR 0/06 to the probe SDM.
        port.Radio!.SdmFailure = new TaitCcdiException("s00", new CcdiErrorMessage('0', 0x06));

        var act = async () => await h.ArmAsync(Port);

        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.BadRequest);
        thrown.Which.Message.Should().Contain("SDM is disabled");
        h.Gateway.Restores.Should().Equal("vhf-1");
        port.Paused.Should().BeFalse("the port is running again after the restore");
        h.Service.Get(Port).Should().BeNull("no session was ever registered");
    }

    [Fact]
    public async Task The_link_factory_failing_restores_the_port_exactly_once()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);
        h.Links.Failure = new TuningLinkException("the radio rejected the SDM side channel");

        var act = async () => await h.ArmAsync(Port);

        await act.Should().ThrowAsync<TuningLinkException>();
        h.Gateway.Restores.Should().Equal("vhf-1");
        port.Paused.Should().BeFalse("the port is running again after the restore");
        h.Service.Get(Port).Should().BeNull();
    }

    [Fact]
    public async Task The_session_factory_failing_restores_the_port_exactly_once()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);

        var act = async () => await h.Service.ArmSessionAsync(
            Port, Peer,
            (_, _, _) => throw new InvalidOperationException("this port's modem is not a NinoTNC after all"),
            _ => { },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Gateway.Restores.Should().Equal("vhf-1");
        port.Paused.Should().BeFalse();
        h.Service.Get(Port).Should().BeNull();
    }

    [Fact]
    public async Task A_second_start_while_a_session_is_live_is_a_conflict_that_leaves_it_alone()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);
        var live = await h.ArmAsync(Port);

        // The public verb, so the 409 the API maps is the one under test.
        var act = async () => await h.Service.StartAsync(Port, TuningRole.Tuned, Peer, burstFrames: 5);

        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.Conflict);
        port.Pauses.Should().Be(1, "the live session's port must not be paused a second time");
        port.Paused.Should().BeTrue();
        h.Gateway.Restores.Should().BeEmpty("a refused start must never restore the live session's port");
        h.Service.Get(Port)!.Info.SessionId.Should().Be(live.SessionId);
    }

    [Fact]
    public async Task Disposing_the_service_restores_every_live_sessions_port()
    {
        await using var h = new Harness();
        var vhf = h.AddPort("vhf-1");
        var uhf = h.AddPort("uhf-2");
        await h.ArmAsync("vhf-1");
        await h.ArmAsync("uhf-2");

        await h.Service.DisposeAsync();

        h.Gateway.Restores.Should().HaveCount(2).And.Contain("vhf-1").And.Contain("uhf-2");
        vhf.Paused.Should().BeFalse();
        uhf.Paused.Should().BeFalse();
        h.Service.Get("vhf-1").Should().BeNull();
        h.Service.Get("uhf-2").Should().BeNull();
    }

    [Fact]
    public async Task Stopping_a_live_session_restores_the_port()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);
        await h.ArmAsync(Port);

        (await h.Service.StopAsync(Port)).Should().BeTrue();

        h.Gateway.Restores.Should().Equal("vhf-1");
        port.Paused.Should().BeFalse("stop returns only once the port is back in service");
        h.Service.Get(Port).Should().BeNull("the slot is freed only after the restore");
    }

    [Fact]
    public async Task Stopping_a_port_with_no_session_is_a_no_op()
    {
        await using var h = new Harness();
        h.AddPort(Port);

        (await h.Service.StopAsync(Port)).Should().BeFalse();

        h.Gateway.Restores.Should().BeEmpty("an idle port must never be rebuilt");
    }

    [Fact]
    public async Task Signalling_next_without_a_session_is_a_not_found()
    {
        await using var h = new Harness();
        h.AddPort(Port);

        var act = () => h.Service.SignalNext(Port);

        act.Should().Throw<TuningStartException>()
            .Which.Error.Should().Be(TuningStartError.NotFound);
    }

    [Fact]
    public async Task Signalling_next_on_a_session_with_no_waiting_round_is_a_conflict()
    {
        await using var h = new Harness();
        h.AddPort(Port);
        await h.ArmAsync(Port);

        var act = () => h.Service.SignalNext(Port);

        act.Should().Throw<TuningStartException>()
            .Which.Error.Should().Be(TuningStartError.Conflict);
    }

    [Fact]
    public async Task A_restore_that_itself_fails_is_swallowed_and_logged()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);
        port.Radio!.SdmFailure = new TaitCcdiException("s00", new CcdiErrorMessage('0', 0x06));
        h.Gateway.RestoreFailure = new IOException("the modem serial port vanished mid-rebuild");

        var act = async () => await h.ArmAsync(Port);

        // The refusal surfaces, NOT the restore failure: a failed restore must never mask it.
        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.BadRequest);
        var failure = h.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which;
        failure.Text.Should().Be("tuning[vhf-1] PORT RESTORE FAILED");
        failure.Error.Should().BeOfType<IOException>()
            .Which.Message.Should().Be("the modem serial port vanished mid-rebuild");
    }

    [Fact]
    public async Task Starting_on_a_port_that_is_not_running_is_a_not_found()
    {
        await using var h = new Harness();

        var act = async () => await h.Service.StartAsync("no-such-port", TuningRole.Tuned, Peer, burstFrames: 5);

        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.NotFound);
        h.Gateway.Timeline.Should().BeEmpty("an unknown port is refused before anything is paused");
    }

    [Fact]
    public async Task Starting_on_a_port_with_no_tait_radio_is_a_bad_request()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port, radio: null);

        var act = async () => await h.Service.StartAsync(Port, TuningRole.Tuned, Peer, burstFrames: 5);

        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.BadRequest);
        thrown.Which.Message.Should().Contain("Tait CCDI radio");
        port.Pauses.Should().Be(0, "preflight refuses before the port is touched");
        h.Gateway.Restores.Should().BeEmpty();
    }

    [Fact]
    public async Task Starting_with_a_malformed_peer_identity_is_a_bad_request()
    {
        await using var h = new Harness();
        var port = h.AddPort(Port);

        var act = async () => await h.Service.StartAsync(Port, TuningRole.Tuned, "SHORT", burstFrames: 5);

        var thrown = await act.Should().ThrowAsync<TuningStartException>();
        thrown.Which.Error.Should().Be(TuningStartError.BadRequest);
        port.Pauses.Should().Be(0);
    }

    /// <summary>The service over fakes: a port gateway with scriptable ports, an in-memory link
    /// factory and a capturing logger. Arms sessions through the real orchestration.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public FakeTuningPortGateway Gateway { get; } = new();

        public FakeTuningLinkFactory Links { get; }

        public CapturingLogger<PortTuningService> Logger { get; } = new();

        public PortTuningService Service { get; }

        public Harness()
        {
            Links = new FakeTuningLinkFactory(Gateway);
            Service = new PortTuningService(
                Gateway,
                Links,
                new TestConfigProvider(new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } }),
                Logger);
        }

        public FakeTuningPort AddPort(string portId, FakeTuningRadio? radio) =>
            Gateway.Add(new FakeTuningPort(Gateway, portId) { Radio = radio });

        public FakeTuningPort AddPort(string portId) =>
            AddPort(portId, new FakeTuningRadio(Gateway));

        /// <summary>Arm a session the way the public verbs do: the shared orchestration plus a
        /// session factory. The factory here builds a hardware-free session rather than a
        /// NinoTNC-driven one.</summary>
        public Task<TuningSessionInfo> ArmAsync(string portId) =>
            Service.ArmSessionAsync(
                portId,
                Peer,
                (port, link, restore) =>
                {
                    Gateway.Record($"session:{port.PortId}");
                    FakeTuningSession session = null!;
                    session = new FakeTuningSession(port.PortId, link, Gateway, c => restore(session, c));
                    return Task.FromResult<IPortTuningSession>(session);
                },
                s => ((FakeTuningSession)s).Start(),
                CancellationToken.None);

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    /// <summary>The node host, faked: running ports by id, and a restore that brings a paused port
    /// back up (or fails, when scripted). Also owns the ordered timeline the arm-path assertions
    /// read.</summary>
    private sealed class FakeTuningPortGateway : ITuningPortGateway
    {
        private readonly Dictionary<string, FakeTuningPort> ports = new(StringComparer.Ordinal);
        private readonly List<string> timeline = [];
        private readonly List<string> restores = [];
        private readonly object gate = new();

        /// <summary>Every arm-path step, in the order it happened.</summary>
        public IReadOnlyList<string> Timeline { get { lock (gate) { return timeline.ToList(); } } }

        /// <summary>The ports restored, in order, one entry per restore call.</summary>
        public IReadOnlyList<string> Restores { get { lock (gate) { return restores.ToList(); } } }

        /// <summary>When set, every restore throws this (a port rebuild that fails).</summary>
        public Exception? RestoreFailure { get; set; }

        public FakeTuningPort Add(FakeTuningPort port)
        {
            ports[port.PortId] = port;
            return port;
        }

        public void Record(string step)
        {
            lock (gate)
            {
                timeline.Add(step);
            }
        }

        public ITuningPortHandle? GetPort(string portId) =>
            ports.TryGetValue(portId, out var port) ? port : null;

        public Task RestartAsync(string portId, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                timeline.Add($"restore:{portId}");
                restores.Add(portId);
            }
            if (RestoreFailure is not null)
            {
                return Task.FromException(RestoreFailure);
            }
            if (ports.TryGetValue(portId, out var port))
            {
                port.Paused = false;   // a rebuild brings the listener back up
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTuningPort(FakeTuningPortGateway owner, string portId) : ITuningPortHandle
    {
        public string PortId => portId;

        public bool HasNinoTnc { get; init; } = true;

        public FakeTuningRadio? Radio { get; init; }

        ITuningRadio? ITuningPortHandle.Radio => Radio;

        public NinoTncSerialPort? Tnc => null;

        public TaitCcdiRadio? Tait => null;

        /// <summary>Whether the port's AX.25 listener is stopped right now.</summary>
        public bool Paused { get; set; }

        /// <summary>How many times this port has been paused.</summary>
        public int Pauses { get; private set; }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            Paused = true;
            Pauses++;
            owner.Record($"pause:{portId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTuningRadio(FakeTuningPortGateway owner) : ITuningRadio
    {
        /// <summary>When set, the probe SDM throws this (e.g. the radio's ERROR 0/06).</summary>
        public Exception? SdmFailure { get; set; }

        public Task SetProgressMessagesAsync(bool enable, CancellationToken cancellationToken)
        {
            owner.Record(enable ? "progress:on" : "progress:off");
            return Task.CompletedTask;
        }

        public Task SendSdmAsync(string dataMessageId, string message, CancellationToken cancellationToken)
        {
            owner.Record("sdm-probe");
            return SdmFailure is null ? Task.CompletedTask : Task.FromException(SdmFailure);
        }
    }

    private sealed class FakeTuningLinkFactory(FakeTuningPortGateway owner) : ITuningLinkFactory
    {
        /// <summary>When set, opening the link throws this.</summary>
        public Exception? Failure { get; set; }

        public ITuningLink Create(ITuningPortHandle port, string peerSdmId, Action<string> log)
        {
            owner.Record($"link:{peerSdmId}");
            return Failure is null ? new DeadTuningLink() : throw Failure;
        }
    }

    /// <summary>A session with the real <see cref="IPortTuningSession"/> contract and none of the
    /// machinery: it owns the port until <see cref="StopAsync"/>, which restores it (idempotently)
    /// exactly as <c>PortTuningSession</c> does.</summary>
    private sealed class FakeTuningSession(
        string portId,
        ITuningLink link,
        FakeTuningPortGateway owner,
        Func<CancellationToken, ValueTask> restore) : IPortTuningSession
    {
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private int stopped;

        public string PortId => portId;

        public TuningSessionInfo Info =>
            new(sessionId, portId, TuningPreflight.RoleToWire(TuningRole.Tuned), Peer, "armed", 7,
                DateTimeOffset.UnixEpoch);

        public void Start() => owner.Record($"start:{portId}");

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }
            await link.DisposeAsync();
            await restore(CancellationToken.None);
        }

        public IDisposable Subscribe(out ChannelReader<TuningEvent> reader)
        {
            var channel = Channel.CreateUnbounded<TuningEvent>();
            channel.Writer.TryComplete();
            reader = channel.Reader;
            return new NoSubscription();
        }

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Captures RENDERED log lines: an assertion on the formatted string is what catches a
    /// <c>LoggerMessage</c> argument swap, which a persisted-value assertion never would.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<Entry> entries = [];
        private readonly object gate = new();

        public sealed record Entry(LogLevel Level, string Text, Exception? Error);

        public IReadOnlyList<Entry> Entries { get { lock (gate) { return entries.ToList(); } } }

        public List<string> Rendered(LogLevel level) =>
            Entries.Where(e => e.Level == level).Select(e => e.Text).ToList();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (gate)
            {
                entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
