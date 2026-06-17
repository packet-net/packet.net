using Packet.Ax25.Sdl;
using Packet.Ax25.Xid;
using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// The runtime driver for the AX.25 v2.2 <strong>management data-link (MDL)</strong>
/// state machine — the XID parameter-negotiation FSM of Appendix C5
/// (figc5.1 <c>Ready</c> / figc5.2 <c>Negotiating</c>). It is the consumer of the
/// <c>MDL-NEGOTIATE Request</c> poke the data-link side emits after the UA on a
/// v2.2 connect (figc4.6), and it drives the single XID command/response
/// exchange that turns SREJ / segmentation / modulo / window / T1 / N2 from
/// config-flag-forced defaults into <em>negotiated</em> link parameters.
/// </summary>
/// <remarks>
/// <para>
/// This is a small (2-state) sibling of <see cref="Ax25Session"/>. It is driven
/// from the same generated tables (<see cref="ManagementDataLink_Ready"/> /
/// <see cref="ManagementDataLink_Negotiating"/>) through the same
/// <see cref="Ax25Session"/> / <see cref="ActionDispatcher"/> / <see cref="GuardEvaluator"/>
/// machinery the data-link uses — reusing that infrastructure rather than
/// hand-rolling a second interpreter. The MDL-specific behaviour rides on the
/// dispatcher's injectable MDL hooks (the <c>XID_command</c> builder, the
/// <c>Apply Negotiated Parameters</c> merge, the full-v2.0 <c>Set Version 2.0</c>,
/// the MDL→L3 signal sink, the TM201 timer).
/// </para>
/// <para>
/// <b>State isolation.</b> The MDL machine reads/writes <c>RC</c> and arms TM201;
/// the data-link session has its own <c>RC</c> and T1/T2/T3. To keep the two
/// retry/timer regimes from colliding, the MDL driver runs the generated tables
/// against its <em>own</em> <see cref="Ax25SessionContext"/> and timer
/// scheduler. The negotiated parameters are applied to the <em>real</em>
/// data-link context (<see cref="LinkContext"/>) — that is the whole point of
/// the exercise. <c>NM201</c> (the management retry limit) maps onto the MDL
/// context's <see cref="Ax25SessionContext.N2"/>.
/// </para>
/// <para>
/// <b>Provenance.</b> The MDL SDL pages are a deliberate prose-derived bootstrap
/// (Tom-directed; figc5.x not yet redrawn, marked <c>verification_pending</c>).
/// The figc5.3–figc5.8 per-parameter "reverts-to" subroutines are collapsed in
/// the SDL to a single <c>Apply Negotiated Parameters</c> placeholder; its
/// runtime body lives in <see cref="XidNegotiator"/>. See the
/// management-data-link YAML headers in packet-net/ax25sdl and §5.Z of docs/plan.md.
/// </para>
/// </remarks>
public sealed class Ax25ManagementDataLink
{
    private readonly Ax25Session machine;
    private readonly Ax25SessionContext mdlContext;
    private readonly Ax25SessionContext linkContext;
    private readonly XidParameters? explicitOffer;
    private readonly Action<ReadOnlyMemory<byte>> responderSendFrame;

    /// <summary>
    /// Our XID offer — an explicit set if one was supplied at construction, else
    /// derived from the <em>current</em> link context (so a context mutated
    /// between construction and negotiation is reflected). Used as the "our
    /// offer" half of the §6.3.2 merge on both the initiator and responder sides,
    /// and as the payload of the XID command/response we emit.
    /// </summary>
    private XidParameters Offered => explicitOffer ?? DefaultOfferFor(linkContext);

    /// <summary>The real data-link session state the negotiated parameters are applied to.</summary>
    public Ax25SessionContext LinkContext => linkContext;

    /// <summary>Current MDL state — <c>Ready</c> or <c>Negotiating</c>.</summary>
    public string State => machine.CurrentState;

    /// <summary>
    /// Raised when the MDL machine emits a Layer-3 signal — <c>MDL-NEGOTIATE
    /// Confirm</c> or <c>MDL-ERROR Indicate (B/C/D)</c>. Subscribers run
    /// synchronously on the posting thread; keep handlers fast.
    /// </summary>
    public event EventHandler<MdlSignal>? MdlSignalEmitted;

    /// <summary>
    /// Raised after a transition of the underlying <c>management_data_link</c>
    /// machine commits — forwarded verbatim from the internal
    /// <see cref="Ax25Session.TransitionFired"/> of the MDL's state machine.
    /// The argument is the matched <see cref="TransitionSpec"/> (its <c>From</c>
    /// is <c>Ready</c>/<c>Negotiating</c>, its <c>Id</c> the codegen transition
    /// id). A pure observability hook (transition-coverage instrumentation,
    /// tracing) with the same contract as <see cref="Ax25Session.TransitionFired"/>;
    /// it adds no MDL behaviour. Handlers run synchronously on the posting thread;
    /// keep them fast and non-throwing.
    /// </summary>
    public event EventHandler<TransitionSpec>? TransitionFired;

    /// <summary>
    /// Construct an MDL driver bound to a data-link session's state.
    /// </summary>
    /// <param name="linkContext">
    /// The data-link <see cref="Ax25SessionContext"/> whose parameters the
    /// negotiation will replace (SREJ / modulo / k / N1 / T1V / N2).
    /// </param>
    /// <param name="scheduler">
    /// The timer scheduler used for TM201. May be shared with the data-link
    /// session's scheduler — TM201 is a distinct timer name, so it does not
    /// collide with T1/T2/T3 — or a dedicated one. (In production the listener
    /// shares the session's scheduler so a single <see cref="TimeProvider"/>
    /// drives everything.)
    /// </param>
    /// <param name="sendFrame">
    /// Sink for outgoing frame bytes — the MDL uses it to put the XID command on
    /// the wire. Same shape as the data-link session's frame sink (parse / drop /
    /// deliver, or modem write).
    /// </param>
    /// <param name="offered">
    /// Our offered XID parameter set (our Rx capability / preferences) — sent in
    /// the XID command and used as the "our offer" half of the §6.3.2 merge. When
    /// <c>null</c>, <see cref="DefaultOfferFor"/> derives a sensible offer from the
    /// current <paramref name="linkContext"/>.
    /// </param>
    /// <param name="nm201">
    /// Maximum number of XID-command retries (§C5.3). Defaults to the data-link
    /// N2 value of <paramref name="linkContext"/>.
    /// </param>
    public Ax25ManagementDataLink(
        Ax25SessionContext linkContext,
        ITimerScheduler scheduler,
        Action<ReadOnlyMemory<byte>> sendFrame,
        XidParameters? offered = null,
        int? nm201 = null)
    {
        ArgumentNullException.ThrowIfNull(linkContext);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(sendFrame);

        this.linkContext = linkContext;
        this.explicitOffer = offered;
        this.responderSendFrame = sendFrame;

        // The MDL machine runs against its own context so its RC / NM201 / TM201
        // bookkeeping never disturbs the live data-link session. NM201 lives in
        // this context's N2; addressing mirrors the link so the XID command is
        // built with the right Local/Remote/digipeaters.
        mdlContext = new Ax25SessionContext
        {
            Local = linkContext.Local,
            Remote = linkContext.Remote,
            Digipeaters = linkContext.Digipeaters,
            N2 = nm201 ?? linkContext.N2,
        };

        Ax25Session? selfRef = null;

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name =>
            {
                // The MDL machine only arms TM201; route its expiry to the
                // TM201_expiry event. Any other timer name is a wiring bug.
                if (name == "TM201") selfRef!.PostEvent(new Tm201Expiry());
                else throw new InvalidOperationException($"MDL driver received unexpected timer expiry '{name}'");
            },
            // The MDL machine emits no supervisory frames; a bare sink satisfies
            // the non-null contract. XID goes out via sendXidCommand instead.
            sendSFrame: _ => { },
            sendMdl: OnMdlSignal,
            sendXidCommand: tx => sendFrame(BuildXidCommand().ToBytes()),
            // Apply Negotiated Parameters: parse the peer's XID response off the
            // triggering frame and run the §6.3.2 reverts-to merge into the REAL
            // data-link context.
            applyNegotiatedParameters: ApplyNegotiatedParameters,
            // Set Version 2.0 here means the COMPLETE §1436 v2.0 default set
            // applied to the real link context (not merely IsExtended=false) —
            // the figc5.2 FRMR path draws a single "Set Version 2.0" box.
            setVersion20: _ => XidNegotiator.ApplyVersion20Defaults(linkContext));
        // NB: TM201's duration is left at the dispatcher default (3000 ms — the
        // management analogue of T1; §C5.3 gives no numeric default). We do NOT
        // seed it from linkContext.T1V here: the MDL driver is built before the
        // data-link connects, and the figc4.x establishment resets T1V (to
        // 2*SRT) afterwards, so a value captured now would be stale. The
        // negotiation outcome is independent of the retry cadence; only the
        // give-up timing depends on TM201, and 3000 ms is the spec's T1 default.

        // The standard exhaustive binding table over the MDL context: it binds
        // every Ax25Guard atom, including RC_eq_NM201 (figc5.2's TM201-expiry
        // retry-limit diamond — RC == NM201, with NM201 carried in the MDL
        // context's N2) and F_eq_1 (the figc5.2 XID-response final-bit diamond,
        // reading the MDL machine's current trigger frame).
        var bindings = Ax25SessionBindings.CreateDefault(
            mdlContext, scheduler, () => selfRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        machine = new Ax25Session(
            mdlContext, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap,
            initialState: "Ready");
        selfRef = machine;

        // Forward the MDL machine's transition-fired observability event so
        // coverage instrumentation can see the management_data_link transitions
        // (the harness subscribes here; see TwoStationHarness / TransitionCoverageTests).
        machine.TransitionFired += (_, spec) => TransitionFired?.Invoke(this, spec);
    }

    /// <summary>
    /// Start a negotiation — posts <c>MDL-NEGOTIATE Request</c>, which (from
    /// <c>Ready</c>) sends the XID command, starts TM201, and moves to
    /// <c>Negotiating</c>. This is the handler the data-link side's
    /// <see cref="MdlNegotiateRequestSignal"/> poke maps to.
    /// </summary>
    public void Negotiate() => machine.PostEvent(new MdlNegotiateRequest());

    /// <summary>
    /// Feed an inbound XID frame to the MDL machine. The frame is routed as an
    /// <see cref="XidResponseReceived"/> event — the MDL <c>Negotiating</c> state
    /// reacts only to the XID <em>response</em> (§C5.3); an XID frame arriving in
    /// <c>Ready</c> (no command outstanding) is the error-B "unexpected XID
    /// response" path. The data-link classifier produces a single
    /// <see cref="XidReceived"/>; the listener hands the frame here when the MDL
    /// owns the exchange.
    /// </summary>
    public void OnXidReceived(Ax25Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        machine.PostEvent(new XidResponseReceived(frame));
    }

    /// <summary>
    /// Feed an inbound FRMR frame to the MDL machine (figc5.2 t02): a pre-v2.2
    /// peer rejecting our XID command. From <c>Negotiating</c> this triggers the
    /// §6.3.2 ¶1 version-2.0 fallback. A FRMR arriving in <c>Ready</c> (no
    /// command outstanding) has no MDL transition and is ignored.
    /// </summary>
    public void OnFrmrReceived(Ax25Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        machine.PostEvent(new FrmrReceived(frame));
    }

    /// <summary>
    /// True while a negotiation is in progress (the MDL is in <c>Negotiating</c>,
    /// awaiting the peer's XID response / FRMR / a TM201 retry). The listener
    /// uses this to decide whether an inbound XID/FRMR belongs to the MDL or the
    /// data-link session.
    /// </summary>
    public bool IsNegotiating => machine.CurrentState == "Negotiating";

    /// <summary>
    /// Handle an inbound XID <em>command</em> as the <strong>responder</strong>:
    /// merge the command's offered parameters with our own offer per §6.3.2,
    /// apply the agreed values to our link context, and reply with an XID
    /// <em>response</em> (F=1) carrying those agreed values. A v2.2 connection is
    /// thereby made on both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the un-transcribed figc5.1 path.</b> The prose-bootstrap MDL
    /// machine encodes only the <em>initiator</em> side (Ready reacts to
    /// MDL-NEGOTIATE Request and an unexpected XID <em>response</em>); the
    /// XID-<em>command</em>-reception column of figc5.1 — the responder generating
    /// the XID response — is explicitly NOT transcribed (the YAML header flags it
    /// as figure detail awaiting the figc5.x backfill). So this responder
    /// behaviour cannot be driven from the generated tables; it is implemented
    /// directly in the runtime here, deriving the response from the same
    /// normative §6.3.2 reverts-to rules (<see cref="XidNegotiator"/>) the
    /// initiator applies. When figc5.1 is redrawn this should move onto the SDL.
    /// </para>
    /// <para>
    /// Per §6.3.2 ¶7 the responder "chooses to accept the values offered, or
    /// other acceptable values, and places these values in the XID response." We
    /// place the <em>agreed</em> (post-merge) values, which is the strongest form
    /// of "acceptable values" and guarantees both stations converge on the
    /// identical reverts-to result.
    /// </para>
    /// </remarks>
    public void RespondToXidCommand(Ax25Frame command)
    {
        ArgumentNullException.ThrowIfNull(command);

        XidParameters commandParams =
            !command.Info.IsEmpty && XidInfoField.TryParse(command.Info.Span, out var parsed)
                ? parsed
                : new XidParameters();

        // Apply the §6.3.2 merge to our link context (our offer vs theirs).
        XidNegotiator.ApplyNegotiated(linkContext, Offered, commandParams);

        // Echo the agreed values back in the response so the initiator's merge
        // (its offer vs our response) lands on the identical result.
        var agreed = DefaultOfferFor(linkContext);
        var response = Ax25Frame.Xid(
            destination: mdlContext.Remote,
            source: mdlContext.Local,
            info: XidInfoField.Encode(agreed),
            isCommand: false,
            pollFinal: true,           // F=1 — the initiator's figc5.2 F_eq_1 diamond requires it
            digipeaters: mdlContext.Digipeaters);
        responderSendFrame(response.ToBytes());
    }

    private void OnMdlSignal(MdlSignal signal) => MdlSignalEmitted?.Invoke(this, signal);

    private void ApplyNegotiatedParameters(TransitionContext tx)
    {
        // The triggering frame is the peer's XID response; parse its info field.
        // A malformed / empty info field means "no parameters offered" → the
        // merge falls through to the spec defaults per field (§4.3.3.7 ¶1024).
        var frame = tx.IncomingFrame;
        XidParameters response;
        if (frame is { } f && !f.Info.IsEmpty
            && XidInfoField.TryParse(f.Info.Span, out var parsed))
        {
            response = parsed;
        }
        else
        {
            response = new XidParameters();
        }

        XidNegotiator.ApplyNegotiated(linkContext, Offered, response);
    }

    private Ax25Frame BuildXidCommand()
        => Ax25Frame.Xid(
            destination: mdlContext.Remote,
            source: mdlContext.Local,
            info: XidInfoField.Encode(Offered),
            isCommand: true,
            pollFinal: true,           // error A ("XID command without P=1", §C5.3) implies P=1
            digipeaters: mdlContext.Digipeaters);

    /// <summary>
    /// Derive a sensible offered XID parameter set from a session context — our
    /// current modulo / SREJ capability, window k, N1, T1, N2. Used when the
    /// caller doesn't supply an explicit offer. We advertise our capability
    /// (mod-128 + SREJ when the context is extended/SREJ-enabled) so the §6.3.2
    /// merge can revert to the lesser against the peer.
    /// </summary>
    public static XidParameters DefaultOfferFor(Ax25SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new XidParameters
        {
            ClassesOfProcedures = context.HalfDuplex
                ? ClassesOfProcedures.HalfDuplexDefault
                : ClassesOfProcedures.FullDuplexCapable,
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = context.SrejEnabled ? RejectMode.SelectiveReject : RejectMode.ImplicitReject,
                Modulo128 = context.IsExtended,
                // Advertise SREJ-multiframe alongside SREJ. LinBPQ's XID responder
                // (L2Code.c ProcessXIDCommand case 3) REQUIRES the OPSREJMult bit in
                // the command or it rejects the whole XID (BadXID → FRMR) and never
                // negotiates SREJ; direwolf offers it as part of its SREJ "menu". We
                // recover any incoming SREJ regardless of the multi bit, so offering
                // it is the interoperable, harmless choice. Only meaningful when we
                // are actually offering SREJ.
                SrejMultiframe = context.SrejEnabled,
                SegmenterReassembler = context.SegmenterReassemblerEnabled,
            },
            IFieldLengthRxBits = XidParameters.OctetsToBits(context.N1),
            WindowSizeRx = context.K,
            AckTimerMillis = (int)context.T1V.TotalMilliseconds,
            Retries = context.N2,
        };
    }

    private static readonly Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap = new()
    {
        ["Ready"] = ManagementDataLink_Ready.Transitions,
        ["Negotiating"] = ManagementDataLink_Negotiating.Transitions,
    };
}
