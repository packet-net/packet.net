using System.Runtime.CompilerServices;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// Disconnects an AX.25 link only once everything already handed to it has reached the peer:
/// the node's half of an app- or console-initiated close (packet.net#850).
/// </summary>
/// <remarks>
/// <para>
/// A DL-DISCONNECT request in Connected or Timer Recovery discards the I-frame queue and sends
/// DISC at once (figc4.4 / figc4.5), so a close posted straight after the owner's last write
/// threw that write away while it was still queued or unacknowledged. The engine stays as the
/// SDL draws it. Instead the node waits, the way LinBPQ's DISCPENDING does ("SEND DISC WHEN ALL
/// DATA ACKED", L4Code.c): the owner has already stopped writing, and once the queue is empty and
/// every sent I-frame is acknowledged (<see cref="Ax25Session.AllSentDataAcknowledged"/>) the
/// DL-DISCONNECT request goes in.
/// </para>
/// <para>
/// No timer of its own. Every step is a transition of the session, so the check runs from
/// <see cref="Ax25Session.TransitionFired"/>: an acknowledgement, a retransmission, a busy peer
/// clearing, T1 or T3 polling. A peer that has gone is ended by the link itself when T1 has run
/// out N2 times, which takes the session to Disconnected and ends the wait. A link that reaches
/// Disconnected or Awaiting Release by any other route (the peer's DISC, a sysop kill) ends it
/// too, and so does a DL-CONNECT confirm: that is a new dial on this cached session, which now
/// belongs to someone else.
/// </para>
/// <para>
/// Node shutdown and port teardown do not wait: <see cref="DisconnectAllNow"/> sends the DISC for
/// every close still pending on a listener before the listener goes.
/// </para>
/// </remarks>
internal sealed class Ax25GracefulClose
{
    // One pending close per session at most, looked up by the connector (a dial to the same peer
    // waits for it) and by port teardown (which cuts it short). Weak so a session the listener
    // evicts takes its entry with it.
    private static readonly ConditionalWeakTable<Ax25Session, Ax25GracefulClose> Pending = new();

    private readonly Ax25Session session;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int done;

    private Ax25GracefulClose(Ax25Session session) => this.session = session;

    /// <summary>Completes when this close has either sent its DISC or found the link
    /// already ending or taken over.</summary>
    public Task Finished => finished.Task;

    /// <summary>
    /// Start closing <paramref name="session"/> once its data is delivered. A link still being
    /// set up (a dial nobody has data on yet) is disconnected at once, as before; a link that is
    /// already down or releasing needs nothing. Returns the pending close, or null when there is
    /// nothing to wait for.
    /// </summary>
    public static Ax25GracefulClose? Begin(Ax25Session session)
    {
        switch (session.CurrentState)
        {
            case "Connected" or "TimerRecovery":
                var close = new Ax25GracefulClose(session);
                Pending.AddOrUpdate(session, close);
                session.DataLinkSignalEmitted += close.OnSignal;
                session.TransitionFired += close.OnTransition;
                // Subscribed first, then checked, so an acknowledgement landing between the two
                // is seen by one or the other (Finish runs once either way).
                close.Check();
                return close;
            case "AwaitingConnection" or "AwaitingV22Connection":
                PostDisconnect(session);
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Disconnect now, discarding anything not yet delivered: the old close, kept for a sysop
    /// kill and shutdown. If a close is pending on the session it is cut short.
    /// </summary>
    public static void DisconnectNow(Ax25Session session)
    {
        if (Pending.TryGetValue(session, out var close))
        {
            close.Abort();
            return;
        }

        if (session.CurrentState is "Connected" or "TimerRecovery" or "AwaitingConnection" or "AwaitingV22Connection")
        {
            PostDisconnect(session);
        }
    }

    /// <summary>Send the DISC now for every close still pending on <paramref name="listener"/>'s
    /// sessions. Port teardown calls it before disposing the listener, so a link still draining
    /// is ended cleanly rather than left for the peer to time out.</summary>
    public static void DisconnectAllNow(Ax25Listener listener)
    {
        foreach (var session in listener.ActiveSessions)
        {
            if (Pending.TryGetValue(session, out var close))
            {
                close.Abort();
            }
        }
    }

    /// <summary>Wait for a close still pending on the link from <paramref name="local"/> to
    /// <paramref name="remote"/>, so a new dial neither discards its tail (a DL-CONNECT request in
    /// Connected empties the queue) nor has its own link disconnected by it.</summary>
    public static async Task WaitForPendingAsync(
        Ax25Listener listener, Callsign local, Callsign remote, CancellationToken cancellationToken)
    {
        foreach (var session in listener.ActiveSessions)
        {
            if (session.Context.Local.Equals(local) && session.Context.Remote.Equals(remote)
                && Pending.TryGetValue(session, out var close))
            {
                await close.Finished.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Cut this close short: DISC now if the link is still up.</summary>
    public void Abort()
    {
        if (Detach())
        {
            if (session.CurrentState is "Connected" or "TimerRecovery" or "AwaitingConnection" or "AwaitingV22Connection")
            {
                PostDisconnect(session);
            }
            finished.TrySetResult();
        }
    }

    private void OnTransition(object? sender, Packet.Ax25.Sdl.TransitionSpec transition) => Check();

    private void OnSignal(object? sender, DataLinkSignal signal)
    {
        switch (signal)
        {
            case DataLinkConnectConfirm:
            case DataLinkDisconnectIndication:
            case DataLinkDisconnectConfirm:
                Finish(disconnect: false);
                break;
        }
    }

    // Runs on the dispatch thread under the session's lock when called from TransitionFired, so
    // the state and the acknowledgement check agree. A DL-DISCONNECT request posted from there is
    // queued by the session and dispatched straight after the current event.
    private void Check()
    {
        if (Volatile.Read(ref done) != 0)
        {
            return;
        }

        switch (session.CurrentState)
        {
            case "Connected" or "TimerRecovery":
                if (session.AllSentDataAcknowledged)
                {
                    Finish(disconnect: true);
                }
                break;
            case "AwaitingConnection" or "AwaitingV22Connection":
                // The link is re-establishing after an error; T1/N2 bound that too.
                break;
            default:
                // Disconnected or Awaiting Release: the link is ending by another route.
                Finish(disconnect: false);
                break;
        }
    }

    private void Finish(bool disconnect)
    {
        if (!Detach())
        {
            return;
        }

        if (disconnect)
        {
            PostDisconnect(session);
        }
        finished.TrySetResult();
    }

    // True for the one caller that ends this close.
    private bool Detach()
    {
        if (Interlocked.Exchange(ref done, 1) != 0)
        {
            return false;
        }

        session.TransitionFired -= OnTransition;
        session.DataLinkSignalEmitted -= OnSignal;
        if (Pending.TryGetValue(session, out var current) && ReferenceEquals(current, this))
        {
            Pending.Remove(session);
        }
        return true;
    }

    private static void PostDisconnect(Ax25Session session)
    {
        try
        {
            session.PostEvent(new DlDisconnectRequest());
        }
        catch
        {
            // Best-effort teardown; a close must never throw.
        }
    }
}
