using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Console;

/// <summary>
/// An <see cref="IOutboundConnector"/> that dials out on one
/// <see cref="Ax25Listener"/> - the slice-1 same-port connect-out. The console's
/// <c>Connect</c> command uses it to open an outbound session and wrap it as a
/// <see cref="Ax25NodeConnection"/> to relay against the inbound user.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ax25Listener.ConnectAsync"/> raises <c>SessionAccepted</c> on
/// success - the SAME event the node uses to start an inbound console session.
/// Without coordination, dialling OUT to a station would also start a node
/// console <em>against that station</em> (spewing our prompt at it). The
/// optional <paramref name="claim"/> lets the owner (the port supervisor) mark
/// the dialled remote as outbound for the duration of the connect so its
/// <c>SessionAccepted</c> handler skips it; the claim is released once the
/// session is established (or the connect fails).
/// </para>
/// <para>
/// Every user-initiated AX.25 dial goes through here - the console's <c>C</c>,
/// <c>POST /sessions</c> and the RHPv2 server's <c>open</c> - so the port's declared link
/// policy and the per-peer capability cache apply identically on all three.
/// </para>
/// </remarks>
public sealed class Ax25OutboundConnector : IOutboundConnector
{
    private readonly Ax25Listener listener;
    private readonly Func<Callsign, IDisposable>? claim;
    private readonly Callsign? localOverride;
    // The per-peer capability cache. Null ⇒ nothing is learned or remembered (the dial still
    // honours a declared link policy). Non-null ⇒ the dial consults PlanDial to pick the version
    // + XID probe and records the OUTCOME of a RETURNED dial, plus the silent-to-SABME negative
    // when an extended dial draws no answer at all.
    private readonly PeerCapabilityCache? cache;
    // The port's declared link policy, read per dial so a hot config edit reaches the connector
    // the supervisor built at bring-up. Null ⇒ all-auto.
    private readonly Func<PortLinkConfig?>? linkPolicy;

    public Ax25OutboundConnector(
        string portId,
        Ax25Listener listener,
        Func<Callsign, IDisposable>? claim = null,
        Callsign? localOverride = null,
        PeerCapabilityCache? cache = null,
        Func<PortLinkConfig?>? linkPolicy = null)
    {
        PortId = portId ?? throw new ArgumentNullException(nameof(portId));
        this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
        this.claim = claim;
        // Originate from an application callsign instead of the port's own (the RHPv2
        // server's open.local) - multi-callsign origination; null = the listener's MyCall.
        this.localOverride = localOverride;
        this.cache = cache;
        this.linkPolicy = linkPolicy;
    }

    /// <inheritdoc/>
    public string PortId { get; }

    /// <inheritdoc/>
    public async Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default)
    {
        // Claim the remote as outbound so the supervisor's SessionAccepted handler
        // doesn't start a console session against it. Held across ConnectAsync
        // because the listener fires SessionAccepted synchronously within it -
        // and across the v2.0 retry below, which is part of the same dial.
        var ticket = claim?.Invoke(target);
        try
        {
            var local = localOverride ?? listener.MyCall;
            var link = linkPolicy?.Invoke();

            // No cache AND nothing declared ⇒ today's exact call: the no-extended-arg overload
            // follows the listener's PreferExtendedConnect + PreConnectXidNegotiatesSrej defaults,
            // and we record nothing. Preserves every existing connector unchanged.
            if (cache is null && (link is null || link.IsDefault))
            {
                var sessionNoCache = localOverride is { } lo
                    ? await listener.ConnectAsync(target, lo, cancellationToken).ConfigureAwait(false)
                    : await listener.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
                return new Ax25NodeConnection(listener, sessionNoCache);
            }

            var peer = target.ToString();

            // The port's declared policy first, the cache second. PlanDial's miss/stale default
            // for a user CONNECT is the optimistic SABME + (moot) no-XID; a declared v20/v22 pins
            // it, and under `auto` a learned answer overrides it.
            var plan = cache?.PlanDial(PortId, peer, PeerDialPolicy.UserConnect, link)
                ?? PeerCapabilityCache.PlanWithoutCache(PeerDialPolicy.UserConnect, link);

            // Did the peer say ANYTHING to us during the dial? That, not the exception type, is
            // what separates "ignores SABME" from "refused" or "reset". The engine reports the
            // SDL's own give-up at RC == N2 as a teardown (DL-DISCONNECT-indication), which is the
            // very same signal a DM refusal produces, so keying off the exception alone would
            // conflate them. A dial to a station that emitted not one frame at us is unambiguous:
            // it is the on-air signature this whole feature exists for.
            var heard = new PeerSilenceWatch(local, target);
            void OnFrame(object? _, Ax25FrameEventArgs e) => heard.Observe(e);

            listener.FrameTraced += OnFrame;
            try
            {
                Ax25Session session;
                try
                {
                    session = await listener
                        .ConnectAsync(target, local, plan.Extended, plan.PreConnectXid, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is TimeoutException or InvalidOperationException
                    && heard.PeerSaidNothing
                    && CanDegradeToV20(plan, link, cancellationToken))
                {
                    // A SABME that drew NO answer at all - not a DM (which the engine's Ax25Spec48
                    // quirk already degrades), not an FRMR (Ax25Spec45), not a reset. AX.25 v2.2
                    // section 6.3.1 forbids the ENGINE degrading silently, so the decision belongs
                    // here, at the node's dial-policy layer, where it is a named per-port setting
                    // rather than a hidden library behaviour.
                    //
                    // Retry once as v2.0 so the operator's FIRST connect still succeeds instead of
                    // costing them a stalled attempt. Retrying on the SAME cached session is safe:
                    // the SDL reaches RC == N2 and returns to Disconnected a full T1V before
                    // ConnectAsync's (N2+1) x T1V budget expires, so a fresh DL-CONNECT-request
                    // re-enters figc4.1 Establish Data Link and sends a SABM. Proven on the wire by
                    // SilentSabmePeerDialTests.
                    //
                    // The negative ("silent to SABME") is learned only if the peer then PROVES it is
                    // on air by answering the mod-8 attempt (a UA, or even a DM refusal). Silence to
                    // both is "off air", not "v2.0 only", and must not demote a v2.2-capable peer to
                    // mod-8 for the whole re-probe window.
                    bool retryXid = cache?.PlanPreConnectXid(PortId, peer, link)
                        ?? PeerCapabilityCache.PlanPreConnectXidWithoutCache(link);

                    try
                    {
                        session = await listener
                            .ConnectAsync(target, local, extended: false, retryXid, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!heard.PeerSaidNothing)
                        {
                            cache?.RecordSilentToExtended(PortId, peer);
                        }
                    }

                    // The retry RETURNED: record what the mod-8 dial observed. dialedExtended:false
                    // leaves the silent-to-SABME negative we just wrote standing (RecordOutcome only
                    // learns a dimension the dial actually probed).
                    cache?.RecordOutcome(
                        PortId, peer,
                        dialedExtended: false, observedIsExtended: session.Context.IsExtended,
                        dialedPreConnectXid: retryXid, observedSrejEnabled: session.Context.SrejEnabled);

                    return new Ax25NodeConnection(listener, session);
                }

                // Record the OUTCOME of this RETURNED dial (plan-aware: pass what we dialled +
                // what the resulting link observed; the cache decides which dimension to learn).
                // A throw above never reaches here - no link of either version means no signal,
                // except the silent-SABME case handled in the catch.
                cache?.RecordOutcome(
                    PortId, peer,
                    dialedExtended: plan.Extended, observedIsExtended: session.Context.IsExtended,
                    dialedPreConnectXid: plan.PreConnectXid, observedSrejEnabled: session.Context.SrejEnabled);

                return new Ax25NodeConnection(listener, session);
            }
            finally
            {
                listener.FrameTraced -= OnFrame;
            }
        }
        finally
        {
            ticket?.Dispose();
        }
    }

    // Whether a failed dial may degrade to v2.0 and learn the negative: only when we actually
    // offered SABME, the port has not pinned itself to v2.2 (where silence means "off air", not
    // "ignores SABME"), and the caller has not cancelled (a cancel surfaces as
    // OperationCanceledException, but a race could still leave the token set).
    private static bool CanDegradeToV20(PeerDialPlan plan, PortLinkConfig? link, CancellationToken ct)
        => plan.Extended
        && PortLinkConfig.Resolve(link).Dial != LinkDialPreference.V22
        && !ct.IsCancellationRequested;

    /// <summary>
    /// Watches the port's frame trace for the duration of one dial and answers a single question:
    /// did the station we are dialling send us anything at all? Only a RECEIVED frame addressed to
    /// our originating callsign FROM the dialled peer counts, so third-party traffic the port
    /// happens to hear (including other traffic from that same station) can never be mistaken for
    /// an answer to us.
    /// </summary>
    private sealed class PeerSilenceWatch(Callsign local, Callsign remote)
    {
        private int fromPeer;

        /// <summary>True while the dialled peer has not addressed a single frame to us.</summary>
        public bool PeerSaidNothing => Volatile.Read(ref fromPeer) == 0;

        public void Observe(Ax25FrameEventArgs e)
        {
            if (e.Direction == FrameDirection.Received
                && e.Frame.Source.Callsign.Equals(remote)
                && e.Frame.Destination.Callsign.Equals(local))
            {
                Interlocked.Increment(ref fromPeer);
            }
        }
    }
}
