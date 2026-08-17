using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Core;
using Packet.Node.Core.Beacons;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Beacons;

/// <summary>
/// The ID-beacon scheduler. Driven on a <see cref="FakeTimeProvider"/> through a
/// synthetic <see cref="IBeaconChannel"/> fake that records each transmit (destination,
/// PID, info) — no modem, no live <see cref="Packet.Ax25.Session.Ax25Listener"/>, fully
/// deterministic. Covers: enabled-arms-and-fires, disabled-is-silent (the no-regression
/// contract), per-port override beats the default, hot-reload re-arms, attach/detach
/// stops, and the default-config-transmits-nothing assertion.
/// </summary>
[Trait("Category", "Node")]
public sealed class BeaconServiceTests
{
    private static readonly Callsign Beacon = Callsign.Parse("BEACON");

    /// <summary>A synthetic <see cref="IBeaconChannel"/> that records every UI send.</summary>
    private sealed class FakeChannel(Callsign myCall) : IBeaconChannel
    {
        public List<(Callsign Dest, byte Pid, string Info)> Sent { get; } = new();
        public Callsign MyCall { get; } = myCall;

        public Task SendUiAsync(Callsign destination, ReadOnlyMemory<byte> info, byte pid = Ax25Frame.PidNoLayer3, CancellationToken ct = default)
        {
            Sent.Add((destination, pid, Encoding.UTF8.GetString(info.Span)));
            return Task.CompletedTask;
        }
    }

    private static NodeConfig Config(BeaconConfig beacon, params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1", Alias = "LONDON" },
        Ports = ports,
        Beacon = beacon,
    };

    private static PortConfig Port(string id, PortBeaconConfig? beacon = null) => new()
    {
        Id = id,
        Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 },
        Beacon = beacon,
    };

    // The FakeTimeProvider runs the timer callback synchronously on Advance; the callback
    // awaits the fake send (which completes synchronously), so by the time Advance returns
    // the send has been recorded. No real-time waits.

    [Fact]
    public async Task Enabled_beacon_fires_exactly_once_per_interval_with_BEACON_dest_and_F0_pid()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "{node} pdn node" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);

        // No beacon before the first interval elapses.
        clock.Advance(TimeSpan.FromMinutes(29));
        channel.Sent.Should().BeEmpty("the first beacon is one full interval after arming");

        // First beacon at the 30-minute mark.
        clock.Advance(TimeSpan.FromMinutes(1));
        channel.Sent.Should().ContainSingle();
        var (dest, pid, info) = channel.Sent[0];
        dest.Should().Be(Beacon);
        pid.Should().Be(Ax25Frame.PidNoLayer3);   // 0xF0
        info.Should().Be("LONDON pdn node");        // {node} = alias

        // One more interval → exactly one more.
        clock.Advance(TimeSpan.FromMinutes(30));
        channel.Sent.Should().HaveCount(2);
        clock.Advance(TimeSpan.FromMinutes(30));
        channel.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task Disabled_beacon_transmits_nothing_however_long_the_clock_runs()
    {
        var clock = new FakeTimeProvider();
        // Default BeaconConfig() is disabled — the no-regression contract.
        var cfg = new TestConfigProvider(Config(new BeaconConfig(), Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        svc.ArmedCount.Should().Be(0, "a disabled beacon arms no timer");

        clock.Advance(TimeSpan.FromHours(24));
        channel.Sent.Should().BeEmpty("a node that never beaconed must keep not beaconing");
    }

    [Fact]
    public async Task A_node_with_no_beacon_config_at_all_transmits_nothing()
    {
        // The default NodeConfig.Beacon (no per-port override) — the as-shipped shape.
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = [Port("vhf")],
            // Beacon left at its default (= new BeaconConfig(), Enabled == false).
        });
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        clock.Advance(TimeSpan.FromHours(12));

        svc.ArmedCount.Should().Be(0);
        channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Per_port_override_beats_the_default_for_interval_and_text()
    {
        var clock = new FakeTimeProvider();
        // System default: 30 min, "{node} default". Port override: 5 min, "{call} custom".
        var cfg = new TestConfigProvider(Config(
            new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "{node} default" },
            Port("vhf", new PortBeaconConfig { Enabled = true, IntervalMinutes = 5, Text = "{call} custom" })));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);

        // The override's 5-minute interval wins (not the default's 30).
        clock.Advance(TimeSpan.FromMinutes(5));
        channel.Sent.Should().ContainSingle();
        channel.Sent[0].Info.Should().Be("M0LTE-1 custom");   // {call}, the override's text
        channel.Sent[0].Dest.Should().Be(Beacon);
    }

    [Fact]
    public async Task Per_port_override_can_inherit_interval_or_text_field_by_field()
    {
        var clock = new FakeTimeProvider();
        // Override sets only enabled+interval; null Text inherits the system default text.
        var cfg = new TestConfigProvider(Config(
            new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "{node} inherited" },
            Port("vhf", new PortBeaconConfig { Enabled = true, IntervalMinutes = 10, Text = null })));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        clock.Advance(TimeSpan.FromMinutes(10));   // override interval

        channel.Sent.Should().ContainSingle();
        channel.Sent[0].Info.Should().Be("LONDON inherited");   // inherited default text, {node}=alias
    }

    [Fact]
    public async Task Per_port_override_can_silence_a_port_the_default_would_beacon()
    {
        var clock = new FakeTimeProvider();
        // System default ON; the port override forces it OFF — the flag is authoritative.
        var cfg = new TestConfigProvider(Config(
            new BeaconConfig { Enabled = true, IntervalMinutes = 5, Text = "default on" },
            Port("vhf", new PortBeaconConfig { Enabled = false })));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        svc.ArmedCount.Should().Be(0);
        clock.Advance(TimeSpan.FromHours(1));
        channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Detach_stops_further_beacons()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 10, Text = "x" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        clock.Advance(TimeSpan.FromMinutes(10));
        channel.Sent.Should().ContainSingle();

        svc.DetachPort("vhf");
        clock.Advance(TimeSpan.FromHours(2));
        channel.Sent.Should().ContainSingle("no beacon fires after detach");
    }

    [Fact]
    public async Task Reapply_after_a_config_edit_re_arms_to_the_new_interval_and_text()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "old" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        clock.Advance(TimeSpan.FromMinutes(30));
        channel.Sent.Should().ContainSingle();
        channel.Sent[0].Info.Should().Be("old");

        // Operator edits the beacon: faster interval + new text. The host swaps the config
        // and calls Reapply (a beacon-only edit is a no-op for the port supervisor).
        cfg.Apply(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 5, Text = "new" }, Port("vhf")));
        svc.Reapply();

        // The new 5-minute interval is now in force (the old 30 would not fire yet).
        clock.Advance(TimeSpan.FromMinutes(5));
        channel.Sent.Should().HaveCount(2);
        channel.Sent[1].Info.Should().Be("new");
    }

    [Fact]
    public async Task Reapply_with_an_unchanged_beacon_preserves_the_phase()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "id" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);

        // 29 minutes into the 30-minute cycle, an unrelated config edit (a KISS tweak on
        // another port) lands and the host calls Reapply. The beacon itself is unchanged.
        clock.Advance(TimeSpan.FromMinutes(29));
        svc.Reapply();

        // The phase survived: the beacon still fires at its original 30-minute mark, not
        // 30 minutes after the reconcile.
        clock.Advance(TimeSpan.FromMinutes(1));
        channel.Sent.Should().ContainSingle("re-applying an unchanged beacon must not push the due time out");
        channel.Sent[0].Info.Should().Be("id");
        svc.ArmedCount.Should().Be(1);
    }

    [Fact]
    public async Task Reapply_with_only_the_text_changed_swaps_the_text_without_resetting_the_phase()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "old" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        clock.Advance(TimeSpan.FromMinutes(29));

        // Same interval, new text: the timer keeps running, the text is swapped in place.
        cfg.Apply(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "new" }, Port("vhf")));
        svc.Reapply();

        clock.Advance(TimeSpan.FromMinutes(1));
        channel.Sent.Should().ContainSingle("a text-only edit keeps the schedule");
        channel.Sent[0].Info.Should().Be("new");
    }

    [Fact]
    public async Task Repeated_Reapply_never_starves_a_beacon()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "id" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);

        // An operator editing config every 20 minutes used to reset the 30-minute beacon
        // every time, so it never transmitted. Three edits across an hour must not stop it.
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(20));
            svc.Reapply();
        }

        channel.Sent.Should().HaveCount(2, "60 minutes of a 30-minute beacon is two transmissions");
    }

    [Fact]
    public async Task Reapply_can_turn_a_beacon_off_live()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 10, Text = "x" }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        svc.ArmedCount.Should().Be(1);

        cfg.Apply(Config(new BeaconConfig { Enabled = false }, Port("vhf")));
        svc.Reapply();

        svc.ArmedCount.Should().Be(0);
        clock.Advance(TimeSpan.FromHours(1));
        channel.Sent.Should().BeEmpty("the beacon was turned off");
    }

    [Fact]
    public async Task Reapply_can_turn_a_beacon_on_live()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(new BeaconConfig { Enabled = false }, Port("vhf")));
        await using var svc = new BeaconService(cfg, clock);
        var channel = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("vhf", channel);
        svc.ArmedCount.Should().Be(0);

        cfg.Apply(Config(new BeaconConfig { Enabled = true, IntervalMinutes = 15, Text = "now on" }, Port("vhf")));
        svc.Reapply();

        svc.ArmedCount.Should().Be(1);
        clock.Advance(TimeSpan.FromMinutes(15));
        channel.Sent.Should().ContainSingle().Which.Info.Should().Be("now on");
    }

    [Fact]
    public async Task Each_port_beacons_on_its_own_interval()
    {
        var clock = new FakeTimeProvider();
        var cfg = new TestConfigProvider(Config(
            new BeaconConfig { Enabled = true, IntervalMinutes = 10, Text = "{node}" },
            Port("a"),
            Port("b", new PortBeaconConfig { Enabled = true, IntervalMinutes = 5, Text = "{node} B" })));
        await using var svc = new BeaconService(cfg, clock);
        var chA = new FakeChannel(Callsign.Parse("M0LTE-1"));
        var chB = new FakeChannel(Callsign.Parse("M0LTE-1"));

        svc.AttachPort("a", chA);
        svc.AttachPort("b", chB);

        clock.Advance(TimeSpan.FromMinutes(5));
        chA.Sent.Should().BeEmpty("port a is on the 10-minute default");
        chB.Sent.Should().ContainSingle("port b overrides to 5 minutes");
        chB.Sent[0].Info.Should().Be("LONDON B");

        clock.Advance(TimeSpan.FromMinutes(5));   // t=10
        chA.Sent.Should().ContainSingle();
        chA.Sent[0].Info.Should().Be("LONDON");
        chB.Sent.Should().HaveCount(2);
    }
}
