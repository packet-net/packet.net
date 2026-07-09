using System.Globalization;
using System.Threading.Channels;

namespace Packet.Tune.Core;

/// <summary>
/// The answering (meter) end of the TXDELAY-minimisation protocol (see
/// <see cref="TxDelayMinimizer"/> for the choreography). Confirms the proposal, opens a
/// tagged probe counter on each <c>step</c>/<c>apply</c>, and reports decodes — plus,
/// when a carrier-sensing radio is attached, the median measured pre-data carrier time
/// per step (the as-heard effective TXDELAY, the sweep's self-evidencing cross-check).
/// The meter is entirely passive on its own hardware: nothing is keyed, nothing is
/// changed, so there is nothing to revert — a silent coordinator simply times the loop
/// out (<see cref="TxDelayMinOptions.MeterIdleTimeout"/>).
/// </summary>
public sealed class TxDelayMinResponder
{
    /// <summary>Probe counts the meter will confirm (a runaway k would keep the
    /// channel keyed for minutes per step).</summary>
    public const int MaxProbesPerStep = 20;

    private readonly ITuningLink link;
    private readonly ITxDelayMinStation station;
    private readonly TxDelayMinOptions options;
    private int sequence = TuningTelegram.NewSessionSequenceBase();

    /// <summary>Create over a link + station pair. The link's and station's lifetimes
    /// stay the caller's.</summary>
    public TxDelayMinResponder(ITuningLink link, ITxDelayMinStation station, TxDelayMinOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(station);
        this.link = link;
        this.station = station;
        this.options = options ?? new TxDelayMinOptions();
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Observer invoked as each step report goes out (drives live UIs /
    /// SSE feeds). Null = none.</summary>
    public Action<TxDelaySweepStep>? StepReported { get; set; }

    /// <summary>
    /// Run the meter loop until the coordinator says <c>done</c> (or <c>BY</c>) —
    /// returns 0 — or the link closes / the idle timeout fires — returns 1.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var inbox = Channel.CreateUnbounded<TuningTelegram>();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var telegram in link.ReceiveAsync(pumpCts.Token).ConfigureAwait(false))
                    {
                        inbox.Writer.TryWrite(telegram);
                    }
                }
                catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
                {
                }
                finally
                {
                    inbox.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        ITxDelayProbeCount? counter = null;
        (int TxDelayMs, int Count)? pendingStep = null;
        Log?.Invoke("txdmeter: ready — waiting for the coordinator's proposal");
        try
        {
            while (true)
            {
                TuningTelegram telegram;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readCts.CancelAfter(options.MeterIdleTimeout);
                    try
                    {
                        telegram = await inbox.Reader.ReadAsync(readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                            $"txdmeter: nothing heard for {options.MeterIdleTimeout.TotalMinutes:0} min — exiting"));
                        return 1;
                    }
                    catch (ChannelClosedException)
                    {
                        Log?.Invoke("txdmeter: link closed without done");
                        return 1;
                    }
                }

                if (telegram.Verb == TuningVerb.Bye)
                {
                    Log?.Invoke("txdmeter: coordinator said BY — session over");
                    return 0;
                }
                if (!TxDelayMinMessage.TryFromTelegram(telegram, out var message) || message is null)
                {
                    continue; // not ours (deviation/mode-coord verbs, unknown actions…)
                }

                switch (message.Action)
                {
                    case TxDelayMinAction.Propose:
                    {
                        int k = message.Count ?? 0;
                        if (k is < 1 or > MaxProbesPerStep)
                        {
                            Log?.Invoke($"txdmeter: rejecting sweep with {k} probes/step");
                            await TrySendAsync(
                                new TxDelayMinMessage { Action = TxDelayMinAction.Reject, Reason = "badk" },
                                cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                            $"txdmeter: confirming sweep from {message.StartTxDelayMs} ms " +
                            $"down in {message.StepMs} ms steps, {k} probes/step"));
                        await TrySendAsync(
                            new TxDelayMinMessage { Action = TxDelayMinAction.Confirm, Count = k },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    case TxDelayMinAction.Step:
                    case TxDelayMinAction.Apply:
                    {
                        // The tag every probe of this step carries is the announce
                        // telegram's sequence number. Open the counter NOW — the
                        // coordinator keys only after its wedge-guard delay, but the
                        // counter is passive and cheap, so first-open-then-wait can
                        // never miss an early probe.
                        counter?.Dispose();
                        counter = station.BeginProbeCount(telegram.Sequence);
                        pendingStep = (message.TxDelayMs ?? 0, message.Count ?? 0);
                        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                            $"txdmeter: {(message.Action == TxDelayMinAction.Apply ? "APPLY verify" : "step")} " +
                            $"{message.TxDelayMs} ms — counting probes tagged s{telegram.Sequence}"));
                        break;
                    }

                    case TxDelayMinAction.ProbesSent:
                    {
                        if (counter is null || pendingStep is not { } step)
                        {
                            Log?.Invoke("txdmeter: 'sent' with no step open — ignoring");
                            break;
                        }
                        await Task.Delay(options.ArrivalGrace, cancellationToken).ConfigureAwait(false);
                        int decoded = counter.Count;
                        double? medianPre = counter.MedianPreDataCarrierMs;
                        counter.Dispose();
                        counter = null;
                        pendingStep = null;
                        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                            $"txdmeter: {decoded}/{step.Count} decoded at {step.TxDelayMs} ms" +
                            $"{(medianPre is { } p ? $", heard pre-data ~{p:0} ms" : string.Empty)} — reporting"));
                        await TrySendAsync(
                            new TxDelayMinMessage
                            {
                                Action = TxDelayMinAction.StepReport,
                                TxDelayMs = step.TxDelayMs,
                                Decoded = decoded,
                                Count = step.Count,
                                MedianPreDataMs = medianPre is { } pre ? (int)Math.Round(pre) : null,
                            },
                            cancellationToken).ConfigureAwait(false);
                        StepReported?.Invoke(new TxDelaySweepStep(
                            step.TxDelayMs, decoded, step.Count, null, medianPre));
                        break;
                    }

                    case TxDelayMinAction.Abort:
                    {
                        Log?.Invoke($"txdmeter: coordinator aborted ({message.Reason ?? "no reason"}) — " +
                                    "staying ready for a retry");
                        counter?.Dispose();
                        counter = null;
                        pendingStep = null;
                        break;
                    }

                    case TxDelayMinAction.Done:
                    {
                        Log?.Invoke(message.RecommendedTxDelayMs is { } rec
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"txdmeter: done — coordinator's recommendation {rec} ms")
                            : "txdmeter: done");
                        return 0;
                    }

                    case TxDelayMinAction.Confirm:
                    case TxDelayMinAction.Reject:
                    case TxDelayMinAction.StepReport:
                    default:
                        break; // coordinator-bound messages echoed back — not ours
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            counter?.Dispose();
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task TrySendAsync(TxDelayMinMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await link.SendAsync(message.ToTelegram(NextSequence()), cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"txdmeter: send unconfirmed ({ex.Message}) — the coordinator's timeouts cover it");
        }
    }

    private int NextSequence() => Interlocked.Increment(ref sequence);
}
