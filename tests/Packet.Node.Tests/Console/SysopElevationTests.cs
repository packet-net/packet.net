using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Console;

/// <summary>
/// End-to-end gate tests for over-RF <c>SYSOP</c> elevation (auth part 4): drive the real
/// <see cref="NodeCommandService"/> over a scripted connection with a real
/// <see cref="SqliteUserStore"/> + <see cref="TotpService"/> and a recording fake
/// <see cref="ISysopOperations"/>. These lock the load-bearing security properties — a valid
/// code elevates, a replay is rejected, an expired elevation is rejected, an unelevated or
/// under-scoped session can't run a privileged command, and the verb is inert when auth is
/// off — exercising the actual replay guard (persisted counter) and TTL (injected clock).
/// </summary>
[Trait("Category", "Node")]
public sealed class SysopElevationTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    public SysopElevationTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-sysop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private const string Secret = "JBSWY3DPEHPK3PXP";   // a valid base32 secret
    private const string Callsign = "M0LTE-7";

    // Build a store with one user (given scope) that has a TOTP credential bound to Callsign.
    private SqliteUserStore StoreWithSysop(string scope)
    {
        var store = new SqliteUserStore(dbPath, NullLogger<SqliteUserStore>.Instance);
        store.Create(new UserRecord("sysop", "hash", scope, clock.GetUtcNow(), null));
        Assert.True(store.SetTotpSecret("sysop", Secret, Callsign));
        return store;
    }

    private NodeCommandService BuildService(IUserStore store, RecordingSysopOps ops, bool authEnabled = true)
    {
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = "M9YYY", Alias = "PDN" },
            Ports = [],
            Management = new ManagementConfig { Auth = new AuthConfig { Enabled = authEnabled } },
        });
        var ctx = new SysopContext(store, new TotpService(clock), ops);
        var env = new NodeConsoleEnvironment(config, outboundConnector: null, netRom: null, sysop: ctx);
        return new NodeCommandService(env, NullLogger<NodeCommandService>.Instance, clock);
    }

    private string CurrentCode() => TotpService.ComputeCode(Secret, TotpService.CounterAt(clock.GetUtcNow()));

    [Fact]
    public async Task Valid_code_elevates_and_a_privileged_command_runs()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps { Sessions = ["gb7rdg:M0LTE-1 Connected"] };
        var svc = BuildService(store, ops);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, [$"SYSOP {CurrentCode()}", "SESSIONS", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Elevated as sysop (admin)", conn.Text, StringComparison.Ordinal);
        Assert.Contains("gb7rdg:M0LTE-1 Connected", conn.Text, StringComparison.Ordinal);
        Assert.Equal(1, ops.ListSessionsCalls);
    }

    [Fact]
    public async Task A_replayed_code_is_rejected()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops);
        var code = CurrentCode();
        // Same code twice in the same session: the first elevates + burns the counter; the
        // second presents the same code (same window ⇒ same counter, now == the persisted
        // high-water mark, not >) and must be rejected.
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, [$"SYSOP {code}", $"SYSOP {code}", "B"]);

        await svc.RunAsync(conn);

        // Exactly one elevation, and a subsequent failure.
        var elevations = CountOccurrences(conn.Text, "Elevated as sysop");
        Assert.Equal(1, elevations);
        Assert.Contains("Sysop authentication failed", conn.Text, StringComparison.Ordinal);
        // The replay guard is persisted: the store's high-water mark advanced past the code.
        Assert.NotNull(store.FindByUsername("sysop")!.LastTotpCounter);
    }

    [Fact]
    public async Task An_unelevated_session_cannot_run_a_privileged_command()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["SESSIONS", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Not authorised. Use SYSOP", conn.Text, StringComparison.Ordinal);
        Assert.Equal(0, ops.ListSessionsCalls);   // the privileged op was never reached
    }

    [Fact]
    public async Task An_expired_elevation_is_rejected()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops);
        // Advance the clock past the default 15-min TTL between the elevate and the command.
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, [$"SYSOP {CurrentCode()}", "SESSIONS", "B"])
        {
            BeforeRead = i => { if (i == 1) clock.Advance(TimeSpan.FromMinutes(16)); },
        };

        await svc.RunAsync(conn);

        Assert.Contains("Elevated as sysop", conn.Text, StringComparison.Ordinal);
        Assert.Contains("Not authorised. Use SYSOP", conn.Text, StringComparison.Ordinal);
        Assert.Equal(0, ops.ListSessionsCalls);
    }

    [Fact]
    public async Task An_under_scoped_elevation_cannot_run_an_admin_command()
    {
        // An 'operate' sysop may SESSIONS (operate) but not PORT (admin).
        var store = StoreWithSysop(AuthScopes.Operate);
        var ops = new RecordingSysopOps { Sessions = [] };
        var svc = BuildService(store, ops);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25,
            [$"SYSOP {CurrentCode()}", "SESSIONS", "PORT gb7rdg DOWN", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("No active sessions", conn.Text, StringComparison.Ordinal);   // SESSIONS allowed
        Assert.Contains("Not authorised (admin required)", conn.Text, StringComparison.Ordinal);   // PORT denied
        Assert.Equal(0, ops.SetPortCalls);
    }

    [Fact]
    public async Task A_wrong_code_does_not_elevate()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["SYSOP 000000", "SESSIONS", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Sysop authentication failed", conn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Elevated as", conn.Text, StringComparison.Ordinal);
        Assert.Equal(0, ops.ListSessionsCalls);
    }

    [Fact]
    public async Task An_unknown_callsign_is_rejected_without_an_oracle()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops);
        // A different (unenrolled) callsign connects. Same generic failure as a bad code.
        var conn = new ScriptedConnection("G0XYZ-1", NodeTransportKind.Ax25, [$"SYSOP {CurrentCode()}", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Sysop authentication failed", conn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Elevated as", conn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sysop_is_unavailable_when_auth_is_off()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps();
        var svc = BuildService(store, ops, authEnabled: false);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, [$"SYSOP {CurrentCode()}", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("not available", conn.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Elevated as", conn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telnet_requires_user_and_code_and_resolves_by_username()
    {
        var store = StoreWithSysop(AuthScopes.Admin);
        var ops = new RecordingSysopOps { Sessions = [] };
        var svc = BuildService(store, ops);
        // Telnet has no callsign: SYSOP <user> <code>. The single-arg form shows usage.
        var conn = new ScriptedConnection("127.0.0.1:5000", NodeTransportKind.Telnet,
            ["SYSOP justacode", $"SYSOP sysop {CurrentCode()}", "SESSIONS", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Usage: SYSOP <user> <code>", conn.Text, StringComparison.Ordinal);
        Assert.Contains("Elevated as sysop (admin)", conn.Text, StringComparison.Ordinal);
        Assert.Equal(1, ops.ListSessionsCalls);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    // A recording fake for the privileged operations — asserts the gate reached (or didn't
    // reach) the action, without standing up a real host/supervisor.
    private sealed class RecordingSysopOps : ISysopOperations
    {
        public IReadOnlyList<string> Sessions { get; init; } = [];
        public int ListSessionsCalls { get; private set; }
        public int KickCalls { get; private set; }
        public int SetPortCalls { get; private set; }
        public int ReloadCalls { get; private set; }

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default)
        { ListSessionsCalls++; return Task.FromResult(Sessions); }

        public Task<SysopActionResult> KickAsync(string sessionId, CancellationToken ct = default)
        { KickCalls++; return Task.FromResult(SysopActionResult.Success($"Disconnecting {sessionId}.")); }

        public Task<SysopActionResult> SetPortEnabledAsync(string portId, bool enabled, CancellationToken ct = default)
        { SetPortCalls++; return Task.FromResult(SysopActionResult.Success($"Port '{portId}' {(enabled ? "up" : "down")}.")); }

        public Task<SysopActionResult> ReloadAsync(CancellationToken ct = default)
        { ReloadCalls++; return Task.FromResult(SysopActionResult.Success("Config reloaded.")); }
    }

    // Drives the command loop: each scripted line is delivered as its own CR-terminated read
    // (modelling a user typing lines), then EOF. An optional BeforeRead hook runs before the
    // Nth read (used to advance the clock mid-session for the expiry test).
    private sealed class ScriptedConnection(string peerId, NodeTransportKind kind, string[] lines)
        : INodeConnection
    {
        private readonly StringBuilder output = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int read;

        public string Text => output.ToString();
        public Action<int>? BeforeRead { get; init; }
        public string PeerId => peerId;
        public NodeTransportKind TransportKind => kind;
        public Task Completion => completion.Task;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke(read);
            if (read >= lines.Length)
            {
                completion.TrySetResult();
                return new ValueTask<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
            }
            var bytes = Encoding.UTF8.GetBytes(lines[read] + "\r");
            read++;
            return new ValueTask<ReadOnlyMemory<byte>>(bytes);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            output.Append(Encoding.UTF8.GetString(bytes.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
