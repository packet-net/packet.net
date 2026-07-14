using Packet.Node.Core.Configuration;
using Packet.Node.Core.Rigs;
using Packet.Rig;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A scripted <see cref="IRigControl"/> for supervisor/monitor tests: settable frequency, mode,
/// PTT and meters, answered instantly from memory; records its disposal (optionally into a
/// shared ordering log) so tests can assert the rig outlives the status poller that reads it.
/// Reads can be scripted to fail (<see cref="ReadFault"/>) to exercise the faulted projection.
/// </summary>
public sealed class FakeRigControl(List<string>? disposalLog = null, string name = "rig") : IRigControl
{
    private int disposed;

    public long FrequencyHz { get; set; } = 14_074_000;
    public string ModeToken { get; set; } = "PKTUSB";
    public int? PassbandHz { get; set; } = 3000;
    public bool Ptt { get; set; }
    public double Swr { get; set; } = 1.2;
    public double RfPowerWatts { get; set; } = 50;
    public double RfPowerRelative { get; set; } = 0.5;

    /// <summary>When set, every read throws it — models a bounced daemon mid-poll.</summary>
    public RigException? ReadFault { get; set; }

    private int frequencyReads;
    private int swrReads;

    /// <summary>Number of frequency reads served — cadence assertions.</summary>
    public int FrequencyReads => Volatile.Read(ref frequencyReads);

    /// <summary>Number of SWR reads served — meters-only-while-keyed assertions.</summary>
    public int SwrReads => Volatile.Read(ref swrReads);

    /// <summary>True once <see cref="DisposeAsync"/> ran.</summary>
    public bool Disposed => disposed != 0;

    /// <inheritdoc/>
    public RigCapabilities Capabilities { get; init; } =
        RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet |
        RigCapabilities.ModeGet | RigCapabilities.ModeSet |
        RigCapabilities.PttGet | RigCapabilities.PttSet |
        RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeter | RigCapabilities.RfPowerMeterWatts;

    /// <inheritdoc/>
    public RigInfo Info { get; init; } = new("Fake rig", "Acme", "Fake-1000");

    private T Answer<T>(T value)
        => ReadFault is { } fault ? throw fault : value;

    /// <inheritdoc/>
    public ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref frequencyReads);
        return new(Answer(FrequencyHz));
    }

    /// <inheritdoc/>
    public ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default)
    {
        FrequencyHz = frequencyHz;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default) =>
        new(Answer(new RigModeState(RigMode.From(ModeToken), PassbandHz)));

    /// <inheritdoc/>
    public ValueTask SetModeAsync(RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default)
    {
        ModeToken = mode.Token;
        PassbandHz = passbandHz;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default) =>
        new(Answer(Ptt));

    /// <inheritdoc/>
    public ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        Ptt = transmit;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref swrReads);
        return new(Answer(Swr));
    }

    /// <inheritdoc/>
    public ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default) =>
        new(Answer(RfPowerRelative));

    /// <inheritdoc/>
    public ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default) =>
        new(Answer(RfPowerWatts));

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
/// A test <see cref="IRigControlFactory"/>: hands out pre-supplied fake rigs in order (or throws
/// a scripted fault, to test the degrade-cleanly path) instead of dialling real daemons.
/// Records every request so tests can assert what the supervisor asked for. (Mirrors
/// <see cref="FakeRadioControlFactory"/>.)
/// </summary>
public sealed class FakeRigControlFactory : IRigControlFactory
{
    private readonly Queue<IRigControl> rigs = new();
    private Exception? fault;

    /// <summary>Every <see cref="PortRigConfig"/> the supervisor requested, in order.</summary>
    public List<PortRigConfig> Requests { get; } = [];

    /// <summary>Supply the rig(s) to hand out, in order.</summary>
    public FakeRigControlFactory Provide(params IRigControl[] provided)
    {
        foreach (var r in provided)
        {
            rigs.Enqueue(r);
        }
        return this;
    }

    /// <summary>Make every subsequent create throw (models an unreachable daemon), to test the
    /// port's rig-degrade path.</summary>
    public FakeRigControlFactory Fault(Exception? ex = null)
    {
        fault = ex ?? new RigConnectionException("fake rig daemon refused the connection");
        return this;
    }

    /// <inheritdoc/>
    public Task<IRigControl> CreateAsync(
        PortRigConfig rig, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(rig);
        if (fault is not null)
        {
            throw fault;
        }
        if (rigs.Count == 0)
        {
            throw new InvalidOperationException("FakeRigControlFactory has no rig to provide — call Provide().");
        }
        return Task.FromResult(rigs.Dequeue());
    }
}
