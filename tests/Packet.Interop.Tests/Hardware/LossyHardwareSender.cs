using Packet.Kiss.NinoTnc;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// Thin pass-through over a <see cref="NinoTncSerialPort"/> that drops
/// outbound frames probabilistically against a seeded RNG. Wrap each
/// TNC in the lossy-transfer test so the link sees scripted loss in
/// both directions; the inbound path is untouched so the session sees
/// the surviving frames exactly as the hardware delivered them.
/// </summary>
/// <remarks>
/// Drop happens before any bytes hit the serial port — so the dropped
/// frame is invisible to the partner TNC, identical to an RF channel
/// where the frame was corrupted into nothing. The seeded RNG keeps
/// flakiness bounded: same seed + same call order = same drop pattern.
/// </remarks>
internal sealed class LossyHardwareSender
{
    private readonly NinoTncSerialPort port;
    private readonly double dropProbability;
    private readonly Random rng;
    private readonly object rngGate = new();
    private int sent;
    private int dropped;

    public LossyHardwareSender(NinoTncSerialPort port, double dropProbability, int seed)
    {
        if (dropProbability is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dropProbability),
                dropProbability, "drop probability must be in [0.0, 1.0]");
        }
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        this.dropProbability = dropProbability;
        this.rng = new Random(seed);
    }

    public int SentCount    => Volatile.Read(ref sent);
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
