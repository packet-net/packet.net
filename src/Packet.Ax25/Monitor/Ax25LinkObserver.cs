using Packet.Core;

namespace Packet.Ax25.Monitor;

/// <summary>
/// Watches AX.25 traffic go by and keeps track of the links in it: which pairs of stations
/// are talking, whether each link is up, what has been acknowledged, who is waiting on whom,
/// and which frames are retries. Feed it every frame heard or sent on a port and it hands back
/// an <see cref="Ax25LinkEvent"/> for each, narrated in plain words, plus a
/// <see cref="Snapshot()"/> of every link it knows about.
/// </summary>
/// <remarks>
/// <para>
/// This is a third party's view, not a session's. A session (<c>Ax25Session</c>) knows its own
/// variables and timers; a monitor hears only what reaches it, from both ends, and so reasons
/// about the link from the outside: an I frame with an N(S) already seen from that side is a
/// resend; an RR command with P set is a station that has stopped hearing acknowledgements;
/// a jump in N(S) is frames the monitor itself missed. It never assumes it heard everything.
/// </para>
/// <para>
/// A link is a port plus an unordered pair of callsigns. The same pair heard on two ports is
/// two links, because they are: different paths, different conditions. The digipeater path is
/// not part of the identity; a copy of a frame repeated by a digipeater is recognised as such
/// (<see cref="Ax25LinkFlags.Digipeated"/>) and does not count as a resend.
/// </para>
/// <para>
/// Time comes in with each frame rather than from a clock, so a log can be replayed through
/// the observer to reconstruct the links it held, and the result is the same as if it had been
/// listening at the time. The one thing frames cannot tell it is that nothing came: a call that
/// was never answered stays a call until <see cref="Expire"/> is given the time and gives up on
/// it, so a live monitor calls that on a timer. Thread-safe: frames may arrive from a receive
/// thread and a transmit thread at once.
/// </para>
/// </remarks>
public sealed class Ax25LinkObserver
{
    private static readonly string[] BeaconDestinations = ["ID", "BEACON", "CQ", "QST", "ALL", "MAIL", "NODES"];

    private readonly Ax25LinkObserverOptions _options;
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Creates an observer with default <see cref="Ax25LinkObserverOptions"/>.</summary>
    public Ax25LinkObserver() : this(new Ax25LinkObserverOptions())
    {
    }

    /// <summary>Creates an observer.</summary>
    public Ax25LinkObserver(Ax25LinkObserverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RecentPerLink, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLinks, 1);
        _options = options;
    }

    /// <summary>
    /// Reads one frame in the context of its link and returns what it meant, or null when the
    /// bytes are not an AX.25 frame at all (which the caller may still want to log, but which
    /// has no link to belong to).
    /// </summary>
    /// <param name="port">The port it was seen on; part of the link's identity.</param>
    /// <param name="frame">The frame in KISS form: addresses, control, PID and info, no FCS.</param>
    /// <param name="at">When it was seen.</param>
    /// <param name="transmitted">True when the caller sent it rather than heard it.</param>
    public Ax25LinkEvent? Observe(string port, ReadOnlySpan<byte> frame, DateTimeOffset at, bool transmitted = false)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!Ax25Frame.TryParse(frame, out var parsed))
        {
            return null;
        }

        lock (_lock)
        {
            Forget(at);

            var link = GetOrAdd(port, parsed.Source.Callsign, parsed.Destination.Callsign, at);

            // An I or S frame on a modulo-128 link has two control octets, which the modulo-8
            // parse just read as PID and info. Re-read it now the link is known. If that fails
            // (a one-octet control on a link that promised two) keep the first reading.
            if (link.Modulo == 128 && parsed.FrameType.CarriesNr()
                && Ax25Frame.TryParse(frame, Ax25ParseOptions.Lenient, extended: true, out var wide))
            {
                parsed = wide;
            }

            var sender = link.SideOf(parsed.Source.Callsign);
            var receiver = link.SideOf(parsed.Destination.Callsign);
            var other = parsed.Destination.Callsign;

            link.LastSeen = at;

            var via = new string[parsed.Digipeaters.Count];
            for (var i = 0; i < via.Length; i++)
            {
                var digi = parsed.Digipeaters[i];
                via[i] = digi.CrhBit ? digi.Callsign + "*" : digi.Callsign.ToString();
            }

            string narration;
            var flags = Ax25LinkFlags.None;
            int? count = null;

            var repeatedBy = DigipeatedBy(sender.LastFrame, frame, parsed);
            sender.LastFrame = frame.ToArray();

            if (repeatedBy is not null)
            {
                narration = $"repeated by {repeatedBy}";
                flags = Ax25LinkFlags.Digipeated;
            }
            else
            {
                sender.Frames++;
                (narration, flags, count) = Interpret(link, sender, receiver, other, parsed);
            }

            var evt = new Ax25LinkEvent(
                link.Id,
                port,
                at,
                transmitted,
                parsed.Source.Callsign,
                parsed.Destination.Callsign,
                via,
                parsed.FrameType,
                IsCommand(parsed),
                parsed.PollFinal,
                parsed.FrameType.IsInformation() ? parsed.Ns : null,
                parsed.FrameType.CarriesNr() ? parsed.Nr : null,
                parsed.Pid,
                parsed.Info.Length,
                parsed.FrameType is Ax25FrameType.I or Ax25FrameType.Ui ? Ax25InfoText.TryRead(parsed.Info.Span, parsed.Pid) : null,
                narration,
                flags,
                count,
                link.State);

            link.Recent.Enqueue(evt);
            while (link.Recent.Count > _options.RecentPerLink)
            {
                link.Recent.Dequeue();
            }

            return evt;
        }
    }

    /// <summary>
    /// Ages the links to <paramref name="now"/>. A call or a hang-up that nothing has answered
    /// within <see cref="Ax25LinkObserverOptions.CallTimeout"/> is given up on: the link goes to
    /// <see cref="Ax25LinkState.Disconnected"/> and an event says so, timed at the moment the
    /// wait ran out rather than at <paramref name="now"/>, so a replay lands it where it belongs.
    /// The events are returned, one per link that changed and oldest first, and are in each
    /// link's <see cref="Ax25LinkSnapshot.Recent"/> like any frame. Links quiet for longer than
    /// <see cref="Ax25LinkObserverOptions.Lifetime"/> are forgotten, as a frame arriving would
    /// have had them. Frames age links on their own as they arrive; call this on a timer to age
    /// the ones nothing arrives on.
    /// </summary>
    public IReadOnlyList<Ax25LinkEvent> Expire(DateTimeOffset now)
    {
        lock (_lock)
        {
            Forget(now);

            List<Ax25LinkEvent>? expired = null;
            foreach (var link in _links.Values.OrderBy(l => l.LastSeen))
            {
                if (link.State is not (Ax25LinkState.Calling or Ax25LinkState.Disconnecting)
                    || link.Caller is not Side caller
                    || now - link.LastSeen < _options.CallTimeout)
                {
                    continue;
                }

                var hangingUp = link.State == Ax25LinkState.Disconnecting;
                var waited = Waited(_options.CallTimeout);
                var narration = hangingUp
                    ? $"got no answer to the hang-up in {waited}; link down"
                    : $"got no answer in {waited}; the call has failed";
                var flags = Ax25LinkFlags.Timeout | (hangingUp ? Ax25LinkFlags.LinkDown : Ax25LinkFlags.None);
                var other = caller == link.A ? link.B : link.A;

                link.State = Ax25LinkState.Disconnected;
                link.ClearCalls();

                var evt = new Ax25LinkEvent(
                    link.Id,
                    link.Port,
                    link.LastSeen + _options.CallTimeout,
                    Transmitted: false,
                    caller.Callsign,
                    other.Callsign,
                    [],
                    FrameType: null,
                    IsCommand: false,
                    PollFinal: false,
                    Ns: null,
                    Nr: null,
                    Pid: null,
                    InfoLength: 0,
                    Text: null,
                    narration,
                    flags,
                    Count: null,
                    link.State);

                link.Recent.Enqueue(evt);
                while (link.Recent.Count > _options.RecentPerLink)
                {
                    link.Recent.Dequeue();
                }

                (expired ??= []).Add(evt);
            }

            return expired ?? [];
        }
    }

    /// <summary>"3 minutes", "1 minute", "90 seconds": the wait, as a person would say it.</summary>
    private static string Waited(TimeSpan wait)
    {
        if (wait.TotalMinutes >= 1 && wait.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            var minutes = (int)wait.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        var seconds = (int)Math.Round(wait.TotalSeconds);
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    /// <summary>Every link the observer knows, most recently active first.</summary>
    public IReadOnlyList<Ax25LinkSnapshot> Snapshot()
    {
        lock (_lock)
        {
            return _links.Values
                .OrderByDescending(l => l.LastSeen)
                .Select(SnapshotOf)
                .ToList();
        }
    }

    /// <summary>One link by its <see cref="Ax25LinkSnapshot.Id"/>, or null if it is not known.</summary>
    public Ax25LinkSnapshot? Snapshot(string linkId)
    {
        ArgumentNullException.ThrowIfNull(linkId);
        lock (_lock)
        {
            return _links.TryGetValue(linkId, out var link) ? SnapshotOf(link) : null;
        }
    }

    /// <summary>The link id an observation of this port and pair would land on.</summary>
    public static string LinkIdFor(string port, Callsign x, Callsign y)
    {
        ArgumentNullException.ThrowIfNull(port);
        var a = x.ToString();
        var b = y.ToString();
        return string.CompareOrdinal(a, b) <= 0 ? $"{port}|{a}<>{b}" : $"{port}|{b}<>{a}";
    }

    private static (string Narration, Ax25LinkFlags Flags, int? Count) Interpret(Link link, Side sender, Side receiver, Callsign other, Ax25Frame frame)
    {
        var command = IsCommand(frame);
        var poll = command && frame.PollFinal;
        var final = frame.IsResponse && frame.PollFinal;

        // A response with F set answers whatever the other side was polling for.
        if (final)
        {
            receiver.PollsUnanswered = 0;
        }

        switch (frame.FrameType)
        {
            case Ax25FrameType.I:
            {
                var numbered = NumberedTraffic(link);
                var (narration, flags, count) = InterpretI(link, sender, receiver, frame);
                return (narration, flags | numbered, count);
            }

            case Ax25FrameType.Rr:
            case Ax25FrameType.Rnr:
            {
                var numbered = NumberedTraffic(link);
                var (narration, flags, count) = InterpretRrRnr(link, sender, receiver, other, frame, poll, final);
                return (narration, flags | numbered, count);
            }

            case Ax25FrameType.Rej:
            case Ax25FrameType.Srej:
            {
                var numbered = NumberedTraffic(link);
                sender.Rejects++;
                sender.LastNr = frame.Nr;
                var what = frame.FrameType == Ax25FrameType.Rej ? $"resend from #{frame.Nr}" : $"resend #{frame.Nr}";
                var flags = Ax25LinkFlags.Reject | (poll ? Ax25LinkFlags.Poll : final ? Ax25LinkFlags.Final : 0);
                return ($"asks {other} to {what}", flags | numbered, null);
            }

            case Ax25FrameType.Sabm:
            case Ax25FrameType.Sabme:
            {
                var extended = frame.FrameType == Ax25FrameType.Sabme;
                if (link.State == Ax25LinkState.Calling && link.Caller == sender)
                {
                    sender.CallsUnanswered++;
                    return ($"calls {other} again (attempt {sender.CallsUnanswered})", Ax25LinkFlags.Repeat, sender.CallsUnanswered);
                }

                var wasUp = link.State == Ax25LinkState.Connected;
                link.Reset(Ax25LinkState.Calling, extended ? 128 : 8);
                link.Caller = sender;
                sender.CallsUnanswered = 1;
                var verb = wasUp ? $"restarts the link with {other}" : $"calls {other}";
                return (extended ? verb + " (extended mode, modulo 128)" : verb, Ax25LinkFlags.None, null);
            }

            case Ax25FrameType.Disc:
                if (link.State == Ax25LinkState.Disconnecting && link.Caller == sender)
                {
                    sender.CallsUnanswered++;
                    return ($"hangs up again (attempt {sender.CallsUnanswered})", Ax25LinkFlags.Repeat, sender.CallsUnanswered);
                }

                link.State = Ax25LinkState.Disconnecting;
                link.Caller = sender;
                sender.CallsUnanswered = 1;
                return ("hangs up", Ax25LinkFlags.None, null);

            case Ax25FrameType.Ua:
                switch (link.State)
                {
                    case Ax25LinkState.Calling:
                        // Both ends now start from V(S) = V(R) = 0 (§6.3.1), so from here
                        // on a gap in N(S) really is a gap.
                        link.State = Ax25LinkState.Connected;
                        link.Inferred = false;
                        link.ClearCalls();
                        link.A.StartSequence();
                        link.B.StartSequence();
                        return ("accepts the call; link up", Ax25LinkFlags.LinkUp, null);
                    case Ax25LinkState.Disconnecting:
                        link.State = Ax25LinkState.Disconnected;
                        link.ClearCalls();
                        return ("confirms; link down", Ax25LinkFlags.LinkDown, null);
                    default:
                        return ("acknowledges, though nothing was asked", Ax25LinkFlags.Unexpected, null);
                }

            case Ax25FrameType.Dm:
                switch (link.State)
                {
                    case Ax25LinkState.Calling:
                        link.State = Ax25LinkState.Disconnected;
                        link.ClearCalls();
                        return ("refuses the call", Ax25LinkFlags.Refused, null);
                    case Ax25LinkState.Disconnecting:
                        link.State = Ax25LinkState.Disconnected;
                        link.ClearCalls();
                        return ("confirms there is no link; link down", Ax25LinkFlags.LinkDown, null);
                    case Ax25LinkState.Connected:
                        link.State = Ax25LinkState.Disconnected;
                        link.ClearCalls();
                        return ("says there is no link; link down", Ax25LinkFlags.LinkDown, null);
                    default:
                        return ("says there is no link", Ax25LinkFlags.None, null);
                }

            case Ax25FrameType.Ui:
                return (NarrateUi(frame), Ax25LinkFlags.None, null);

            case Ax25FrameType.Frmr:
                return ("rejects a frame as malformed (protocol error)", Ax25LinkFlags.ProtocolError, null);

            case Ax25FrameType.Xid:
                return (command ? "proposes link parameters" : "answers with link parameters", Ax25LinkFlags.None, null);

            case Ax25FrameType.Test:
                return (command ? "sends a test frame" : "echoes the test frame", Ax25LinkFlags.None, null);

            default:
                return ($"sends an unrecognised frame (control 0x{frame.Control:X2})", Ax25LinkFlags.ProtocolError, null);
        }
    }

    /// <summary>
    /// What a numbered frame (I, RR, RNR, REJ, SREJ) says about the link it is heard on, before
    /// the frame itself is read. Both ends sending numbered traffic means both think the link is
    /// up. On a link never heard set up, we joined late and it is taken to be up from here. On a
    /// link that is being taken down, it is the other side not yet having heard the hang-up, and
    /// nothing changes: the UA or DM that follows will finish the job. On a link heard to come
    /// down, or still being called, one end disagrees: the link is taken to be up, and the frame
    /// is flagged so the disagreement is visible.
    /// </summary>
    private static Ax25LinkFlags NumberedTraffic(Link link)
    {
        switch (link.State)
        {
            case Ax25LinkState.Connected:
            case Ax25LinkState.Disconnecting:
                return Ax25LinkFlags.None;
            case Ax25LinkState.Unconnected:
                link.State = Ax25LinkState.Connected;
                link.Inferred = true;
                return Ax25LinkFlags.None;
            default:
                // The call, if one was open, was answered without us hearing it.
                link.State = Ax25LinkState.Connected;
                link.Inferred = true;
                link.ClearCalls();
                return Ax25LinkFlags.Unexpected;
        }
    }

    private static (string, Ax25LinkFlags, int?) InterpretI(Link link, Side sender, Side receiver, Ax25Frame frame)
    {
        var flags = Ax25LinkFlags.None;
        var m = link.Modulo;
        int ns = frame.Ns;
        string verb;

        sender.HasSentData = true;
        if (sender.NextNs is not int expected)
        {
            verb = $"sends #{ns}";
            sender.NextNs = Mod(ns + 1, m);
            sender.DataFrames++;
            sender.DataBytes += frame.Info.Length;
        }
        else
        {
            var behind = Mod(expected - ns, m);
            if (behind == 0)
            {
                verb = $"sends #{ns}";
                sender.NextNs = Mod(ns + 1, m);
                sender.DataFrames++;
                sender.DataBytes += frame.Info.Length;
            }
            else if (behind <= m / 2)
            {
                verb = $"resends #{ns}";
                sender.Resends++;
                flags |= Ax25LinkFlags.Resend;
            }
            else
            {
                var missed = Mod(ns - expected, m);
                verb = missed == 1
                    ? $"sends #{ns} (missed #{expected})"
                    : $"sends #{ns} (missed #{expected}-#{Mod(ns - 1, m)})";
                flags |= Ax25LinkFlags.Missed;
                sender.NextNs = Mod(ns + 1, m);
                sender.DataFrames++;
                sender.DataBytes += frame.Info.Length;
            }
        }

        if (Ax25InfoText.TryRead(frame.Info.Span, frame.Pid) is null)
        {
            verb += frame.Pid is byte pid && pid != Ax25Frame.PidNoLayer3
                ? $", {frame.Info.Length} bytes of {Ax25Pid.Name(pid)}"
                : $", {frame.Info.Length} bytes";
        }

        var ack = AckProgress(sender, receiver, frame.Nr, m);
        if (ack is not null)
        {
            verb += $"; {ack}";
        }
        sender.LastNr = frame.Nr;

        return (verb, flags, null);
    }

    private static (string, Ax25LinkFlags, int?) InterpretRrRnr(Link link, Side sender, Side receiver, Callsign other, Ax25Frame frame, bool poll, bool final)
    {
        var m = link.Modulo;
        var busy = frame.FrameType == Ax25FrameType.Rnr;
        var flags = busy ? Ax25LinkFlags.Busy : Ax25LinkFlags.None;
        int? count = null;
        var wasBusy = sender.Busy;
        sender.Busy = busy;

        var progress = AckProgress(sender, receiver, frame.Nr, m);
        var caughtUp = !receiver.HasSentData || receiver.NextNs == frame.Nr;
        string narration;

        if (poll)
        {
            // An RR/RNR command with P is the T1 recovery of §6.4.10 (or the T3 keepalive):
            // "what have you received?", sent because no acknowledgement arrived.
            flags |= Ax25LinkFlags.Poll;
            sender.Polls++;
            var repeat = sender.PollsUnanswered > 0;
            sender.PollsUnanswered++;
            if (repeat)
            {
                flags |= Ax25LinkFlags.Repeat;
                count = sender.PollsUnanswered;
            }

            var again = repeat ? " again" : "";
            var outstanding = Outstanding(sender, receiver, m);
            narration = outstanding is null
                ? (busy ? $"busy; asks {other}{again} to hold on" : $"checks{again} that {other} is still there")
                : (busy ? $"busy; asks {other}{again} what it has received ({outstanding})" : $"asks {other}{again} what it has received ({outstanding})");
            if (repeat)
            {
                narration += $" (poll {sender.PollsUnanswered})";
            }
            if (progress is not null)
            {
                narration += $"; {progress}";
            }
        }
        else if (final)
        {
            flags |= Ax25LinkFlags.Final;
            var state = busy ? "busy, hold on"
                : !receiver.HasSentData ? "nothing received, ready"
                : caughtUp ? $"all received through #{Mod(frame.Nr - 1, m)}"
                : $"still waiting for #{frame.Nr}";
            narration = $"answers the poll: {state}";
        }
        else
        {
            var readyAgain = wasBusy && !busy;
            narration = busy
                ? (progress is null ? "busy, hold on" : $"busy, hold on; {progress}")
                : progress is not null
                    ? (readyAgain ? $"ready again; {progress}" : progress)
                    : readyAgain ? "ready again"
                    : caughtUp ? "ready"
                    : $"still waiting for #{frame.Nr}";
        }

        sender.LastNr = frame.Nr;
        return (narration, flags, count);
    }

    /// <summary>
    /// "acknowledges #2-#4" when this N(R) acknowledges something new from the other side;
    /// null when it does not move, or when the other side has sent nothing we know of.
    /// </summary>
    private static string? AckProgress(Side sender, Side receiver, int nr, int m)
    {
        if (!receiver.HasSentData)
        {
            return null;
        }
        if (sender.LastNr is not int last)
        {
            return $"acknowledges through #{Mod(nr - 1, m)}";
        }
        var moved = Mod(nr - last, m);
        if (moved == 0)
        {
            return null;
        }
        if (moved == 1)
        {
            return $"acknowledges #{Mod(nr - 1, m)}";
        }
        if (moved > m / 2)
        {
            return $"acknowledges through #{Mod(nr - 1, m)}";
        }
        return $"acknowledges #{last}-#{Mod(nr - 1, m)}";
    }

    /// <summary>
    /// "no acknowledgement heard for #3-#5" when the sender has I frames out that the other
    /// side has not been heard to acknowledge; null when nothing is outstanding or unknown.
    /// </summary>
    private static string? Outstanding(Side sender, Side receiver, int m)
    {
        if (sender.NextNs is not int next)
        {
            return null;
        }
        if (receiver.LastNr is not int acked)
        {
            return "no acknowledgement heard";
        }
        var pending = Mod(next - acked, m);
        if (pending == 0)
        {
            return null;
        }
        var last = Mod(next - 1, m);
        return pending == 1
            ? $"no acknowledgement heard for #{last}"
            : $"no acknowledgement heard for #{acked}-#{last}";
    }

    private static string NarrateUi(Ax25Frame frame)
    {
        var length = frame.Info.Length;
        if (frame.Pid == Ax25Frame.PidNetRom)
        {
            return frame.Destination.Callsign.Base == "NODES"
                ? $"NET/ROM routing broadcast, {length} bytes"
                : $"NET/ROM, {length} bytes";
        }

        var kind = Array.IndexOf(BeaconDestinations, frame.Destination.Callsign.Base) >= 0 ? "beacon" : "unconnected";
        if (Ax25InfoText.TryRead(frame.Info.Span, frame.Pid) is not null)
        {
            return kind;
        }
        return frame.Pid is byte pid && pid != Ax25Frame.PidNoLayer3
            ? $"{kind}, {length} bytes of {Ax25Pid.Name(pid)}"
            : $"{kind}, {length} bytes";
    }

    /// <summary>
    /// The digipeater that just repeated the previous frame from this side, when the new frame
    /// is that frame again with one more H bit set; null otherwise.
    /// </summary>
    private static string? DigipeatedBy(byte[]? previous, ReadOnlySpan<byte> current, Ax25Frame parsed)
    {
        if (previous is null || previous.Length != current.Length || parsed.Digipeaters.Count == 0)
        {
            return null;
        }

        // The H bit is bit 7 of the SSID octet of each digipeater slot. Everything else must
        // match exactly; the H bits must have grown.
        var grown = false;
        var digiCount = parsed.Digipeaters.Count;
        var lastSet = -1;
        for (var i = 0; i < current.Length; i++)
        {
            var slot = (i - (2 * Ax25Address.EncodedLength)) / Ax25Address.EncodedLength;
            var inDigiSsid = i >= 2 * Ax25Address.EncodedLength && slot < digiCount && i % Ax25Address.EncodedLength == Ax25Address.EncodedLength - 1;
            if (!inDigiSsid)
            {
                if (previous[i] != current[i])
                {
                    return null;
                }
                continue;
            }

            if ((previous[i] & 0x7F) != (current[i] & 0x7F))
            {
                return null;
            }
            var was = (previous[i] & 0x80) != 0;
            var now = (current[i] & 0x80) != 0;
            if (was && !now)
            {
                return null;
            }
            if (now)
            {
                lastSet = slot;
            }
            grown |= now && !was;
        }

        return grown && lastSet >= 0 ? parsed.Digipeaters[lastSet].Callsign.ToString() : null;
    }

    /// <summary>Command per the C bits; an AX.25 v1 frame (C bits equal) reads as a command.</summary>
    private static bool IsCommand(Ax25Frame frame) => frame.IsCommand || !frame.IsResponse;

    private static int Mod(int value, int m) => ((value % m) + m) % m;

    private Link GetOrAdd(string port, Callsign source, Callsign destination, DateTimeOffset at)
    {
        var id = LinkIdFor(port, source, destination);
        if (_links.TryGetValue(id, out var link))
        {
            return link;
        }

        while (_links.Count >= _options.MaxLinks)
        {
            var quietest = _links.Values.MinBy(l => l.LastSeen)!;
            _links.Remove(quietest.Id);
        }

        link = new Link(id, port, source, destination, at);
        _links.Add(id, link);
        return link;
    }

    private void Forget(DateTimeOffset now)
    {
        var cutoff = now - _options.Lifetime;
        List<string>? stale = null;
        foreach (var link in _links.Values)
        {
            if (link.LastSeen < cutoff)
            {
                (stale ??= []).Add(link.Id);
            }
        }
        if (stale is null)
        {
            return;
        }
        foreach (var id in stale)
        {
            _links.Remove(id);
        }
    }

    private static Ax25LinkSnapshot SnapshotOf(Link link) => new(
        link.Id,
        link.Port,
        link.A.Callsign,
        link.B.Callsign,
        link.State,
        link.Inferred,
        link.Modulo,
        link.FirstSeen,
        link.LastSeen,
        StatsOf(link.A, link.B, link.Modulo),
        StatsOf(link.B, link.A, link.Modulo),
        ConcernOf(link),
        link.Recent.ToArray());

    private static Ax25LinkSideStats StatsOf(Side side, Side other, int m) => new(
        side.Frames,
        side.DataFrames,
        side.DataBytes,
        side.Resends,
        side.Polls,
        side.PollsUnanswered,
        side.Rejects,
        side.CallsUnanswered,
        side.Busy,
        side.NextNs is int next && other.LastNr is int acked ? Mod(next - acked, m) : null);

    private static string? ConcernOf(Link link)
    {
        foreach (var side in new[] { link.A, link.B })
        {
            if (side.PollsUnanswered == 1)
            {
                return $"{side.Callsign} timed out waiting and is polling";
            }
            if (side.PollsUnanswered > 1)
            {
                return $"{side.Callsign} has polled {side.PollsUnanswered} times with no answer";
            }
        }

        if (link.Caller is Side caller && caller.CallsUnanswered > 1)
        {
            return link.State == Ax25LinkState.Disconnecting
                ? $"{caller.Callsign} has tried to hang up {caller.CallsUnanswered} times with no answer"
                : $"{caller.Callsign} has called {caller.CallsUnanswered} times with no answer";
        }

        foreach (var side in new[] { link.A, link.B })
        {
            if (side.Busy)
            {
                return $"{side.Callsign} is busy";
            }
        }

        return null;
    }

    private sealed class Link
    {
        public Link(string id, string port, Callsign first, Callsign second, DateTimeOffset at)
        {
            Id = id;
            Port = port;
            A = new Side(first);
            B = new Side(second);
            FirstSeen = at;
            LastSeen = at;
        }

        public string Id { get; }
        public string Port { get; }
        public Side A { get; }
        public Side B { get; }
        public Ax25LinkState State { get; set; } = Ax25LinkState.Unconnected;
        public bool Inferred { get; set; }
        public int Modulo { get; set; } = 8;
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; set; }
        public Side? Caller { get; set; }
        public Queue<Ax25LinkEvent> Recent { get; } = new();

        public Side SideOf(Callsign callsign) => callsign == A.Callsign ? A : B;

        /// <summary>A new call: whatever was known about sequence numbers no longer applies.</summary>
        public void Reset(Ax25LinkState state, int modulo)
        {
            State = state;
            Modulo = modulo;
            Inferred = false;
            A.ResetSequence();
            B.ResetSequence();
        }

        public void ClearCalls()
        {
            A.CallsUnanswered = 0;
            B.CallsUnanswered = 0;
            Caller = null;
        }
    }

    private sealed class Side(Callsign callsign)
    {
        public Callsign Callsign { get; } = callsign;
        public int Frames { get; set; }
        public int DataFrames { get; set; }
        public long DataBytes { get; set; }
        public int Resends { get; set; }
        public int Polls { get; set; }
        public int PollsUnanswered { get; set; }
        public int Rejects { get; set; }
        public int CallsUnanswered { get; set; }
        public bool Busy { get; set; }

        /// <summary>The N(S) this side will use next, as far as we have heard; null when unknown.</summary>
        public int? NextNs { get; set; }

        /// <summary>True once an I frame has been heard from this side since the link came up.</summary>
        public bool HasSentData { get; set; }

        /// <summary>The N(R) this side last sent; null before any numbered frame.</summary>
        public int? LastNr { get; set; }

        /// <summary>The last frame from this side, for spotting a digipeater's copy of it.</summary>
        public byte[]? LastFrame { get; set; }

        public void ResetSequence()
        {
            NextNs = null;
            LastNr = null;
            HasSentData = false;
            PollsUnanswered = 0;
            Busy = false;
        }

        public void StartSequence()
        {
            NextNs = 0;
            LastNr = 0;
            HasSentData = false;
        }
    }
}
