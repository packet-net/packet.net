using Packet.Kiss.NinoTnc;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// Thin pass-through over a <see cref="NinoTncSerialPort"/> that drops
/// outbound frames probabilistically. Wrap each TNC in the lossy-transfer
/// test so the link sees scripted loss in both directions; the inbound path
/// is untouched so the session sees the surviving frames exactly as the
/// hardware delivered them.
/// </summary>
/// <remarks>
/// <para>
/// Drop happens before any bytes hit the serial port - so the dropped frame
/// is invisible to the partner TNC, identical to an RF channel where the
/// frame was corrupted into nothing.
/// </para>
/// <para>
/// The loss is an <b>independent per-transmission Bernoulli draw against a
/// non-seeded RNG</b> - deliberately <em>not</em> a replayed seeded sequence.
/// A real channel's loss is uncorrelated with the protocol's send schedule:
/// when a frame is retransmitted moments later, the channel gives it an
/// independent fate. A replayed seeded sequence does not - because both the
/// loss stream and the protocol are deterministic, the combined system can
/// settle into a limit cycle where the one frame that must get through (the
/// retransmitted window head) lands on a "drop" every recovery cycle, so the
/// link livelocks at high loss even though the protocol is correct (observed
/// at 30 % bidirectional, #214). Fresh draws break that: a transient stall
/// always escapes on the next cycle. The trade-off is that an exact run isn't
/// bit-reproducible - acceptable, and unavoidable anyway, on a hardware bench
/// whose own dropouts already vary run-to-run.
/// </para>
/// </remarks>
internal sealed class LossyHardwareSender
{
    private readonly NinoTncSerialPort port;
    private readonly double dropProbability;
    private readonly Random rng;
    private readonly object rngGate = new();
    private int sent;
    private int dropped;

    public LossyHardwareSender(NinoTncSerialPort port, double dropProbability)
    {
        if (dropProbability is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dropProbability),
                dropProbability, "drop probability must be in [0.0, 1.0]");
        }
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        this.dropProbability = dropProbability;
        this.rng = new Random();
    }

    public int SentCount => Volatile.Read(ref sent);
    public int DroppedCount => Volatile.Read(ref dropped);

    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        bool drop;
        if (dropProbability <= 0.0)
        {
            drop = false;
        }
        else
        {
            lock (rngGate)
            {
                drop = rng.NextDouble() < dropProbability;
            }
        }

        if (drop)
        {
            Interlocked.Increment(ref dropped);
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref sent);
        return port.SendFrameAsync(ax25Bytes, cancellationToken);
    }
}
