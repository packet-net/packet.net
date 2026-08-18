namespace Packet.LinkBench;

/// <summary>One bench run's full parameter set (link-bench plan §5 knobs).
/// Multi-valued CLI flags expand to a cartesian product of these.</summary>
internal sealed record RunConfig
{
    public string Channel { get; init; } = "inproc";          // inproc | axudp | netsim
    public int PayloadBytes { get; init; } = 64 * 1024;
    public int? K { get; init; }                              // null = engine default (4)
    public TimeSpan? T1 { get; init; }                        // null = engine default (6 s)
    public TimeSpan? T2 { get; init; }                        // null = engine default (3 s); 0 = ack-per-frame
    public int Paclen { get; init; } = 256;                   // bytes per I-frame (≤ N1)
    public bool AckMode { get; init; } = true;                // §2: ackmode is the assumed default
    public bool T1FromTxComplete { get; init; }               // re-arm T1 on the frame's TX-complete echo
    public bool Srej { get; init; }                           // force SREJ (selective reject) on both ends - only bites under loss
    public bool Bidirectional { get; init; }

    // InProcChannel model knobs (ignored on axudp/netsim).
    public int Baud { get; init; }                            // 0 = no airtime modelling (rung 1)
    public bool HalfDuplex { get; init; }
    public TimeSpan TxDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan TxTail { get; init; } = TimeSpan.FromMilliseconds(20);
    public TimeSpan Turnaround { get; init; } = TimeSpan.FromMilliseconds(100);
    public double Loss { get; init; }
    public int Seed { get; init; } = 42;

    /// <summary>Run the modeled channel N× faster than real time. Also scales the
    /// engines' T1/T2/T3 defaults by the same factor (explicit --t1/--t2 are taken
    /// as already-scaled), so timer-vs-airtime ratios stay honest.</summary>
    public double TimeScale { get; init; } = 1.0;

    /// <summary>Set when <see cref="T1"/>/<see cref="T2"/> were auto-scaled down from
    /// the spec defaults because this is a zero-airtime (<c>--baud 0</c>) inproc run -
    /// see <c>Cli.ApplyZeroAirtimeTimerDefaults</c>. Purely informational; drives the
    /// one-time startup notice.</summary>
    public bool ZeroAirtimeTimersAutoScaled { get; init; }

    public TimeSpan DupWindow { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public (int A, int B) AxudpPorts { get; init; } = (27401, 27402);
    public ((string Host, int Port) A, (string Host, int Port) B)? NetSim { get; init; }

    public int EffectiveK => K ?? 4;

    public string Describe() =>
        $"{Channel} k={EffectiveK} T1={(T1 is { } t1 ? $"{t1.TotalMilliseconds:F0}ms" : "def")} " +
        $"T2={(T2 is { } t2 ? $"{t2.TotalMilliseconds:F0}ms" : "def")} paclen={Paclen} " +
        $"ack={(AckMode ? "on" : "off")}{(T1FromTxComplete ? " t1tx" : "")} baud={(Baud > 0 ? Baud.ToString(System.Globalization.CultureInfo.InvariantCulture) : "∞")} " +
        $"{(HalfDuplex ? "half" : "full")}-duplex loss={Loss:0.###}" +
        (Srej ? " srej" : "") +
        (Bidirectional ? " bidi" : "") +
        (TimeScale != 1.0 ? $" ×{TimeScale:G3}" : "");
}
