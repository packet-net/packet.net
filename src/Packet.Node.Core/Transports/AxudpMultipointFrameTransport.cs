using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Presents a multipoint AXUDP endpoint (the BPQ <c>BPQAXIP</c> analog) as a single
/// <see cref="IAx25Transport"/>: ONE UDP socket reaching MANY partners, each addressed
/// by <c>callsign → ip:port</c>, with a per-peer broadcast flag — so an
/// <c>Ax25Listener</c> runs over a mesh of AXUDP links through the exact same seam the
/// KISS / point-to-point AXUDP transports use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outbound routing.</b> Each frame the listener hands us is an AX.25 frame body;
/// its DESTINATION address (the first 7 octets) selects the peer:
/// </para>
/// <list type="bullet">
/// <item>The destination callsign matches a configured peer → the datagram goes to that
/// peer's endpoint (BPQ's <c>MAP &lt;call&gt; &lt;ip&gt; UDP &lt;port&gt;</c>).</item>
/// <item>The destination is a <em>broadcast</em> address (NODES / ID / BEACON / a UI
/// frame to any unmapped pseudo-destination — the BPQAXIP <c>BROADCAST NODES</c> /
/// <c>BROADCAST ID</c> model) → the datagram is fanned out to every peer whose
/// <c>broadcast</c> flag is set (the <c>B</c> suffix on a <c>MAP</c> line).</item>
/// <item>Otherwise (a connected-mode / addressed frame to a station we have no MAP for)
/// → a learned source endpoint is tried (we have replied to a sender before), else the
/// frame is dropped and counted. We never blindly fan a directed frame to every peer.</item>
/// </list>
/// <para>
/// <b>Inbound.</b> Datagrams are accepted from any sender on the bound port; the bare
/// AX.25 frame body is yielded straight up and the <c>Ax25Listener</c> routes it by
/// callsign. The sender endpoint is remembered per source callsign as a reply fallback,
/// but a configured peer endpoint always wins (so a peer behind NAT that we MAP
/// explicitly still gets replies at its configured address).
/// </para>
/// <para>
/// Like the point-to-point AXUDP transport this is a <em>native</em>
/// <see cref="IAx25Transport"/> only — no CSMA (<see cref="ICsmaChannelParams"/>) and no
/// TX-completion (<see cref="ITxCompletionTransport"/>); a UDP mesh has neither. The
/// 2-octet AX.25 FCS is appended on send and stripped + validated on receive inside
/// <see cref="AxudpMultipointSocket"/> (the de-facto wire form — see its remarks).
/// </para>
/// </remarks>
public sealed partial class AxudpMultipointFrameTransport : IAx25Transport
{
    private readonly AxudpMultipointSocket socket;
    private readonly TimeProvider clock;
    private readonly ILogger<AxudpMultipointFrameTransport> logger;

    // callsign → configured endpoint (the MAP table). Includes broadcast and
    // non-broadcast peers alike; the broadcast set is the subset to fan out to.
    private readonly Dictionary<Callsign, IPEndPoint> peersByCall;

    // The configured endpoints with the broadcast flag set (NODES / ID fan-out targets).
    private readonly List<IPEndPoint> broadcastEndpoints;

    // Learned source-callsign → endpoint, a reply fallback for a station we have no MAP
    // for but have heard from. A configured peer endpoint always takes precedence.
    private readonly ConcurrentDictionary<Callsign, IPEndPoint> learned = new();

    // Cutover-observability state (logging only — never gates a send/receive). Per-endpoint
    // last-sent instant so the "sent to {Call}" log is a first-contact / resume-after-silence
    // TRANSITION, not a per-frame line on a busy port. The learned set above doubles as the
    // first-time-learned guard for the inbound "learned peer endpoint" Debug log; this tracks
    // which endpoints we have already logged-as-heard so that stays a transition too.
    private readonly ConcurrentDictionary<IPEndPoint, DateTimeOffset> lastSentTo = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> heardEndpoints = new();

    // After this much silence to a peer, the next send re-logs as a resume (so a peer that
    // goes quiet then comes back is visible at the cutover, not buried as a one-time line).
    private static readonly TimeSpan SendResumeAfter = TimeSpan.FromMinutes(5);

    private int disposed;

    /// <summary>The local UDP port this transport is bound to (the one shared socket).</summary>
    public int LocalPort => socket.LocalPort;

    /// <summary>
    /// Open the multipoint AXUDP transport: bind <paramref name="localPort"/> for the one
    /// shared socket and route outbound frames by destination callsign against
    /// <paramref name="peers"/>.
    /// </summary>
    /// <param name="peers">The callsign→endpoint map (BPQ <c>MAP</c> lines); each entry's
    /// <see cref="AxudpMultipointPeerEndpoint.Broadcast"/> marks it a NODES/ID fan-out target.</param>
    /// <param name="localPort">Local UDP port to bind for the shared socket (0 = ephemeral).</param>
    /// <param name="timeProvider">Clock for stamping inbound-frame capture time (default system).</param>
    /// <param name="logger">Logger for dropped-frame diagnostics (default null logger).</param>
    public AxudpMultipointFrameTransport(
        IReadOnlyList<AxudpMultipointPeerEndpoint> peers,
        int localPort = 0,
        TimeProvider? timeProvider = null,
        ILogger<AxudpMultipointFrameTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        clock = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<AxudpMultipointFrameTransport>.Instance;

        var byCall = new Dictionary<Callsign, IPEndPoint>();
        var bcast = new List<IPEndPoint>();
        foreach (var peer in peers)
        {
            byCall[peer.Call] = peer.Endpoint;
            if (peer.Broadcast)
            {
                bcast.Add(peer.Endpoint);
            }
        }
        peersByCall = byCall;
        broadcastEndpoints = bcast;

        socket = new AxudpMultipointSocket(localPort);
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // The listener hands us the bare AX.25 frame body (no FCS). Read the destination
        // address to pick the peer; AxudpMultipointSocket.SendAsync appends the FCS.
        if (!TryReadDestination(ax25.Span, out var destination))
        {
            // Too short / unparseable destination slot — not a routable AX.25 frame. Drop.
            LogUnroutable("frame too short to read a destination address");
            return;
        }

        // 1) Direct: the destination is a configured peer (a MAP entry).
        if (peersByCall.TryGetValue(destination, out var direct))
        {
            NoteSendTo(direct, destination, broadcast: false);
            await socket.SendAsync(direct, ax25, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 2) Broadcast: NODES / ID / BEACON (and any UI to an unmapped pseudo-dest) fans
        //    out to every broadcast=true peer (BPQAXIP BROADCAST NODES / BROADCAST ID).
        if (IsBroadcastDestination(destination))
        {
            if (broadcastEndpoints.Count == 0)
            {
                LogNoBroadcastPeers(destination);
                return;
            }
            foreach (var endpoint in broadcastEndpoints)
            {
                NoteSendTo(endpoint, EndpointCall(endpoint, destination), broadcast: true);
                await socket.SendAsync(endpoint, ax25, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // 3) Learned fallback: a directed frame to a station we have no MAP for but have
        //    heard from (e.g. an ephemeral-port peer that called us first).
        if (learned.TryGetValue(destination, out var heard))
        {
            NoteSendTo(heard, destination, broadcast: false);
            await socket.SendAsync(heard, ax25, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No route — drop (don't fan a directed frame to every peer). Counted for diagnosis.
        LogNoRoute(destination);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AxudpMultipointReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;   // socket disposed out from under us (shutdown)
            }

            // Cutover observability: a per-datagram Trace (who we heard + from where) and a
            // first-time-per-endpoint Debug (the endpoint became reachable). Reading the source
            // here is cheap — we read it for the learn step below anyway. Logging never gates
            // delivery: a frame whose source slot doesn't read is still surfaced.
            bool haveSource = TryReadSource(result.RawFrame, out var source);
            LogHeard(LocalPort, haveSource ? source.ToString() : "?", result.From);
            if (heardEndpoints.TryAdd(result.From, 0))
            {
                LogLearnedEndpoint(LocalPort, haveSource ? source.ToString() : "?", result.From);
            }

            // Learn the SOURCE callsign → sender endpoint as a reply fallback (a configured
            // peer endpoint still wins on send). Best-effort: a body whose source slot
            // doesn't read is still delivered up — the listener routes by destination.
            if (haveSource && !peersByCall.ContainsKey(source))
            {
                learned[source] = result.From;
            }

            // The FCS is already stripped + validated; yield the bare body straight up.
            yield return new Ax25InboundFrame(result.RawFrame, PortId: 0, ReceivedAt: clock.GetUtcNow());
        }
    }

    // Cutover observability (logging only): emit the "sent to {Call}" Debug the FIRST time we
    // send to an endpoint, and again if it resumes after a silence — a transition, not a
    // per-frame line on a busy port. The stamp is best-effort under concurrency; a duplicated
    // transition log is harmless and far cheaper than locking the send path.
    private void NoteSendTo(IPEndPoint endpoint, Callsign call, bool broadcast)
    {
        var now = clock.GetUtcNow();
        bool transition = !lastSentTo.TryGetValue(endpoint, out var last) || now - last >= SendResumeAfter;
        lastSentTo[endpoint] = now;
        if (transition)
        {
            LogSentToPeer(LocalPort, call, endpoint, broadcast);
        }
    }

    // Reverse-resolve a configured endpoint to its callsign for the broadcast fan-out log
    // (the loop has the endpoint, not the call). Falls back to the frame destination (a
    // broadcast pseudo-dest such as NODES) when no MAP entry owns the endpoint.
    private Callsign EndpointCall(IPEndPoint endpoint, Callsign fallback)
    {
        foreach (var (call, ep) in peersByCall)
        {
            if (ep.Equals(endpoint))
            {
                return call;
            }
        }
        return fallback;
    }

    // Read the DESTINATION callsign (the first 7-octet address slot) from an AX.25 frame
    // body. Modulo-independent — addresses precede the control field — so no session
    // context is needed (unlike N(S)/N(R) on an extended link).
    private static bool TryReadDestination(ReadOnlySpan<byte> body, out Callsign destination)
    {
        destination = default;
        if (body.Length < Ax25Address.EncodedLength)
        {
            return false;
        }
        try
        {
            destination = Ax25Address.Read(body[..Ax25Address.EncodedLength]).Callsign;
            return true;
        }
        catch (ArgumentException)
        {
            return false;   // malformed destination slot
        }
    }

    // Read the SOURCE callsign (the second 7-octet address slot).
    private static bool TryReadSource(ReadOnlySpan<byte> body, out Callsign source)
    {
        source = default;
        if (body.Length < 2 * Ax25Address.EncodedLength)
        {
            return false;
        }
        try
        {
            source = Ax25Address.Read(body.Slice(Ax25Address.EncodedLength, Ax25Address.EncodedLength)).Callsign;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // The pseudo-destinations a node UI-broadcasts to — the BPQAXIP BROADCAST set. A frame
    // to one of these is not a real station to MAP; it fans out to the broadcast peers.
    private static bool IsBroadcastDestination(Callsign destination)
    {
        if (destination.Ssid != 0)
        {
            return false;
        }
        return destination.Base switch
        {
            "NODES" => true,   // NET/ROM routing broadcast
            "ID" => true,      // ID beacon
            "BEACON" => true,  // generic beacon (this node's BeaconService dest)
            "CQ" => true,      // CQ call
            _ => false,
        };
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            socket.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "AXUDP-multipoint: dropped an outbound frame — {Reason}.")]
    private partial void LogUnroutable(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AXUDP-multipoint: broadcast frame to {Destination} had no broadcast peers configured; dropped.")]
    private partial void LogNoBroadcastPeers(Callsign destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AXUDP-multipoint: no route for destination {Destination} (no MAP entry, not a broadcast, not learned); dropped.")]
    private partial void LogNoRoute(Callsign destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AXUDP-multipoint {LocalPort}: sent to {Call} at {Endpoint} (broadcast={Broadcast}).")]
    private partial void LogSentToPeer(int localPort, Callsign call, IPEndPoint endpoint, bool broadcast);

    [LoggerMessage(Level = LogLevel.Trace, Message = "AXUDP-multipoint {LocalPort}: heard {Source} from {Endpoint}.")]
    private partial void LogHeard(int localPort, string source, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AXUDP-multipoint {LocalPort}: learned peer endpoint {Source} -> {Endpoint}.")]
    private partial void LogLearnedEndpoint(int localPort, string source, IPEndPoint endpoint);
}

/// <summary>
/// One resolved multipoint-AXUDP partner: a callsign mapped to a UDP endpoint, with a
/// flag marking it a NODES/ID broadcast fan-out target. The node-host config layer
/// resolves the configured <c>host</c> to an <see cref="IPEndPoint"/> before constructing
/// the transport (the factory owns DNS), so this is the already-resolved form the
/// transport routes against.
/// </summary>
/// <param name="Call">The partner callsign (BPQ <c>MAP &lt;call&gt;</c>).</param>
/// <param name="Endpoint">The partner's resolved UDP endpoint (<c>&lt;ip&gt; UDP &lt;port&gt;</c>).</param>
/// <param name="Broadcast">Whether NODES/ID broadcasts fan out to this peer (the <c>B</c> suffix).</param>
public readonly record struct AxudpMultipointPeerEndpoint(Callsign Call, IPEndPoint Endpoint, bool Broadcast);
