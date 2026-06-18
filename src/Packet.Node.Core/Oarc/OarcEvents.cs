using System.Text.Json.Serialization;

namespace Packet.Node.Core.Oarc;

/// <summary>
/// The outbound OARC ingest event DTOs (#459) — one record per typed ingest endpoint, matching the
/// collector's models exactly (verified against <c>M0LTE/node-api</c> and probed live, 2026-06-18;
/// see <c>docs/oarc-reporting-design.md</c> §3–4). camelCase JSON via explicit
/// <see cref="JsonPropertyNameAttribute"/> — several wire names (<c>frmsSent</c>, <c>cctsIn</c>,
/// <c>l2rttMs</c>) are not the plain camelCase of the C# member, so the names are pinned, not
/// inferred. Optional (nullable) members are omitted when null (the client's
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>), so a payload carries only what we set.
/// </summary>
/// <remarks>
/// These are sent to the <b>typed</b> routes (e.g. <c>api/ingest/node-up</c>), where the server binds
/// the concrete type and computes the <c>@type</c> discriminator itself — so we send no <c>@type</c>.
/// <see cref="OarcEvent.EndpointPath"/> carries the route for the client; it is never serialised.
/// <c>time</c> is Unix epoch seconds (the server stores it as a <c>decimal?</c>); we always send it.
/// </remarks>
public abstract record OarcEvent
{
    /// <summary>The collector route this event POSTs to, relative to the configured base URL
    /// (e.g. <c>api/ingest/node-up</c>). Not part of the JSON body.</summary>
    [JsonIgnore]
    public abstract string EndpointPath { get; }
}

// ---- Node events ----

/// <summary>An <c>api/ingest/node-up</c> report — the node started (or OARC reporting was enabled).</summary>
public sealed record OarcNodeUpEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/node-up";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("nodeCall")] public required string NodeCall { get; init; }
    [JsonPropertyName("nodeAlias")] public required string NodeAlias { get; init; }
    [JsonPropertyName("locator")] public required string Locator { get; init; }
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
    [JsonPropertyName("software")] public required string Software { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
}

/// <summary>An <c>api/ingest/node-status</c> heartbeat — periodic node state + live counts.</summary>
public sealed record OarcNodeStatusEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/node-status";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("nodeCall")] public required string NodeCall { get; init; }
    [JsonPropertyName("nodeAlias")] public required string NodeAlias { get; init; }
    [JsonPropertyName("locator")] public required string Locator { get; init; }
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
    [JsonPropertyName("software")] public required string Software { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("uptimeSecs")] public long UptimeSecs { get; init; }
    [JsonPropertyName("linksIn")] public int? LinksIn { get; init; }
    [JsonPropertyName("linksOut")] public int? LinksOut { get; init; }
    [JsonPropertyName("cctsIn")] public int? CircuitsIn { get; init; }
    [JsonPropertyName("cctsOut")] public int? CircuitsOut { get; init; }
    [JsonPropertyName("l3Relayed")] public long? L3Relayed { get; init; }
}

/// <summary>An <c>api/ingest/node-down</c> report — the node is stopping or reporting was disabled.
/// <c>nodeAlias</c> is required by the collector (a probe without it 400s).</summary>
public sealed record OarcNodeDownEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/node-down";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("nodeCall")] public required string NodeCall { get; init; }
    [JsonPropertyName("nodeAlias")] public required string NodeAlias { get; init; }
    [JsonPropertyName("uptimeSecs")] public long? UptimeSecs { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("linksIn")] public int? LinksIn { get; init; }
    [JsonPropertyName("linksOut")] public int? LinksOut { get; init; }
    [JsonPropertyName("cctsIn")] public int? CircuitsIn { get; init; }
    [JsonPropertyName("cctsOut")] public int? CircuitsOut { get; init; }
    [JsonPropertyName("l3Relayed")] public long? L3Relayed { get; init; }
}

// ---- Link (L2) events ----

/// <summary>An <c>api/ingest/link-up</c> report — an AX.25 session entered connected state.
/// <c>direction</c> ∈ {<c>incoming</c> (remote-initiated uplink), <c>outgoing</c> (locally-initiated
/// downlink)}; <c>id</c> must be &gt; 0.</summary>
public sealed record OarcLinkUpEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/link-up";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("port")] public required string Port { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
}

/// <summary>An <c>api/ingest/link-status</c> report — periodic stats for a live L2 link.</summary>
public sealed record OarcLinkStatusEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/link-status";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("port")] public required string Port { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("upForSecs")] public long? UpForSecs { get; init; }
    [JsonPropertyName("frmsSent")] public long FramesSent { get; init; }
    [JsonPropertyName("frmsRcvd")] public long FramesReceived { get; init; }
    [JsonPropertyName("frmsResent")] public long FramesResent { get; init; }
    [JsonPropertyName("frmsQueued")] public long FramesQueued { get; init; }
    [JsonPropertyName("frmsQdPeak")] public long? FramesQueuedPeak { get; init; }
    [JsonPropertyName("bytesSent")] public long? BytesSent { get; init; }
    [JsonPropertyName("bytesRcvd")] public long? BytesReceived { get; init; }
    [JsonPropertyName("bpsTxMean")] public long? BpsTxMean { get; init; }
    [JsonPropertyName("bpsRxMean")] public long? BpsRxMean { get; init; }
    [JsonPropertyName("frmQMax")] public long? FrameQueueMax { get; init; }
    [JsonPropertyName("l2rttMs")] public long? L2RttMs { get; init; }
}

/// <summary>An <c>api/ingest/link-down</c> report — an L2 link was torn down (final stats).</summary>
public sealed record OarcLinkDownEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/link-down";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("port")] public required string Port { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("upForSecs")] public long? UpForSecs { get; init; }
    [JsonPropertyName("frmsSent")] public long FramesSent { get; init; }
    [JsonPropertyName("frmsRcvd")] public long FramesReceived { get; init; }
    [JsonPropertyName("frmsResent")] public long FramesResent { get; init; }
    [JsonPropertyName("frmsQueued")] public long FramesQueued { get; init; }
    [JsonPropertyName("frmsQdPeak")] public long? FramesQueuedPeak { get; init; }
    [JsonPropertyName("bytesSent")] public long? BytesSent { get; init; }
    [JsonPropertyName("bytesRcvd")] public long? BytesReceived { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

// ---- Circuit (L4 NET/ROM) events ----

/// <summary>An <c>api/ingest/circuit-up</c> report — a NET/ROM L4 circuit was established.</summary>
public sealed record OarcCircuitUpEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/circuit-up";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("service")] public int? Service { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
}

/// <summary>An <c>api/ingest/circuit-status</c> report — periodic stats for a live L4 circuit.</summary>
public sealed record OarcCircuitStatusEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/circuit-status";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("service")] public int? Service { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("segsSent")] public long SegmentsSent { get; init; }
    [JsonPropertyName("segsRcvd")] public long SegmentsReceived { get; init; }
    [JsonPropertyName("segsResent")] public long SegmentsResent { get; init; }
    [JsonPropertyName("segsQueued")] public long SegmentsQueued { get; init; }
    [JsonPropertyName("bytesSent")] public long? BytesSent { get; init; }
    [JsonPropertyName("bytesRcvd")] public long? BytesReceived { get; init; }
    [JsonPropertyName("upForSecs")] public long? UpForSecs { get; init; }
}

/// <summary>An <c>api/ingest/circuit-down</c> report — a NET/ROM L4 circuit was torn down.</summary>
public sealed record OarcCircuitDownEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/circuit-down";

    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("service")] public int? Service { get; init; }
    [JsonPropertyName("remote")] public required string Remote { get; init; }
    [JsonPropertyName("local")] public required string Local { get; init; }
    [JsonPropertyName("segsSent")] public long SegmentsSent { get; init; }
    [JsonPropertyName("segsRcvd")] public long SegmentsReceived { get; init; }
    [JsonPropertyName("segsResent")] public long SegmentsResent { get; init; }
    [JsonPropertyName("segsQueued")] public long SegmentsQueued { get; init; }
    [JsonPropertyName("bytesSent")] public long? BytesSent { get; init; }
    [JsonPropertyName("bytesRcvd")] public long? BytesReceived { get; init; }
    [JsonPropertyName("upForSecs")] public long? UpForSecs { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

// ---- L2 trace (per-frame) ----

/// <summary>One digipeater hop in an <see cref="OarcL2TraceEvent"/> path.</summary>
public sealed record OarcDigipeater
{
    [JsonPropertyName("call")] public required string Call { get; init; }
    [JsonPropertyName("rptd")] public bool? Repeated { get; init; }
}

/// <summary>
/// An <c>api/ingest/l2trace</c> report — one decoded AX.25 frame (the wire-monitor view). v1 reports
/// the <b>L2 layer only</b>; the NET/ROM L3/L4 decode is a named refinement (design §9), so those
/// fields are not modelled here. Note the trace dialect (design §3.2): <c>dirn</c> ∈
/// {<c>sent</c>,<c>rcvd</c>} (NOT incoming/outgoing); <c>l2Type</c> uses the BPQ trace vocabulary
/// (<c>C</c>=connect/SABM, <c>D</c>=disconnect/DISC, etc.); <c>ilen</c> only on I/UI; <c>icrc</c>
/// only on I; <c>info</c> only on UI; supervisory frames carry no <c>tseq</c>. The mapping that
/// honours those conditions lives in the reporter, not this DTO.
/// </summary>
public sealed record OarcL2TraceEvent : OarcEvent
{
    [JsonIgnore] public override string EndpointPath => "api/ingest/l2trace";

    [JsonPropertyName("reportFrom")] public required string ReportFrom { get; init; }
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("port")] public required string Port { get; init; }
    [JsonPropertyName("dirn")] public string? Direction { get; init; }
    [JsonPropertyName("isRF")] public bool? IsRf { get; init; }
    [JsonPropertyName("srce")] public required string Source { get; init; }
    [JsonPropertyName("dest")] public required string Destination { get; init; }
    [JsonPropertyName("ctrl")] public int Control { get; init; }
    [JsonPropertyName("l2Type")] public required string L2Type { get; init; }
    [JsonPropertyName("cr")] public required string CommandResponse { get; init; }
    [JsonPropertyName("modulo")] public int? Modulo { get; init; }
    [JsonPropertyName("digis")] public IReadOnlyList<OarcDigipeater>? Digipeaters { get; init; }
    [JsonPropertyName("rseq")] public int? ReceiveSequence { get; init; }
    [JsonPropertyName("tseq")] public int? TransmitSequence { get; init; }
    [JsonPropertyName("pf")] public string? PollFinal { get; init; }
    [JsonPropertyName("pid")] public int? Pid { get; init; }
    [JsonPropertyName("ilen")] public int? IFieldLength { get; init; }
}
