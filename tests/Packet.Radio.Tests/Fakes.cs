using System.Threading.Channels;
using Packet.Ax25.Transport;

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
