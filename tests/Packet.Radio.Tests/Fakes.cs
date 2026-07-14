using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Rig;

namespace Packet.Radio.Tests;

/// <summary>Scripted <see cref="IRadioControl"/>: RSSI comes from a settable value, busy state
/// and carrier-sense edges are driven by the test, and every RSSI read releases a semaphore so
/// tests can await "the sampler has polled N times" deterministically.</summary>
internal sealed class FakeRadio : IRadioControl
{
    private readonly SemaphoreSlim reads = new(0);

    public RadioCapabilities Capabilities { get; init; } =
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense | RadioCapabilities.TransmitterControl;

    public float RssiDbm { get; set; } = -128f;

    public bool? ChannelBusy { get; set; }

    public bool? Transmitting { get; private set; }

    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default)
    {
        reads.Release();
        return ValueTask.FromResult(RssiDbm);
    }

    public ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        Transmitting = transmit;
        return ValueTask.CompletedTask;
    }

    public void RaiseCarrierSense(bool busy, DateTimeOffset at)
    {
        ChannelBusy = busy;
        CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, at));
    }

    /// <summary>Await the next <paramref name="count"/> RSSI polls (with a real-time guard so a
    /// hung sampler fails the test rather than deadlocking it).</summary>
    public async Task WaitForReadsAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            (await reads.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("sampler should keep polling");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Scripted <see cref="IRigControl"/> for the rig→radio bridge tests: settable
/// capabilities, DCD, signal strength and PTT answered instantly from memory, receive-side reads
/// scriptable to fail (<see cref="ReadFault"/>), and counters/flags for cadence and disposal
/// assertions. The station-control members the bridge never touches (frequency/mode/meters)
/// throw <see cref="NotSupportedException"/>.</summary>
internal sealed class FakeRig : IRigControl
{
    private int disposed;
    private int dcdReads;
    private int strengthReads;
    private int pttSets;

    public RigCapabilities Capabilities { get; init; } =
        RigCapabilities.DcdRead | RigCapabilities.SignalStrengthRead | RigCapabilities.PttSet;

    public RigInfo Info { get; init; } = new("Fake rig", "Acme", "Fake-1000");

    public bool Dcd { get; set; }

    public double StrengthDbm { get; set; } = -120;

    public bool Ptt { get; private set; }

    /// <summary>When set, every receive-side read throws it — models a bounced daemon mid-poll.</summary>
    public RigException? ReadFault { get; set; }

    /// <summary>Number of DCD reads attempted (faulted ones included) — cadence assertions.</summary>
    public int DcdReads => Volatile.Read(ref dcdReads);

    /// <summary>Number of signal-strength reads attempted — delegation assertions.</summary>
    public int StrengthReads => Volatile.Read(ref strengthReads);

    /// <summary>Number of PTT commands issued — no-redundant-unkey assertions.</summary>
    public int PttSets => Volatile.Read(ref pttSets);

    /// <summary>True once <see cref="DisposeAsync"/> ran.</summary>
    public bool Disposed => disposed != 0;

    private T Answer<T>(T value)
        => ReadFault is { } fault ? throw fault : value;

    public ValueTask<bool> ReadDcdAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref dcdReads);
        return new(Answer(Dcd));
    }

    public ValueTask<double> ReadSignalStrengthDbmAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref strengthReads);
        return new(Answer(StrengthDbm));
    }

    public ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref pttSets);
        Ptt = transmit;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask SetModeAsync(RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Push-driven <see cref="IAx25Transport"/>: tests push inbound frames, and sends are
/// recorded with a completion the test can hold open.</summary>
internal sealed class FakeTransport : IAx25Transport
{
    private readonly Channel<Ax25InboundFrame> inbound = Channel.CreateUnbounded<Ax25InboundFrame>();

    public List<ReadOnlyMemory<byte>> Sent { get; } = [];

    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(ax25);
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    public void Push(Ax25InboundFrame frame) => inbound.Writer.TryWrite(frame);

    public ValueTask DisposeAsync()
    {
        inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
