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
/// No timer for the wait itself. Every step is a transition of the session, so the check runs from
/// <see cref="Ax25Session.TransitionFired"/>: an acknowledgement, a retransmission, a busy peer
/// clearing, T1 or T3 polling. A peer that has gone is ended by the link itself when T1 has run
/// out N2 times, which takes the session to Disconnected and ends the wait. A peer that stays busy
/// (RNR) is alive, so the link never gives up on it; it gets the same budget the link would give a
/// silent peer (the one timer here), and if still busy after it the DISC goes anyway. A link that reaches
/// Disconnected or Awaiting Release by any other route (the peer's DISC, a sysop kill) ends it
/// too, and so does a DL-CONNECT confirm: that is a new dial on this cached session, which now
/// belongs to someone else. A link being reset (Awaiting Connection after an FRMR or an
/// unexpected UA) is waited for, not disconnected, since a DL-DISCONNECT request has no effect
/// there; a peer that starts the link over with a SABM(E) gets it handed to a fresh owner when
/// the connection's owner provides one.
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
    private readonly Action? peerRestarted;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int done;

    // Set by Abort: disconnect as soon as the link can take a DL-DISCONNECT request, without
    // waiting for the data. Read on the dispatch thread, so volatile.
    private volatile bool abortRequested;

    // Running while the peer is busy (RNR) with our data still waiting; see TrackBusyPeer.
    private readonly object busyGate = new();
    private ITimer? busyTimer;

    private Ax25GracefulClose(Ax25Session session, Action? peerRestarted)
    {
        this.session = session;
        this.peerRestarted = peerRestarted;
    }

    /// <summary>Completes when this close has either sent its DISC or found the link
    /// already ending or taken over.</summary>
    public Task Finished => finished.Task;

    /// <summary>
    /// Start closing <paramref name="session"/> once its data is delivered. A link that is
    /// already down or releasing needs nothing. Returns the pending close, or null when there is
    /// nothing to wait for.
    /// </summary>
    /// <remarks>
    /// Awaiting Connection (or Awaiting v2.2 Connection) on a session a connection already wraps
    /// is a reset of the established link (an FRMR, an unexpected UA, an N(R) error), not a
    /// fresh dial: the wrapper only exists once a dial has connected. A DL-DISCONNECT request has
    /// no effect there, so the close waits for the reset to settle like any other wait.
    /// <paramref name="peerRestarted"/>, when given, is called if the peer starts the link over
    /// with a SABM(E) while the close is pending: the close is dropped and the owner hands the
    /// link on as a fresh connect, since no SessionAccepted is raised for a SABM in Connected.
    /// Without it the close carries on (and, the SABM having emptied the queue, disconnects).
    /// </remarks>
    public static Ax25GracefulClose? Begin(Ax25Session session, Action? peerRestarted = null)
        => Start(session, abort: false, peerRestarted);

    /// <summary>
    /// Disconnect now, discarding anything not yet delivered: the old close, kept for a sysop
    /// kill and shutdown. If a close is pending on the session it is cut short. A link in the
    /// middle of a reset is disconnected as soon as the reset settles.
    /// </summary>
    public static void DisconnectNow(Ax25Session session)
    {
        if (Pending.TryGetValue(session, out var close))
        {
            close.Abort();
            return;
        }

        Start(session, abort: true, peerRestarted: null);
    }

    private static Ax25GracefulClose? Start(Ax25Session session, bool abort, Action? peerRestarted)
    {
        if (session.CurrentState is not ("Connected" or "TimerRecovery" or "AwaitingConnection" or "AwaitingV22Connection"))
        {
            return null;
        }

        var close = new Ax25GracefulClose(session, peerRestarted) { abortRequested = abort };
        Pending.AddOrUpdate(session, close);
        session.DataLinkSignalEmitted += close.OnSignal;
        session.TransitionFired += close.OnTransition;
        // Subscribed first, then checked, so an acknowledgement (or the reset settling) landing
        // between the two is seen by one or the other (Finish runs once either way).
        close.Check();
        return close;
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

    /// <summary>Cut this close short: DISC now if the link can take it, or as soon as a reset
    /// in progress settles.</summary>
    public void Abort()
    {
        abortRequested = true;
        Check();
    }

    private void OnTransition(object? sender, Packet.Ax25.Sdl.TransitionSpec transition) => Check();

    private void OnSignal(object? sender, DataLinkSignal signal)
    {
        switch (signal)
        {
            case DataLinkConnectIndication when peerRestarted is not null
                && session.CurrentTrigger is SabmReceived or SabmeReceived:
                // The peer started the link over. Leave it to a fresh owner instead of
                // disconnecting the connection the peer has just made.
                if (Finish(disconnect: false))
                {
                    peerRestarted();
                }
                break;
            case DataLinkConnectConfirm:
                // A new dial on this cached session: it belongs to whoever dialled.
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
                if (abortRequested || session.AllSentDataAcknowledged)
                {
                    Finish(disconnect: true);
                }
                else
                {
                    TrackBusyPeer();
                }
                break;
            case "AwaitingConnection" or "AwaitingV22Connection":
                // The link is being reset; T1/N2 bound that too.
                break;
            default:
                // Disconnected or Awaiting Release: the link is ending by another route.
                Finish(disconnect: false);
                break;
        }
    }

    // A peer that stays busy (answering RNR) is alive, so the link never gives up on it and a
    // close waiting for it would wait for ever, with any redial to that peer behind it. So a busy
    // peer gets the budget the link would give a silent one before declaring it gone, N2 retries
    // of T1 with T1's backoff (figc4.7 Select T1 Value: RC x 250 ms + 2 x SRT), and if it is
    // still busy at the end the DISC goes anyway, discarding what it would not take. The clock
    // starts when the peer is seen busy and resets whenever it clears.
    private void TrackBusyPeer()
    {
        lock (busyGate)
        {
            if (!session.Context.PeerReceiverBusy)
            {
                busyTimer?.Dispose();
                busyTimer = null;
            }
            else if (busyTimer is null)
            {
                busyTimer = TimeProvider.System.CreateTimer(
                    _ => OnBusyBudgetSpent(), null, BusyBudget(session.Context), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnBusyBudgetSpent()
    {
        lock (busyGate)
        {
            busyTimer?.Dispose();
            busyTimer = null;
        }

        if (session.Context.PeerReceiverBusy)
        {
            Abort();
        }
    }

    internal static TimeSpan BusyBudget(Ax25SessionContext context)
    {
        int n2 = context.N2;
        return TimeSpan.FromMilliseconds((250.0 * n2 * (n2 + 1) / 2) + (n2 * 2 * context.Srt.TotalMilliseconds));
    }

    // True for the one call that ends this close.
    private bool Finish(bool disconnect)
    {
        if (!Detach())
        {
            return false;
        }

        if (disconnect)
        {
            PostDisconnect(session);
        }
        finished.TrySetResult();
        return true;
    }

    private bool Detach()
    {
        if (Interlocked.Exchange(ref done, 1) != 0)
        {
            return false;
        }

        session.TransitionFired -= OnTransition;
        session.DataLinkSignalEmitted -= OnSignal;
        lock (busyGate)
        {
            busyTimer?.Dispose();
            busyTimer = null;
        }
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
