using System.Threading.Channels;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// One codeplug-programming run on one port: take the port out of service, drive the radio's
/// programming interface, put the port back, and narrate the whole thing to whoever is watching.
/// </summary>
/// <remarks>
/// <para>
/// The run outlives the HTTP request that started it - it is minutes long, most of them spent
/// waiting for a human to power-cycle a radio - so the POST accepts it and returns, and the operator
/// watches the SSE feed. <see cref="Subscribe"/> replays the run's whole history before going live,
/// so opening the feed late (or re-opening it after a browser reload) shows everything that has
/// happened rather than a blank panel.
/// </para>
/// <para>
/// <b>The port always comes back.</b> The gateway's <c>RunWithPortDownAsync</c> restores it in a
/// finally, so a throw, a cancel and a clean finish all end with the port back in service. A
/// terminal state is only published after that has happened.
/// </para>
/// </remarks>
internal sealed class TaitProgrammingSession : IDisposable
{
    private const int MaxHistory = 500;

    private readonly PortRadioConfig radio;
    private readonly ITaitProgrammingGateway gateway;
    private readonly ITaitCodeplugWriter writer;
    private readonly string? backupDirectory;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly List<TaitProgramEvent> history = [];
    private readonly Dictionary<Guid, ChannelWriter<TaitProgramEvent>> subscribers = [];

    private TaitProgramState state = TaitProgramState.Starting;
    private string? devicePath;
    private string? radioModel;
    private string? radioSerial;
    private string? backupPath;
    private string? error;
    private DateTimeOffset? finishedAt;
    private Task? runTask;

    /// <summary>Create a run. Nothing happens until <see cref="Start"/>.</summary>
    /// <param name="portId">The port whose radio is being programmed.</param>
    /// <param name="plan">What to write.</param>
    /// <param name="radio">The port's radio block (how to find the device when it is not open).</param>
    /// <param name="devicePathHint">The device path already known from the live radio or the config,
    /// or null to resolve it once the port is down.</param>
    /// <param name="gateway">Node-host operations (port down / up, device resolution).</param>
    /// <param name="writer">The hardware seam.</param>
    /// <param name="backupDirectory">Where to snapshot the pre-change codeplug, or null.</param>
    /// <param name="clock">Time source for event timestamps.</param>
    internal TaitProgrammingSession(
        string portId,
        TaitProgramPlan plan,
        PortRadioConfig radio,
        string? devicePathHint,
        ITaitProgrammingGateway gateway,
        ITaitCodeplugWriter writer,
        string? backupDirectory,
        TimeProvider clock)
    {
        PortId = portId;
        Plan = plan;
        this.radio = radio;
        devicePath = devicePathHint;
        this.gateway = gateway;
        this.writer = writer;
        this.backupDirectory = backupDirectory;
        this.clock = clock;
        StartedAt = clock.GetUtcNow();
    }

    /// <summary>The port this run holds (the registry key).</summary>
    internal string PortId { get; }

    /// <summary>What this run is writing.</summary>
    internal TaitProgramPlan Plan { get; }

    /// <summary>When the run was accepted.</summary>
    internal DateTimeOffset StartedAt { get; }

    /// <summary>Whether the run has finished (successfully or not).</summary>
    internal bool IsTerminal
    {
        get
        {
            lock (gate)
            {
                return TaitProgramStates.IsTerminal(state);
            }
        }
    }

    /// <summary>The API projection of this run.</summary>
    internal TaitProgramInfo Info
    {
        get
        {
            lock (gate)
            {
                return new TaitProgramInfo(
                    PortId, TaitProgramStates.ToWire(state), StartedAt, finishedAt, devicePath,
                    Plan.ToWire(), radioModel, radioSerial, backupPath, error);
            }
        }
    }

    /// <summary>Begin the run on a background task. Call once.</summary>
    internal void Start()
    {
        Publish("state", TaitProgramState.Starting, $"programming {PortId}: {Plan}");
        runTask = Task.Run(RunAsync);
    }

    /// <summary>
    /// Abandon the run. The radio is left as it was unless the write block had already opened - past
    /// that point a write always runs to its commit, because stopping half way would leave the
    /// codeplug open and partly applied. Returns once the run has finished and the port is back.
    /// </summary>
    internal async ValueTask CancelAsync()
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;   // already disposed: the run is long over.
        }

        if (runTask is { } task)
        {
            // RunAsync catches everything and turns it into a terminal event, so this never throws.
            await task.ConfigureAwait(false);
        }
    }

    /// <summary>Release the run's cancellation source. Only valid once the run has finished - the
    /// service disposes a run when it supersedes it or at node shutdown, both after
    /// <see cref="CancelAsync"/>.</summary>
    public void Dispose() => cancellation.Dispose();

    /// <summary>
    /// Subscribe to the run's event feed: every event so far, then live ones. A run that has already
    /// finished replays its history and completes the reader immediately.
    /// </summary>
    /// <param name="reader">The channel to read <see cref="TaitProgramEvent"/>s from.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes.</returns>
    internal IDisposable Subscribe(out ChannelReader<TaitProgramEvent> reader)
    {
        var channel = Channel.CreateUnbounded<TaitProgramEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        lock (gate)
        {
            foreach (var e in history)
            {
                channel.Writer.TryWrite(e);
            }

            if (TaitProgramStates.IsTerminal(state))
            {
                channel.Writer.TryComplete();
            }
            else
            {
                subscribers[id] = channel.Writer;
            }
        }

        reader = channel.Reader;
        return new Subscription(this, id);
    }

    private async Task RunAsync()
    {
        try
        {
            await gateway.RunWithPortDownAsync(PortId, ProgramAsync, cancellation.Token).ConfigureAwait(false);
            Publish("state", TaitProgramState.Done, "done - the radio is programmed and the port is back in service");
        }
        catch (OperationCanceledException)
        {
            Publish("state", TaitProgramState.Cancelled, "cancelled - the port is back in service");
        }
        catch (Exception ex)
        {
            // Every failure mode ends here: no radio on the bus, the operator never power-cycled it
            // (a TimeoutException from the connect probe), an unvalidated codeplug database version,
            // a frequency the radio's band split does not cover, a serial fault mid-transfer. The
            // operator gets the reason, not a status code.
            Fail(Describe(ex));
        }
    }

    private async Task ProgramAsync(CancellationToken cancellationToken)
    {
        string path = devicePath ?? await LocateAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            devicePath = path;
        }

        Log($"port stopped; programming the radio on {path}");

        var outcome = await Task.Run(
            () => writer.Program(path, Plan, backupDirectory, Report, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            radioModel = outcome.Model;
            radioSerial = outcome.Serial;
            backupPath = outcome.BackupPath;
        }

        Publish("state", TaitProgramState.Restoring,
            $"{outcome.RecordsWritten} records written; bringing the port back into service");
    }

    private async Task<string> LocateAsync(CancellationToken cancellationToken)
    {
        // Only reached for a serial-bound radio whose port was not running, so the live handle could
        // not supply the path. The scan opens candidate serial ports, which is why it happens here -
        // with the port already down - rather than on the request thread.
        Log($"looking for the radio with CCDI serial {radio.Serial}");
        return await gateway.ResolveDevicePathAsync(radio, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A note on the feed that does not move the run on - it carries whatever state the run
    /// is already in, so a log line can never drag the panel backwards.</summary>
    private void Log(string message)
    {
        TaitProgramState current;
        lock (gate)
        {
            current = state;
        }

        Publish("log", current, message);
    }

    private void Report(TaitProgramState reported, double? fraction, string message)
    {
        // Progress from inside the programmer: a state change when it moves on, a fraction while it
        // stays put. Both go on the feed; the panel renders the latest of each.
        bool moved;
        lock (gate)
        {
            moved = state != reported;
        }

        Publish(moved ? "state" : "progress", reported, message, fraction);
    }

    private void Fail(string reason)
    {
        lock (gate)
        {
            error = reason;
        }

        Publish("state", TaitProgramState.Failed, "failed - the port is back in service", error: reason);
    }

    private void Publish(string kind, TaitProgramState newState, string? message, double? fraction = null, string? error = null)
    {
        TaitProgramEvent evt;
        lock (gate)
        {
            state = newState;
            if (TaitProgramStates.IsTerminal(newState))
            {
                finishedAt ??= clock.GetUtcNow();
            }

            evt = new TaitProgramEvent(
                kind, clock.GetUtcNow(), TaitProgramStates.ToWire(newState), message, fraction, error);

            history.Add(evt);
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(0);
            }

            foreach (var w in subscribers.Values)
            {
                w.TryWrite(evt);
            }

            if (TaitProgramStates.IsTerminal(newState))
            {
                foreach (var w in subscribers.Values)
                {
                    w.TryComplete();
                }

                subscribers.Clear();
            }
        }
    }

    /// <summary>An exception as the operator should read it. The library's own messages are already
    /// written for a human ("power-cycle it as the read is triggered", "refusing to write: the
    /// radio's database version..."), so they pass through; a serial-open failure gets the device
    /// path and the two things worth checking; anything else is prefixed with its type so an
    /// unexpected fault is still identifiable.</summary>
    private string Describe(Exception ex) => ex switch
    {
        TimeoutException => "the radio never entered programming mode - power-cycle it while the run is asking you to. " + ex.Message,
        UnauthorizedAccessException or IOException =>
            $"could not open the radio's serial port{(devicePath is { } path ? $" ({path})" : string.Empty)}: {ex.Message}. " +
            "Is the cable on that device, and is anything else holding it open?",
        InvalidOperationException or NotSupportedException or ArgumentException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private sealed class Subscription(TaitProgrammingSession owner, Guid id) : IDisposable
    {
        private int disposedFlag;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }

            lock (owner.gate)
            {
                if (owner.subscribers.Remove(id, out var writer))
                {
                    writer.TryComplete();
                }
            }
        }
    }
}
