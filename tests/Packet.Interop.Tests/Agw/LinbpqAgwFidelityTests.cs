using AwesomeAssertions;
using Packet.Agw;
using Xunit;

namespace Packet.Interop.Tests.Agw;

/// <summary>
/// AGW protocol-fidelity smoke tests against the real LinBPQ
/// container's AGW listener (AGWPORT=8000 in bpq32.cfg, mapped to
/// host 8000). Confirms our <see cref="AgwClient"/> can dial LinBPQ,
/// query metadata, register a callsign, and disconnect cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// LinBPQ's AGW listener takes a few seconds longer than HTTP to bind;
/// the test budgets enough time to absorb that.
/// </para>
/// <para>
/// Does NOT exercise connected-mode L2 (which would need a remote
/// peer to terminate the connect against — and BPQ would route it via
/// its modem, not via this same AGW listener). That's a separate test
/// once we have direwolf + kissutil bridged into net-sim, or two BPQ
/// instances in different docker namespaces talking to each other.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
public class LinbpqAgwFidelityTests
{
    private const string Host = "127.0.0.1";
    private const int    AgwPort = 8000;
    // BPQ's AGW listener binds a few seconds after HTTP goes healthy, and
    // under host-CPU contention the first round-trip to it can lag. 30 s
    // gives generous headroom; the calls return as soon as BPQ answers.
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Connects_To_Linbpq_Agw_Listener_And_Receives_Port_Info()
    {
        using var cts = new CancellationTokenSource(ConnectBudget);
        await using var client = await AgwClient.ConnectAsync(Host, AgwPort, ct: cts.Token);

        var ports = await client.GetPortInfoAsync(cts.Token);

        ports.Should().NotBeNull("LinBPQ must reply to G (AskPortInfo)");
        ports.Should().NotBeEmpty("LinBPQ has multiple PORT blocks declared (Telnet, AXIP, netsim) so the port list should be non-empty");
    }

    [Fact]
    public async Task Registers_A_Callsign_Without_Error()
    {
        using var cts = new CancellationTokenSource(ConnectBudget);
        await using var client = await AgwClient.ConnectAsync(Host, AgwPort, ct: cts.Token);

        // Async returns when the X-ack frame arrives. BPQ's status byte
        // is 0x01 (registered) vs XR's 0x00 (already registered); we
        // tolerate either — the call simply asserts the server acknowledged.
        await client.RegisterCallsignAsync("PNTEST", cts.Token);
    }

    [Fact]
    public async Task Disposes_Cleanly_Without_Hanging()
    {
        using var cts = new CancellationTokenSource(ConnectBudget);
        var client = await AgwClient.ConnectAsync(Host, AgwPort, ct: cts.Token);
        await client.DisposeAsync();
        // No assertion needed — the test passing means dispose returned
        // without timing out. Regression-guards the background loops
        // shutdown ordering.
    }
}
