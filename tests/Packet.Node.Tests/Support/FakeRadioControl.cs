using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Radio;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A scripted <see cref="IRadioControl"/> for supervisor tests: advertises RSSI (and
/// carrier-sense) capability, answers RSSI polls with a settable value, and records
/// its disposal (optionally into a shared ordering log) so tests can assert the
/// radio outlives the RSSI-tagging wrapper that samples it.
/// </summary>
public sealed class FakeRadioControl(List<string>? disposalLog = null, string name = "radio") : IRadioControl
{
    private int disposed;
    private bool? channelBusy;

    /// <summary>What <see cref="ReadRssiDbmAsync"/> answers.</summary>
    public float RssiDbm { get; set; } = -100f;

    /// <summary>True once <see cref="DisposeAsync"/> ran.</summary>
    public bool Disposed => disposed != 0;

    /// <inheritdoc/>
    public RadioCapabilities Capabilities { get; init; } =
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense;

    /// <inheritdoc/>
    public bool? ChannelBusy => channelBusy;

    /// <inheritdoc/>
    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    /// <inheritdoc/>
    public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default) =>
        new(RssiDbm);

    /// <inheritdoc/>
    public ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the fake radio has no transmitter control.");

    /// <summary>Script a hardware DCD edge (drives <see cref="CarrierSenseChanged"/>).</summary>
    public void RaiseCarrierSense(bool busy, DateTimeOffset at)
    {
        channelBusy = busy;
        CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, at));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            disposalLog?.Add(name);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A test <see cref="IRadioControlFactory"/>: hands out pre-supplied fake radios in
/// order (or throws a scripted fault, to test the degrade-cleanly path) instead of
/// opening real serial hardware. Records every request so tests can assert what the
/// supervisor asked for.
/// </summary>
public sealed class FakeRadioControlFactory : IRadioControlFactory
{
    private readonly Queue<IRadioControl> radios = new();
    private Exception? fault;

    /// <summary>Every <see cref="PortRadioConfig"/> the supervisor requested, in order.</summary>
    public List<PortRadioConfig> Requests { get; } = [];

    /// <summary>Supply the radio(s) to hand out, in order.</summary>
    public FakeRadioControlFactory Provide(params IRadioControl[] provided)
    {
        foreach (var r in provided)
        {
            radios.Enqueue(r);
        }
        return this;
    }

    /// <summary>Make every subsequent create throw (models a control cable that
    /// won't open), to test the port's radio-degrade path.</summary>
    public FakeRadioControlFactory Fault(Exception? ex = null)
    {
        fault = ex ?? new IOException("fake radio control refused to open");
        return this;
    }

    /// <inheritdoc/>
    public Task<IRadioControl> CreateAsync(
        PortRadioConfig radio,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        PortRigConfig? rig = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(radio);
        if (fault is not null)
        {
            throw fault;
        }
        if (radios.TryDequeue(out var provided))
        {
            return Task.FromResult(provided);
        }
        throw new InvalidOperationException("no fake radio was provided for this request.");
    }
}
