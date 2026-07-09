using System.Threading.Channels;

namespace Packet.Tune.Core;

/// <summary>How a hail ended.</summary>
public enum StationHailOutcome
{
    /// <summary>The peer answered with its <see cref="StationStatus"/>.</summary>
    Answered,

    /// <summary>The hail was sent but no status came back before the timeout — the peer's
    /// radio may be off, unprogrammed for SDM, or out of range on the side channel.</summary>
    NoReply,

    /// <summary>The hail could not be delivered over the side channel (no receipt after the
    /// link's own retries) and no status came back either.</summary>
    LinkFailed,
}

/// <summary>The result of a hail.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Status">The peer's status when <see cref="StationHailOutcome.Answered"/>;
/// <c>null</c> otherwise.</param>
/// <param name="Detail">Human-facing detail (failure stage / reason), or <c>null</c>.</param>
public sealed record StationHailResult(StationHailOutcome Outcome, StationStatus? Status, string? Detail)
{
    /// <summary>True when the peer answered.</summary>
    public bool Success => Outcome == StationHailOutcome.Answered;
}

/// <summary>Tunables for <see cref="StationHailer"/>.</summary>
public sealed record StationHailerOptions
{
    /// <summary>How many times to (re-)send the hail while waiting for a status. Each attempt
    /// re-sends the hail (the underlying link retries delivery internally) and waits
    /// <see cref="ReplyTimeout"/> for the reply. Default 2.</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>How long to wait for the <c>STAT</c> reply after a hail is sent, per attempt.
    /// Default 30 s — a delivered+receipted hail plus the responder's post-receive auto-ack
    /// guard plus an extended-SDM status reply all cost seconds over the side channel.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The hailing end of the station-hail protocol: sends a <see cref="StationHail"/> over an
/// <see cref="ITuningLink"/> (canonically <see cref="SdmTuningLink"/> over the radios' own
/// FFSK side channel) and awaits the peer's <see cref="StationStatus"/>, with retries. Because
/// the side channel is mode/channel-agnostic, a hail succeeds — and reports the peer's mode —
/// even when the packet path between the two stations is broken by a mode mismatch.
/// </summary>
public sealed class StationHailer : IAsyncDisposable
{
    private readonly ITuningLink link;
    private readonly StationHailerOptions options;
    private readonly string? requesterCallsign;
    private readonly Channel<TuningTelegram> inbox = Channel.CreateUnbounded<TuningTelegram>();
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Task pumpLoop;
    private int sequence = TuningTelegram.NewSessionSequenceBase();

    /// <summary>Create over a link. The link's lifetime stays the caller's.</summary>
    /// <param name="link">The (mode-agnostic) telegram link to the peer.</param>
    /// <param name="requesterCallsign">This station's callsign, carried in the hail so the
    /// peer can log who hailed it. Null omits it.</param>
    /// <param name="options">Tunables; null = defaults.</param>
    public StationHailer(ITuningLink link, string? requesterCallsign = null, StationHailerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        this.link = link;
        this.requesterCallsign = requesterCallsign;
        this.options = options ?? new StationHailerOptions();
        pumpLoop = Task.Run(() => PumpAsync(pumpCts.Token));
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Hail the peer and return its status (or why not).</summary>
    public async Task<StationHailResult> HailAsync(CancellationToken cancellationToken = default)
    {
        var hail = new StationHail { RequesterCallsign = requesterCallsign };
        string? lastLinkError = null;

        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            Log?.Invoke($"hailer: sending hail (attempt {attempt}/{options.MaxAttempts})");
            try
            {
                await link.SendAsync(hail.ToTelegram(NextSequence()), cancellationToken).ConfigureAwait(false);
            }
            catch (TuningLinkException ex)
            {
                // Delivery unconfirmed after the link's own retries. The peer may still have
                // heard it (a lost receipt) — wait for a reply anyway before giving up.
                lastLinkError = ex.Message;
                Log?.Invoke($"hailer: hail delivery unconfirmed ({ex.Message}) — waiting for a reply regardless");
            }

            var reply = await WaitForStatusAsync(options.ReplyTimeout, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                Log?.Invoke($"hailer: status received from {reply.Callsign}");
                return new StationHailResult(StationHailOutcome.Answered, reply, null);
            }
            Log?.Invoke($"hailer: no status within {options.ReplyTimeout.TotalSeconds:0}s");
        }

        return lastLinkError is null
            ? new StationHailResult(StationHailOutcome.NoReply,
                null, $"no status reply after {options.MaxAttempts} hail(s)")
            : new StationHailResult(StationHailOutcome.LinkFailed,
                null, $"hail undelivered ({lastLinkError}) and no reply after {options.MaxAttempts} attempt(s)");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await pumpLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        pumpCts.Dispose();
    }

    private async Task<StationStatus?> WaitForStatusAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var telegram = await inbox.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                if (StationStatus.TryFromTelegram(telegram, out var status) && status is not null)
                {
                    return status;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var telegram in link.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                inbox.Writer.TryWrite(telegram);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            inbox.Writer.TryComplete();
        }
    }

    private int NextSequence() => Interlocked.Increment(ref sequence);
}
