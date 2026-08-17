using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Console;

/// <summary>
/// Dispatch tests for the CONSOLE surface of the per-peer AX.25 capability cache (the operator
/// view added alongside PORTS / KICK): drive the real <see cref="NodeCommandService"/> over a
/// scripted connection against a real <see cref="PeerCapabilityCache"/> seeded via the public
/// <c>RecordOutcome</c>. They lock the two load-bearing behaviours — <c>CAP</c> renders the
/// cached records read-only (no elevation), and <c>CAP CLEAR</c> forgets one only when elevated.
/// Mirrors <see cref="SysopElevationTests"/>'s harness (ScriptedConnection + RecordingSysopOps).
/// </summary>
[Trait("Category", "Node")]
public sealed class CapabilityConsoleTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    public CapabilityConsoleTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private const string Secret = "JBSWY3DPEHPK3PXP";   // a valid base32 secret
    private const string Callsign = "M0LTE-7";

    private SqliteUserStore StoreWithSysop(string scope)
    {
        var store = new SqliteUserStore(dbPath, NullLogger<SqliteUserStore>.Instance);
        store.Create(new UserRecord("sysop", "hash", scope, clock.GetUtcNow(), null));
        Assert.True(store.SetTotpSecret("sysop", Secret, Callsign));
        return store;
    }

    private string CurrentCode() => TotpService.ComputeCode(Secret, TotpService.CounterAt(clock.GetUtcNow()));

    // Build the service over the given (optional) cache; sysop wired so CAP CLEAR can elevate.
    // The logger is a seam so a test can assert the RENDERED audit line.
    private NodeCommandService BuildService(
        PeerCapabilityCache? cache, IUserStore store, bool authEnabled = true, ILogger<NodeCommandService>? logger = null)
    {
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = "M9YYY", Alias = "PDN" },
            Ports = [],
            Management = new ManagementConfig { Auth = new AuthConfig { Enabled = authEnabled } },
        });
        var ctx = new SysopContext(store, new TotpService(clock), new NoopSysopOps());
        var env = new NodeConsoleEnvironment(
            config, outboundConnector: null, netRom: null, sysop: ctx,
            applications: null, connectRouter: null, capabilities: cache);
        return new NodeCommandService(env, logger ?? NullLogger<NodeCommandService>.Instance, clock);
    }

    // A cache (sharing the test clock) seeded with two learned records on two ports.
    private PeerCapabilityCache SeededCache()
    {
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // gb7rdg:M0LTE-1 — extended dialled + succeeded; SREJ left null (never probed via XID).
        cache.RecordOutcome("gb7rdg", "M0LTE-1",
            dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);
        // gb7isw:G0XYZ-2 — extended dialled + REFUSED (degrade), and an XID that disabled SREJ.
        cache.RecordOutcome("gb7isw", "G0XYZ-2",
            dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: false);
        return cache;
    }

    [Fact]
    public async Task Cap_lists_the_seeded_records_with_no_elevation()
    {
        var cache = SeededCache();
        var svc = BuildService(cache, StoreWithSysop(AuthScopes.Operate));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["CAP", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Peer capabilities:", conn.Text, StringComparison.Ordinal);
        // Positive extended + unknown SREJ; ordered by id so gb7isw precedes gb7rdg.
        Assert.Contains("gb7rdg:M0LTE-1  v2.2  SREJ?  probed 0:00:00", conn.Text, StringComparison.Ordinal);
        // Refused extended (v2.0) + a probed-no SREJ (REJ), and a refused timestamp rendered.
        Assert.Contains("gb7isw:G0XYZ-2  v2.0  REJ  probed 0:00:00  refused 0:00:00", conn.Text, StringComparison.Ordinal);
        // Read-only: it never demanded elevation.
        Assert.DoesNotContain("Not authorised", conn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cap_with_no_cache_reports_unavailable()
    {
        var svc = BuildService(cache: null, StoreWithSysop(AuthScopes.Operate));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["CAP", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("cache not available", conn.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cap_clear_when_elevated_forgets_one_record()
    {
        var cache = SeededCache();
        var svc = BuildService(cache, StoreWithSysop(AuthScopes.Operate));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25,
            [$"SYSOP {CurrentCode()}", "CAP CLEAR gb7rdg:M0LTE-1", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Forgot capability for gb7rdg:M0LTE-1.", conn.Text, StringComparison.Ordinal);
        // The cache really lost that entry — and only that entry.
        Assert.DoesNotContain(cache.All(), r => r.PortId == "gb7rdg" && r.Peer == "M0LTE-1");
        Assert.Contains(cache.All(), r => r.PortId == "gb7isw" && r.Peer == "G0XYZ-2");
    }

    [Fact]
    public async Task Cap_clear_unelevated_is_refused_and_keeps_the_record()
    {
        var cache = SeededCache();
        var svc = BuildService(cache, StoreWithSysop(AuthScopes.Operate));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25,
            ["CAP CLEAR gb7rdg:M0LTE-1", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("Not authorised. Use SYSOP", conn.Text, StringComparison.Ordinal);
        // The gate was reached before the cache — the record survives.
        Assert.Contains(cache.All(), r => r.PortId == "gb7rdg" && r.Peer == "M0LTE-1");
    }

    [Fact]
    public async Task Cap_clear_of_an_unknown_id_when_elevated_says_so()
    {
        var cache = SeededCache();
        var svc = BuildService(cache, StoreWithSysop(AuthScopes.Operate));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25,
            [$"SYSOP {CurrentCode()}", "CAP CLEAR gb7rdg:NOBODY-9", "B"]);

        await svc.RunAsync(conn);

        Assert.Contains("No cached capability for gb7rdg:NOBODY-9.", conn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cap_clear_audit_line_names_the_issuing_connection_and_the_target()
    {
        var cache = SeededCache();
        var log = new CapturingLogger<NodeCommandService>();
        var svc = BuildService(cache, StoreWithSysop(AuthScopes.Operate), logger: log);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25,
            [$"SYSOP {CurrentCode()}", "CAP CLEAR gb7rdg:M0LTE-1", "B"]);

        await svc.RunAsync(conn);

        // The RENDERED line: the actor is the connection the command came in over, and the
        // cleared target is its own field (it used to render in the {PeerId} slot).
        var line = log.Messages.Find(m => m.Text.Contains("CAP-CLEAR", StringComparison.Ordinal));
        line.Text.Should().Be("Sysop command CAP-CLEAR (gb7rdg:M0LTE-1) run over M0LTE-7.");
    }

    /// <summary>An in-memory ILogger recording the level + RENDERED text of every entry, so the
    /// audit assertion reads exactly what an operator would see in the log.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    // A no-op privileged-operations stub — these tests exercise CAP, not SESSIONS/KICK/etc., so
    // the ops surface just needs to exist for the SysopContext.
    private sealed class NoopSysopOps : ISysopOperations
    {
        public bool SupportsReload => false;   // a pdn.db node, like every shipped one

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<SysopActionResult> KickAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(SysopActionResult.Success($"Disconnecting {sessionId}."));

        public Task<SysopActionResult> SetPortEnabledAsync(string portId, bool enabled, CancellationToken ct = default)
            => Task.FromResult(SysopActionResult.Success($"Port '{portId}'."));

        public Task<SysopActionResult> ReloadAsync(CancellationToken ct = default)
            => Task.FromResult(SysopActionResult.Success("Config reloaded."));
    }

    // Drives the command loop: each scripted line is delivered as its own CR-terminated read,
    // then EOF. Mirrors SysopElevationTests.ScriptedConnection.
    private sealed class ScriptedConnection(string peerId, NodeTransportKind kind, string[] lines)
        : INodeConnection
    {
        private readonly StringBuilder output = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int read;

        public string Text => output.ToString();
        public string PeerId => peerId;
        public NodeTransportKind TransportKind => kind;
        public Task Completion => completion.Task;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
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
