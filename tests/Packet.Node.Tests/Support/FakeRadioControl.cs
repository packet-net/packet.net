using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Radio;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A scripted <see cref="IRadioControl"/> for supervisor tests: advertises RSSI (and
/// carrier-sense) capability, answers RSSI polls with a settable value, counts the
/// carrier-sense reads the AX.25 medium-access gate makes through it, and records
/// its disposal (optionally into a shared ordering log) so tests can assert the
/// radio outlives the RSSI-tagging wrapper that samples it.
/// </summary>
public sealed class FakeRadioControl(List<string>? disposalLog = null, string name = "radio") : IRadioControl
{
    private int disposed;

    // Scripted DCD, held as an int so the read side is one atomic read: the CSMA gate polls
    // ChannelBusy from its own task while the test thread scripts edges, and a bool? is two
    // fields (HasValue + Value) that can tear. 0 = unknown (no DCD report yet), 1 = busy, 2 = clear.
    private const int Unknown = 0;
    private const int Busy = 1;
    private const int Clear = 2;
    private int carrierState = Unknown;

    private int channelBusyReads;
    private int busyChannelBusyReads;

    /// <summary>What <see cref="ReadRssiDbmAsync"/> answers.</summary>
    public float RssiDbm { get; set; } = -100f;

    /// <summary>True once <see cref="DisposeAsync"/> ran.</summary>
    public bool Disposed => disposed != 0;

    /// <inheritdoc/>
    public RadioCapabilities Capabilities { get; init; } =
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense;

    /// <inheritdoc/>
    public bool? ChannelBusy
    {
        get
        {
            var state = Volatile.Read(ref carrierState);
            Interlocked.Increment(ref channelBusyReads);
            if (state == Busy)
            {
                Interlocked.Increment(ref busyChannelBusyReads);
            }
            return state switch { Busy => true, Clear => false, _ => null };
        }
    }

    /// <summary>
    /// How many times <see cref="ChannelBusy"/> has been read, whatever it answered.
    /// </summary>
    public int ChannelBusyReads => Volatile.Read(ref channelBusyReads);

    /// <summary>
    /// How many of those reads answered <b>busy</b>: the deferral observable. The AX.25
    /// stack's medium-access gate (<c>Packet.Ax25.Session.CarrierSenseGate</c>) reads this
    /// seam once on entry and once more per slot time inside its wait loop, reaching this
    /// fake through the node's <c>RadioCarrierSense</c> adapter, which is a pure read-through
    /// and caches nothing. So a count that climbs while the channel is busy is direct evidence
    /// that a frame reached the gate and is being <em>held</em> there, the positive a test
    /// must observe before "nothing was heard on the medium" means anything. Nothing else in a
    /// supervisor test reads this seam unprompted: the generic radio-status monitor only reads
    /// it inside <c>Snapshot()</c> (an API request), so with the virtual clock un-advanced the
    /// gate is the only reader.
    /// </summary>
    public int BusyChannelBusyReads => Volatile.Read(ref busyChannelBusyReads);

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
        Volatile.Write(ref carrierState, busy ? Busy : Clear);
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
    // How many more creates the fault applies to: int.MaxValue for a permanent Fault(), a count
    // for FaultTimes().
    private int faultsRemaining;

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
        faultsRemaining = int.MaxValue;
        return this;
    }

    /// <summary>Make the next <paramref name="times"/> creates throw and the ones after that
    /// succeed - a radio that is briefly not there (one rebooting after a codeplug write), which
    /// the supervisor's bounded open retry is supposed to ride out.</summary>
    public FakeRadioControlFactory FaultTimes(int times, Exception? ex = null)
    {
        fault = ex ?? new IOException("fake radio control is not answering yet");
        faultsRemaining = times;
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
        if (fault is not null && faultsRemaining > 0)
        {
            if (faultsRemaining != int.MaxValue)
            {
                faultsRemaining--;
            }
            throw fault;
        }
        if (radios.TryDequeue(out var provided))
        {
            return Task.FromResult(provided);
        }
        throw new InvalidOperationException("no fake radio was provided for this request.");
    }
}
