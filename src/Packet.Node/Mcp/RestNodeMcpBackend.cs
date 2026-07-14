using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Packet.Mcp;

namespace Packet.Node.Mcp;

/// <summary>
/// The <see cref="INodeMcpBackend"/> the <c>pdn mcp</c> stdio entrypoint uses: an
/// HTTP client of the <b>running</b> node's loopback REST API. A <c>pdn mcp</c>
/// subcommand is a separate process and can't share the live node's in-proc state,
/// so this bridges stdio to the node over <c>127.0.0.1</c>. The read tools +
/// <c>reset_port</c>/<c>disconnect_session</c> map onto existing <c>/api/v1</c>
/// endpoints; <c>send_ui_frame</c>/<c>set_kiss_param</c> have no REST endpoint and
/// report honestly that they're SSE-transport-only. <c>decode_frame</c> is pure and
/// never reaches here. See docs/mcp-design.md.
/// </summary>
public sealed class RestNodeMcpBackend(HttpClient http) : INodeMcpBackend
{
    private readonly HttpClient http = http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- read ----

    public async Task<IReadOnlyList<McpPortStatus>> ListPortsAsync(CancellationToken ct = default)
    {
        var ports = await GetAsync<List<RestPort>>("api/v1/ports", ct).ConfigureAwait(false) ?? [];
        return ports.Select(p => new McpPortStatus(
            p.Id, p.Enabled, p.State ?? "?", p.SessionCount, p.FramesIn, p.FramesOut)).ToList();
    }

    public async Task<IReadOnlyList<McpSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
    {
        var s = await GetAsync<List<RestSession>>("api/v1/sessions", ct).ConfigureAwait(false) ?? [];
        return s.Select(x => new McpSessionInfo(
            x.Id, x.PortId, x.Peer, x.Role ?? "?", x.State ?? "?", x.Vs, x.Vr, x.Window,
            x.UptimeSeconds, x.BytesIn, x.BytesOut, x.LastActivity ?? "—")).ToList();
    }

    public async Task<IReadOnlyList<McpMonitorFrame>> RecentFramesAsync(FrameFilter filter, CancellationToken ct = default)
    {
        int limit = Math.Clamp(filter.Limit ?? 250, 1, 250);
        var frames = await GetAsync<List<RestMonitor>>("api/v1/monitor/recent?limit=250", ct).ConfigureAwait(false)
            ?? [];

        IEnumerable<RestMonitor> q = frames;
        if (!string.IsNullOrWhiteSpace(filter.Port))
        {
            q = q.Where(f => string.Equals(f.PortId, filter.Port, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter.Peer))
        {
            q = q.Where(f => string.Equals(f.Source, filter.Peer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Dest, filter.Peer, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            q = q.Where(f => string.Equals(f.Type, filter.Kind, StringComparison.OrdinalIgnoreCase));
        }
        if (filter.SinceSeconds is { } secs && secs > 0)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(secs);
            q = q.Where(f => f.Timestamp >= cutoff);
        }

        return q.TakeLast(limit)
            .Select(f => new McpMonitorFrame(
                f.Seq, f.Timestamp, f.PortId, f.Direction ?? "?", f.Source, f.Dest, f.Type ?? "?", f.Length))
            .ToList();
    }

    public async Task<McpLinkQuality> LinkQualityAsync(string remote, string? portId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        var links = await GetAsync<List<RestLink>>("api/v1/links", ct).ConfigureAwait(false) ?? [];
        var link = links.FirstOrDefault(l =>
            string.Equals(l.Peer, remote, StringComparison.OrdinalIgnoreCase)
            && (portId is null || string.Equals(l.PortId, portId, StringComparison.OrdinalIgnoreCase)));

        return link is null
            ? new McpLinkQuality(portId ?? "?", remote, 0, 0, 0, 0, 0, 0, Unknown: true)
            : new McpLinkQuality(link.PortId, link.Peer, link.SmoothedRttMs, link.Retries,
                link.RejCount, link.SrejCount, link.FramesIn, link.FramesOut, Unknown: false);
    }

    public async Task<McpNetworkTopology> NetworkTopologyAsync(CancellationToken ct = default)
    {
        var topo = await GetAsync<RestTopology>("api/v1/netrom/routes", ct).ConfigureAwait(false);
        if (topo is null)
        {
            return new McpNetworkTopology(DateTimeOffset.MinValue, [], []);
        }

        var neighbours = (topo.Neighbours ?? [])
            .Select(n => new McpNeighbour(n.Neighbour, n.Alias, n.PortId ?? "?", n.PathQuality, n.LastHeard ?? "—"))
            .ToList();
        var destinations = (topo.Destinations ?? [])
            .Select(d => new McpDestination(d.Destination, d.Alias,
                (d.Routes ?? []).Select(r => new McpRoute(r.Neighbour, r.Quality, r.Obsolescence)).ToList()))
            .ToList();
        return new McpNetworkTopology(topo.GeneratedAt, neighbours, destinations);
    }

    public async Task<IReadOnlyList<McpRigStatus>> RigStatusAsync(string? portId = null, CancellationToken ct = default)
    {
        if (portId is null)
        {
            var rigs = await GetAsync<List<RestRig>>("api/v1/rigs", ct).ConfigureAwait(false) ?? [];
            return rigs.Select(ToMcpRigStatus).ToList();
        }

        // One port's rig; 404 (no such port) becomes the empty result, matching the seam's
        // contract. A port with no rig block answers 200 attached:false and passes through.
        using var resp = await http.GetAsync(
            $"api/v1/ports/{Uri.EscapeDataString(portId)}/rig", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
        resp.EnsureSuccessStatusCode();
        var rig = await resp.Content.ReadFromJsonAsync<RestRig>(Json, ct).ConfigureAwait(false);
        return rig is null ? [] : [ToMcpRigStatus(rig)];
    }

    private static McpRigStatus ToMcpRigStatus(RestRig r) => new(
        r.PortId, r.Attached, r.Kind ?? "", r.Endpoint ?? "", r.Backend, r.Manufacturer, r.Model,
        r.Capabilities ?? [], r.ConnectionState ?? "unknown", r.FrequencyHz, r.Mode, r.PassbandHz,
        r.Transmitting, r.Meters?.Swr, r.Meters?.RfPowerWatts, r.Meters?.RfPowerRelative, r.SampledAt);

    // ---- write ----

    public Task<SendResult> SendUiFrameAsync(SendUiRequest req, McpCaller caller, CancellationToken ct = default)
        => Task.FromResult(new SendResult(false,
            "send_ui_frame is only available over the in-process SSE transport, not the stdio bridge."));

    public async Task<PortActionResult> ResetPortAsync(string portId, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portId);
        using var resp = await http.PostAsJsonAsync(
            $"api/v1/ports/{Uri.EscapeDataString(portId)}/lifecycle", new { action = "restart" }, Json, ct)
            .ConfigureAwait(false);

        return resp.StatusCode switch
        {
            HttpStatusCode.OK => new PortActionResult(true, portId, $"port '{portId}' restarted."),
            HttpStatusCode.NotFound => new PortActionResult(false, portId, $"no such port '{portId}'."),
            HttpStatusCode.Conflict => new PortActionResult(false, portId, $"port '{portId}' is disabled (enable it first)."),
            _ => new PortActionResult(false, portId, $"restart failed ({(int)resp.StatusCode})."),
        };
    }

    public async Task<SessionResult> DisconnectSessionAsync(string sessionId, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var resp = await http.DeleteAsync(
            $"api/v1/sessions/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);

        return resp.StatusCode switch
        {
            HttpStatusCode.NoContent => new SessionResult(true, sessionId, "disconnect requested."),
            HttpStatusCode.NotFound => new SessionResult(false, sessionId, "no such session."),
            _ => new SessionResult(false, sessionId, $"disconnect failed ({(int)resp.StatusCode})."),
        };
    }

    public Task<KissParamResult> SetKissParamAsync(SetKissParamRequest req, McpCaller caller, CancellationToken ct = default)
        => Task.FromResult(new KissParamResult(false, false,
            "set_kiss_param is not available over the stdio bridge (and not yet wired to the KISS modem)."));

    public async Task<RigFrequencyResult> SetRigFrequencyAsync(SetRigFrequencyRequest req, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        using var resp = await http.PostAsJsonAsync(
            $"api/v1/ports/{Uri.EscapeDataString(req.Port)}/rig/frequency",
            new { frequencyHz = req.FrequencyHz }, Json, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadFromJsonAsync<RestRigFrequency>(Json, ct).ConfigureAwait(false);
            long hz = body?.FrequencyHz ?? req.FrequencyHz;
            return new RigFrequencyResult(true, req.Port, hz, $"rig on '{req.Port}' tuned to {hz} Hz.");
        }
        return new RigFrequencyResult(false, req.Port, null,
            await RigErrorAsync(resp, req.Port, ct).ConfigureAwait(false));
    }

    public async Task<RigModeResult> SetRigModeAsync(SetRigModeRequest req, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        using var resp = await http.PostAsJsonAsync(
            $"api/v1/ports/{Uri.EscapeDataString(req.Port)}/rig/mode",
            new { mode = req.Mode, passbandHz = req.PassbandHz }, Json, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadFromJsonAsync<RestRigMode>(Json, ct).ConfigureAwait(false);
            string mode = body?.Mode ?? req.Mode;
            return new RigModeResult(true, req.Port, mode, body?.PassbandHz,
                $"rig on '{req.Port}' set to {mode}.");
        }
        return new RigModeResult(false, req.Port, null, null,
            await RigErrorAsync(resp, req.Port, ct).ConfigureAwait(false));
    }

    // The rig mutation endpoints answer 400/409 with an { error } body and a body-less 404 for
    // an unknown port — surface the server's own words where it said any.
    private static async Task<string> RigErrorAsync(HttpResponseMessage resp, string portId, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return $"no such port '{portId}'.";
        }
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<RestError>(Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(err?.Error))
            {
                return err.Error;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body — fall through to the status-code message.
        }
        return $"rig command failed ({(int)resp.StatusCode}).";
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        => await http.GetFromJsonAsync<T>(path, Json, ct).ConfigureAwait(false);

    // ---- REST wire shapes (camelCase via the node's STJ web defaults) ----

    private sealed record RestPort(string Id, bool Enabled, string? State, int SessionCount, long FramesIn, long FramesOut);

    private sealed record RestSession(
        string Id, string PortId, string Peer, string? Role, string? State,
        int Vs, int Vr, int Window, long UptimeSeconds, long BytesIn, long BytesOut, string? LastActivity);

    private sealed record RestMonitor(
        long Seq, DateTimeOffset Timestamp, string PortId, string? Direction,
        string Source, string Dest, string? Type, int Length);

    private sealed record RestLink(
        string PortId, string Peer, int SmoothedRttMs, int Retries,
        int RejCount, int SrejCount, long FramesIn, long FramesOut);

    private sealed record RestTopology(
        DateTimeOffset GeneratedAt, List<RestNeighbour>? Neighbours, List<RestDestination>? Destinations);

    private sealed record RestNeighbour(string Neighbour, string? Alias, string? PortId, int PathQuality, string? LastHeard);

    private sealed record RestDestination(string Destination, string? Alias, List<RestRoute>? Routes);

    private sealed record RestRoute(string Neighbour, int Quality, int Obsolescence);

    private sealed record RestRig(
        string PortId, bool Attached, string? Kind, string? Endpoint, string? Backend,
        string? Manufacturer, string? Model, List<string>? Capabilities, string? ConnectionState,
        long? FrequencyHz, string? Mode, int? PassbandHz, bool? Transmitting,
        RestRigMeters? Meters, DateTimeOffset? SampledAt);

    private sealed record RestRigMeters(double? Swr, double? RfPowerWatts, double? RfPowerRelative);

    private sealed record RestRigFrequency(long FrequencyHz);

    private sealed record RestRigMode(string? Mode, int? PassbandHz);

    private sealed record RestError(string? Error);
}
