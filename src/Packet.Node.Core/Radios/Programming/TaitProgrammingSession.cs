using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
internal sealed partial class TaitProgrammingSession : IDisposable
{
    /// <summary>
    /// How long a run waits for the radio to start answering CCDI again before it gives the port
    /// back anyway. Every programming session ends by resetting the radio (the library's teardown
    /// sends <c>^</c>, and a committed write restarts it on the new codeplug regardless), so for
    /// several seconds afterwards there is nothing on the wire to find. Restoring the port into
    /// that window is what left a programmed port serving traffic with no radio control until
    /// someone restarted it by hand.
    /// </summary>
    internal static readonly TimeSpan RadioRestartWait = TimeSpan.FromSeconds(45);

    /// <summary>How often the radio is probed while waiting for it to come back.</summary>
    internal static readonly TimeSpan RadioRestartPoll = TimeSpan.FromSeconds(1);

    private const int MaxHistory = 500;

    private readonly PortRadioConfig radio;
    private readonly ILogger logger;
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
    private TaitRadioSettings? current;
    private string? error;
    private string? failedState;
    private DateTimeOffset? finishedAt;
    private Task? runTask;

    /// <summary>Create a run. Nothing happens until <see cref="Start"/>.</summary>
    /// <param name="portId">The port whose radio is being programmed.</param>
    /// <param name="mode">Whether the run writes the codeplug or only reads it.</param>
    /// <param name="plan">What to write, or null on a read-only run.</param>
    /// <param name="radio">The port's radio block (how to find the device when it is not open).</param>
    /// <param name="devicePathHint">The device path already known from the live radio or the config,
    /// or null to resolve it once the port is down.</param>
    /// <param name="gateway">Node-host operations (port down / up, device resolution).</param>
    /// <param name="writer">The hardware seam.</param>
    /// <param name="backupDirectory">Where to snapshot the pre-change codeplug, or null.</param>
    /// <param name="logger">Where a run failure is logged, with the exception, for journalctl.</param>
    /// <param name="clock">Time source for event timestamps.</param>
    internal TaitProgrammingSession(
        string portId,
        TaitProgramMode mode,
        TaitProgramPlan? plan,
        PortRadioConfig radio,
        string? devicePathHint,
        ITaitProgrammingGateway gateway,
        ITaitCodeplugWriter writer,
        string? backupDirectory,
        ILogger logger,
        TimeProvider clock)
    {
        PortId = portId;
        Mode = mode;
        Plan = plan;
        this.logger = logger;
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

    /// <summary>Whether this run writes the codeplug or only reads it.</summary>
    internal TaitProgramMode Mode { get; }

    /// <summary>What this run is writing, or null on a read-only run.</summary>
    internal TaitProgramPlan? Plan { get; }

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
                    PortId, TaitProgramModes.ToWire(Mode), TaitProgramStates.ToWire(state), StartedAt,
                    finishedAt, devicePath, Plan?.ToWire(), current, radioModel, radioSerial, backupPath,
                    error, failedState,
                    [.. history.Where(e => !string.IsNullOrWhiteSpace(e.Message)).Select(e => e.Message!)]);
            }
        }
    }

    /// <summary>Begin the run on a background task. Call once.</summary>
    internal void Start()
    {
        Publish("state", TaitProgramState.Starting, Plan is null
            ? $"reading the codeplug of the radio on {PortId}"
            : $"programming {PortId}: {Plan}");
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
            Publish("state", TaitProgramState.Done, Plan is null
                ? "done - the codeplug was read and the port is back in service"
                : "done - the radio is programmed, it has restarted on the new codeplug, and the port is back in service");
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
            Fail(ex);
        }
    }

    private async Task ProgramAsync(CancellationToken cancellationToken)
    {
        string path = devicePath ?? await LocateAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            devicePath = path;
        }

        Log($"port stopped; {(Plan is null ? "reading" : "programming")} the radio on {path}");

        var outcome = await Task.Run(
            () => writer.Program(path, Plan, backupDirectory, Report, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            radioModel = outcome.Model;
            radioSerial = outcome.Serial;
            backupPath = outcome.BackupPath;
            current = outcome.Current;
        }

        await WaitForRadioAsync(path, cancellationToken).ConfigureAwait(false);

        Publish("state", TaitProgramState.Restoring, Plan is null
            ? "codeplug read; bringing the port back into service"
            : $"{outcome.RecordsWritten} records written; bringing the port back into service");
    }

    /// <summary>
    /// Wait for the just-reset radio to answer CCDI again, so the port restore that follows finds
    /// it instead of coming up degraded. Best-effort in both directions: the run is never failed
    /// over this (the codeplug is written either way), and a radio that has not come back inside
    /// <see cref="RadioRestartWait"/> is reported and the port given back regardless - the
    /// supervisor's own bring-up retry gets the next few seconds' worth of attempts.
    /// </summary>
    private async Task WaitForRadioAsync(string path, CancellationToken cancellationToken)
    {
        Log($"waiting for the radio on {path} to restart and answer its control channel again");
        var deadline = clock.GetUtcNow() + RadioRestartWait;
        while (true)
        {
            if (await gateway.ProbeRadioAsync(radio, path, cancellationToken).ConfigureAwait(false))
            {
                Log("the radio is answering again");
                return;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                Log("the radio has not answered its control channel within " +
                    $"{(int)RadioRestartWait.TotalSeconds} s; bringing the port back anyway - it will " +
                    "run without radio control until the radio is back, and a port restart picks it up");
                return;
            }

            await Task.Delay(RadioRestartPoll, clock, cancellationToken).ConfigureAwait(false);
        }
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

    private void Fail(Exception ex)
    {
        TaitProgramState during;
        lock (gate)
        {
            during = state;
        }

        // Whatever the panel ends up showing, the full exception is in the node's log: an
        // unexpected fault is only ever diagnosable from the stack trace, and a run that failed on
        // a radio the operator has since walked away from cannot be reproduced on demand.
        LogRunFailed(logger, PortId, TaitProgramStates.ToWire(during), ex);

        string reason = Describe(ex, during);
        lock (gate)
        {
            error = reason;
            failedState = TaitProgramStates.ToWire(during);
        }

        Publish(
            "state", TaitProgramState.Failed,
            $"failed while {DescribeState(during)} - the port is back in service",
            error: reason,
            failedState: TaitProgramStates.ToWire(during));
    }

    private void Publish(
        string kind, TaitProgramState newState, string? message, double? fraction = null,
        string? error = null, string? failedState = null)
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
                kind, clock.GetUtcNow(), TaitProgramStates.ToWire(newState), message, fraction, error,
                failedState);

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

    /// <summary>What the run was doing, in words, for the failure line.</summary>
    private static string DescribeState(TaitProgramState state) => state switch
    {
        TaitProgramState.Starting => "taking the port out of service",
        TaitProgramState.PowerCycle => "waiting for the radio to enter programming mode",
        TaitProgramState.Reading => "reading the codeplug",
        TaitProgramState.Writing => "writing the codeplug",
        TaitProgramState.Restoring => "bringing the port back into service",
        _ => "running",
    };

    /// <summary>
    /// An exception as the operator should read it. The library's own messages are already written
    /// for a human ("refusing to write: the radio's database version..."), so they pass through; a
    /// serial-open failure gets the device path and the two things worth checking; anything else is
    /// prefixed with its type so an unexpected fault is still identifiable. Inner exceptions are
    /// appended, because a serial fault's reason usually lives one level down.
    /// </summary>
    /// <remarks>
    /// A timeout means two completely different things depending on when it lands, and saying the
    /// wrong one sends the operator to power-cycle a radio that is already talking: before the
    /// handshake it is "you did not power-cycle it", and after it is "it stopped answering
    /// mid-transfer", which is a cable, a baud rate or a radio that rejected a command.
    /// </remarks>
    private string Describe(Exception ex, TaitProgramState during) => ex switch
    {
        TimeoutException when during is TaitProgramState.PowerCycle =>
            "the radio never entered programming mode. It only latches programming mode as it boots, so it has " +
            "to be power-cycled while the run is asking for it - and the programming lead has to be on " +
            $"{devicePath ?? "the radio's data connector"}. ({ex.Message})",
        TimeoutException =>
            $"the radio stopped answering while {DescribeState(during)}: {ex.Message}. It had already entered " +
            "programming mode, so this is the link or the radio refusing a command rather than a missed " +
            "power-cycle - check the lead, and try again with a fresh power-cycle.",
        UnauthorizedAccessException or IOException =>
            $"could not use the radio's serial port{(devicePath is { } path ? $" ({path})" : string.Empty)}: " +
            $"{Chain(ex)}. Is the cable on that device, and is anything else holding it open?",
        InvalidOperationException or NotSupportedException or ArgumentException => Chain(ex),
        _ => $"{ex.GetType().Name}: {Chain(ex)}",
    };

    /// <summary>An exception's message plus its inner messages, which is where a serial fault's
    /// actual reason usually is.</summary>
    private static string Chain(Exception ex)
    {
        string text = ex.Message;
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += $" ({inner.Message})";
        }

        return text;
    }

    [LoggerMessage(EventId = 7793, Level = LogLevel.Error,
        Message = "port {PortId}: the codeplug programming run failed while in state {State}")]
    private static partial void LogRunFailed(ILogger logger, string portId, string state, Exception exception);

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
