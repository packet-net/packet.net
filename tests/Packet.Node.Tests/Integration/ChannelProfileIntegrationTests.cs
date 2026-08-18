using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The slow-channel profile, proven end-to-end at the node-host layer: a port
/// configured with <c>profile: slow-afsk1200</c> brings up a listener whose
/// sessions carry the profile's longer T1 (10 s) - i.e. the named, opt-in tuning
/// actually flows config → supervisor → listener → session. Without a profile (or
/// any explicit tuning) the session keeps the engine's spec default - proving the
/// profile is opt-in and strict-by-default is intact.
/// </summary>
[Trait("Category", "Node")]
public sealed class ChannelProfileIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static NodeConfig ConfigWith(string? profile, Ax25PortParams? ax25 = null) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Profile = profile,
                Ax25 = ax25,
                Transport = new KissTcpTransport { Host = "mem", Port = 1 },
            },
        ],
    };

    [Fact]
    public async Task A_slow_afsk1200_port_gives_its_sessions_the_profiles_long_T1()
    {
        var t1 = await NodeSessionT1VAsync(ConfigWith("slow-afsk1200"));
        t1.Should().Be(TimeSpan.FromMilliseconds(10000),
            "the slow-afsk1200 profile lengthens T1 to 10 s and it must reach the live session");
    }

    [Fact]
    public async Task A_port_with_no_profile_keeps_the_spec_default_T1()
    {
        var t1 = await NodeSessionT1VAsync(ConfigWith(profile: null));
        t1.Should().Be(TimeSpan.FromMilliseconds(6000),
            "no profile = the engine's spec-default T1 (2 x 3000 ms SRT) — strict by default");
    }

    [Fact]
    public async Task An_explicit_t1_overrides_the_profile()
    {
        var t1 = await NodeSessionT1VAsync(
            ConfigWith("slow-afsk1200", new Ax25PortParams { T1Ms = 4000 }));
        t1.Should().Be(TimeSpan.FromMilliseconds(4000),
            "an explicit ax25.t1Ms wins over the profile's value");
    }

    // Bring up a node with the given config over the in-memory bus, accept an inbound
    // connect, and return the node-side session's negotiated T1V.
    private static async Task<TimeSpan> NodeSessionT1VAsync(NodeConfig config)
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();

        var provider = new TestConfigProvider(config);
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        // Capture the node-side session - and its T1V - as it is accepted.
        // The read must happen HERE, synchronously inside the accept dispatch,
        // not after the banner exchange: since #327 the remote delayed-acks the
        // node's banner I-frame, which gives the node a live RTT sample and the
        // figc4.7 SRT IIR correctly adapts T1V (to 7/8 of the seed on a
        // same-process ~0 RTT). These tests verify the CONFIG PLUMBING - that
        // the configured value seeds the session - so they must observe T1V
        // before the first ack lands. At accept time the establishment path
        // (T1V := 2 x SRT seed) has run and no I-frame can have been acked yet
        // (the remote's RR needs a later inbound pump iteration).
        var nodeSession = new TaskCompletionSource<(Ax25Session Session, TimeSpan T1VAtAccept)>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.GetPort("p1")!.Listener.SessionAccepted += (_, e) => nodeSession.TrySetResult((e.Session, e.Session.Context.T1V));

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        // Generous budgets: these listener pumps run on TimeProvider.System, so the
        // test is real-time and tolerates a loaded CI runner (Wait.ForAsync's default
        // budget is timer-driven + bounded; see Wait.cs / the #47 flake analysis).
        await Wait.ForAsync(() => remote.Saw("TESTNODE"), "the node should answer the connect with its banner");

        var (session, t1VAtAccept) = await nodeSession.Task.WaitAsync(Wait.DefaultBudget);
        await Wait.ForAsync(() => session.CurrentState == "Connected", "the node session should reach Connected");
        return t1VAtAccept;
    }
}
