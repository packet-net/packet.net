using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;

namespace Packet.Kiss;

/// <summary>
/// Glue between <see cref="Ax25Adapter"/> and any KISS-speaking modem
/// (<see cref="IKissModem"/>). The bridge does two things:
/// </summary>
/// <list type="bullet">
///   <item><see cref="CreateOutbound"/> — builds an <see cref="Ax25Adapter"/>
///   whose <c>sendBytes</c> callback fans out to
///   <see cref="IKissModem.SendFrameAsync"/>, so outgoing frames the
///   dispatcher emits end up on the wire.</item>
///   <item><see cref="RouteInboundToAdapter"/> — translates a typed
///   <see cref="KissInboundEvent"/> from the driver's inbound event
///   stream into an <see cref="Ax25Adapter.OnReceivedAx25Frame"/>
///   call.</item>
/// </list>
/// <remarks>
/// <para>
/// The two halves are deliberately separate — KISS driver APIs vary on
/// the inbound surface (some expose <c>event EventHandler&lt;KissInboundEvent&gt;</c>,
/// some <c>IAsyncEnumerable</c>, some pull-based <c>ReceiveAsync</c>),
/// so the bridge offers a uniform routing function rather than imposing
/// a single subscription model.
/// </para>
/// <para>
/// The outbound side fire-and-forgets the async <c>SendFrameAsync</c>
/// because the dispatcher's frame sinks are synchronous callbacks. Send
/// errors surface via the modem's own logging / error channels rather
/// than rippling back into the session. A future revision could use an
/// outbound queue with retry policy if the modem's send latency
/// dominates timing budgets, but for typical KISS-over-serial or
/// KISS-over-TCP the send is microseconds.
/// </para>
/// </remarks>
public static class KissAx25Bridge
{
    /// <summary>
    /// Build an <see cref="Ax25Adapter"/> whose outbound <c>sendBytes</c>
    /// is wired to <paramref name="modem"/>. Frames emitted by the
    /// dispatcher arrive at the modem as KISS Data frames.
    /// </summary>
    /// <param name="modem">Outbound KISS modem (send side).</param>
    /// <param name="context">Session state.</param>
    /// <param name="scheduler">Timer scheduler driving T1/T2/T3.</param>
    /// <param name="transitions">Codegen-emitted state → transitions map.</param>
    /// <param name="initialState">Session's starting state.</param>
    /// <param name="bindings">Optional custom guard bindings.</param>
    /// <param name="subroutines">Optional subroutine registry override.</param>
    public static Ax25Adapter CreateOutbound(
        IKissModem modem,
        Ax25SessionContext context,
        ITimerScheduler scheduler,
        IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> transitions,
        string initialState,
        IReadOnlyDictionary<Ax25Guard, Func<bool>>? bindings = null,
        ISubroutineRegistry? subroutines = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        return new Ax25Adapter(
            context, scheduler, transitions, initialState,
            sendBytes: bytes =>
            {
                // Fire-and-forget. SendFrameAsync handles KISS framing
                // internally; we just hand it the raw AX.25 bytes.
                _ = modem.SendFrameAsync(bytes);
            },
            bindings: bindings,
            subroutines: subroutines);
    }

    /// <summary>
    /// Route a <see cref="KissInboundEvent"/> from a driver's inbound
    /// stream into <paramref name="adapter"/>. Both
    /// <see cref="Ax25FrameReceivedEvent"/> (regular AX.25 frame) and
    /// <see cref="AckModeDataReceivedEvent"/> (ACKMODE-wrapped AX.25
    /// payload) feed into the adapter as ordinary received frames.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the event was a frame-bearing inbound event and
    /// was routed; <c>false</c> for events the bridge doesn't handle
    /// (e.g. <see cref="UnknownInboundEvent"/> or modem-specific
    /// diagnostic frames). The caller can chain bridge calls with
    /// modem-specific handling for the unhandled case.
    /// </returns>
    public static bool RouteInboundToAdapter(KissInboundEvent evt, Ax25Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(adapter);

        switch (evt)
        {
            case Ax25FrameReceivedEvent ax25:
                adapter.OnReceivedAx25Frame(ax25.Ax25);
                return true;

            case AckModeDataReceivedEvent ackmode:
                if (Ax25Frame.TryParse(ackmode.Ax25Payload.Span, out var parsed))
                {
                    adapter.OnReceivedAx25Frame(parsed);
                    return true;
                }
                return false;

            default:
                return false;
        }
    }
}
