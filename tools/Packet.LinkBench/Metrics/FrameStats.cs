using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.LinkBench.Metrics;

/// <summary>Per-endpoint frame-level metrics (link-bench plan §5) computed from
/// one endpoint's <see cref="FrameTap"/> trace.</summary>
internal sealed class FrameStats
{
    public int TxTotal;
    public int TxI;
    public int TxRr;
    public int TxRnr;
    public int TxRej;
    public int TxSrej;
    public int TxU;       // SABM/UA/DISC/DM/FRMR/XID/TEST/UI
    public int RxTotal;

    /// <summary>I-frame transmissions whose N(S) was already outstanding -
    /// i.e. the frame went to the wire a second (third…) time.</summary>
    public int Retransmits;

    /// <summary>Extra copies in runs of consecutive identical supervisory frames
    /// emitted within the dup window - the #79 burst, quantified. 0 = the engine
    /// never emitted the same RR/REJ twice back-to-back.</summary>
    public int DupSupervisory;

    /// <summary>Longest run of identical consecutive supervisory frames
    /// (1 = no duplication; #79 reports bursts of 2-6).</summary>
    public int MaxSupervisoryBurst = 1;

    /// <summary>Cumulative time this endpoint sat with a full send window
    /// (k I-frames unacknowledged) - blocked on the peer's acks.</summary>
    public TimeSpan WindowStall;

    public int TxSupervisory => TxRr + TxRnr + TxRej + TxSrej;

    private static bool IsSupervisory(Ax25Frame f) => (f.Control & 0x03) == 0x01;
    private static bool CarriesNr(Ax25Frame f) => (f.Control & 0x01) == 0 || IsSupervisory(f);

    private static bool SameSupervisory(Ax25Frame x, Ax25Frame y) =>
        x.Control == y.Control &&
        x.ControlExtension == y.ControlExtension &&
        x.Source.Callsign.Equals(y.Source.Callsign) &&
        x.Destination.Callsign.Equals(y.Destination.Callsign);

    /// <summary>
    /// Analyze one endpoint's time-ordered trace. <paramref name="k"/> is the
    /// session's send-window size (for window-stall accounting);
    /// <paramref name="dupWindow"/> bounds how close together two identical
    /// supervisory frames must be to count as a duplicate burst.
    /// </summary>
    public static FrameStats Analyze(IReadOnlyList<TracedFrame> trace, int k, TimeSpan dupWindow)
    {
        var stats = new FrameStats();

        // Window/retransmit tracking (mod-8 or mod-128 transparent: Ns/Nr are
        // mode-aware on the parsed frame). `outstanding` holds the N(S) of every
        // unacknowledged I-frame in send order.
        var outstanding = new Queue<byte>();
        DateTimeOffset? stallStart = null;
        TracedFrame? prevTx = null;
        var burst = 1;

        foreach (var t in trace)
        {
            if (t.Direction == FrameDirection.Transmitted)
            {
                stats.TxTotal++;
                switch (Ax25FrameClassifier.Classify(t.Frame))
                {
                    case IFrameReceived:
                        stats.TxI++;
                        var ns = t.Frame.Ns;
                        if (outstanding.Contains(ns))
                        {
                            stats.Retransmits++;
                        }
                        else
                        {
                            outstanding.Enqueue(ns);
                            if (outstanding.Count >= k && stallStart is null)
                            {
                                stallStart = t.At;
                            }
                        }
                        break;
                    case RrReceived: stats.TxRr++; break;
                    case RnrReceived: stats.TxRnr++; break;
                    case RejReceived: stats.TxRej++; break;
                    case SrejReceived: stats.TxSrej++; break;
                    default: stats.TxU++; break;
                }

                // Duplicate-supervisory detection: consecutive identical S-frames
                // (same control byte ⇒ same type + P/F + N(R)) within the window.
                if (IsSupervisory(t.Frame) &&
                    prevTx is { } p &&
                    IsSupervisory(p.Frame) &&
                    SameSupervisory(p.Frame, t.Frame) &&
                    t.At - p.At <= dupWindow)
                {
                    stats.DupSupervisory++;
                    burst++;
                }
                else
                {
                    burst = 1;
                }

                stats.MaxSupervisoryBurst = Math.Max(stats.MaxSupervisoryBurst, burst);
                prevTx = t;
            }
            else
            {
                stats.RxTotal++;
                if (CarriesNr(t.Frame) && outstanding.Count > 0)
                {
                    var nr = t.Frame.Nr;
                    var mod = t.Frame.IsExtendedControl ? 128 : 8;
                    var vs = (byte)((outstanding.Last() + 1) % mod);

                    if (nr == vs)
                    {
                        outstanding.Clear();
                    }
                    else if (outstanding.Contains(nr))
                    {
                        while (outstanding.Peek() != nr)
                        {
                            outstanding.Dequeue();
                        }
                    }
                    // else: stale N(R) (e.g. a dup ack) - acks nothing new.

                    if (outstanding.Count < k && stallStart is { } start)
                    {
                        stats.WindowStall += t.At - start;
                        stallStart = null;
                    }
                }
            }
        }

        // Still stalled when the trace ends (e.g. a run that timed out mid-window).
        if (stallStart is { } openStall && trace.Count > 0)
        {
            stats.WindowStall += trace[^1].At - openStall;
        }

        return stats;
    }
}
