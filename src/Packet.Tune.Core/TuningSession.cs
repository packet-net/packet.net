using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>Tuned-side stimulus: transmit an n-frame burst on the channel under tune.</summary>
public interface IBurstStimulus
{
    /// <summary>Transmit <paramref name="frames"/> burst frames; returns how
    /// many actually completed transmission.</summary>
    Task<int> FireBurstAsync(int frames, CancellationToken cancellationToken = default);
}

/// <summary>Meter-side measurement: watch the channel for one burst and report the signals.</summary>
public interface IBurstMeter
{
    /// <summary>Measure one burst of <paramref name="requestedFrames"/> frames
    /// (the call spans the whole measurement window).</summary>
    Task<MeterReport> MeasureBurstAsync(int requestedFrames, CancellationToken cancellationToken = default);
}

/// <summary>Tuned-side operator interaction between bursts.</summary>
public interface ITuningPrompt
{
    /// <summary>Ask the operator to adjust the pot and confirm. True =
    /// run another burst; false = finish the session.</summary>
    Task<bool> ContinueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Options for a <see cref="TuningSession"/>.</summary>
public sealed record TuningSessionOptions
{
    /// <summary>Frames per burst (kept short — every burst is airtime). Default 5.</summary>
    public int BurstFrames { get; init; } = 5;
}

/// <summary>
/// The deviation-tuning assistant loop, shared by every link flavour
/// (<see cref="SdmTuningLink"/> and <see cref="WebSocketTuningLink"/> alike).
/// </summary>
/// <remarks>
/// <para>Protocol choreography (five verbs, meter drives the measurements):</para>
/// <list type="number">
///   <item>Both ends send <c>HI|&lt;role&gt;</c>. The tuned end's <c>HI</c>
///     doubles as its "ready for a burst" signal — it re-sends it after the
///     operator confirms each pot adjustment.</item>
///   <item>On <c>HI|tuned</c> the meter sends <c>RQ|n</c> and opens its
///     measurement window; the tuned end fires an n-frame burst.</item>
///   <item>The meter sends <c>MS|&lt;report&gt;</c> then
///     <c>AD|UP/DN/OK</c>; the tuned end shows the trend table and prompts
///     the operator (adjust pot → Enter → next round, or finish).</item>
///   <item><c>BY</c> from either end closes the session.</item>
/// </list>
/// <para>The stimulus is the tuned end's own UI-frame bursts, which decode
/// at any deviation the meter can hear at all; the coordination channel is
/// the link, which does not depend on the pot under tune.</para>
/// </remarks>
public static class TuningSession
{
    /// <summary>Wire token for the tuned role.</summary>
    public const string TunedRole = "tuned";

    /// <summary>Wire token for the meter role.</summary>
    public const string MeterRole = "meter";

    /// <summary>
    /// Run the meter side: wait for the tuned end's ready beacons, request
    /// bursts, measure, and send measurement + advice. Returns 0 after a
    /// clean <c>BY</c>, 1 when the link died first.
    /// </summary>
    /// <param name="link">The coordination link.</param>
    /// <param name="meter">The burst-measurement implementation.</param>
    /// <param name="options">Session options; null = defaults.</param>
    /// <param name="output">Progress/status sink (console).</param>
    /// <param name="cancellationToken">Ends the session (a best-effort <c>BY</c> is sent).</param>
    public static async Task<int> RunMeterAsync(
        ITuningLink link,
        IBurstMeter meter,
        TuningSessionOptions? options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(output);
        var opts = options ?? new TuningSessionOptions();
        int seq = 0;
        int round = 0;
        MeterReport? previous = null;

        await link.SendAsync(new TuningTelegram(seq++, TuningVerb.Hello, MeterRole), cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync("meter: HI sent — waiting for the tuned end...").ConfigureAwait(false);

        try
        {
            await foreach (var telegram in link.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (telegram.Verb)
                {
                    case TuningVerb.Hello when telegram.Args == TunedRole:
                        round++;
                        await output.WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture,
                            $"meter: tuned end ready — requesting burst {round} ({opts.BurstFrames} frames)"))
                            .ConfigureAwait(false);
                        await link.SendAsync(
                            new TuningTelegram(seq++, TuningVerb.BurstRequest,
                                opts.BurstFrames.ToString(CultureInfo.InvariantCulture)),
                            cancellationToken).ConfigureAwait(false);

                        var report = await meter.MeasureBurstAsync(opts.BurstFrames, cancellationToken)
                            .ConfigureAwait(false);
                        var advice = DeviationAdvisor.Advise(report, previous);
                        previous = report;
                        await output.WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture,
                            $"meter: burst {round}: {report.ToArgs()} → {DeviationAdvisor.ToWire(advice)}"))
                            .ConfigureAwait(false);

                        await link.SendAsync(
                            new TuningTelegram(seq++, TuningVerb.Measurement, report.ToArgs()),
                            cancellationToken).ConfigureAwait(false);
                        await link.SendAsync(
                            new TuningTelegram(seq++, TuningVerb.Advice, DeviationAdvisor.ToWire(advice)),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case TuningVerb.Hello:
                        break; // our own role echoed back / unknown role — ignore

                    case TuningVerb.Bye:
                        await output.WriteLineAsync("meter: tuned end said BY — session over").ConfigureAwait(false);
                        return 0;

                    case TuningVerb.BurstRequest:
                    case TuningVerb.Measurement:
                    case TuningVerb.Advice:
                    default:
                        break; // not meter-bound verbs
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TrySendByeAsync(link, seq).ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync("meter: link closed without BY").ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Run the tuned side: announce readiness, fire bursts on request,
    /// display the meter's measurements + advice as a live trend table, and
    /// prompt the operator between bursts. Returns 0 after a clean finish,
    /// 1 when the link died first.
    /// </summary>
    /// <param name="link">The coordination link.</param>
    /// <param name="stimulus">The burst-transmit implementation.</param>
    /// <param name="prompt">Operator interaction between bursts.</param>
    /// <param name="options">Session options; null = defaults.</param>
    /// <param name="output">Progress/trend-table sink (console).</param>
    /// <param name="cancellationToken">Ends the session (a best-effort <c>BY</c> is sent).</param>
    public static async Task<int> RunTunedAsync(
        ITuningLink link,
        IBurstStimulus stimulus,
        ITuningPrompt prompt,
        TuningSessionOptions? options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(stimulus);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(output);
        _ = options; // burst size is the meter's choice (RQ argument)
        int seq = 0;
        var trend = new List<(MeterReport Report, TuningAdvice? Advice)>();

        await link.SendAsync(new TuningTelegram(seq++, TuningVerb.Hello, TunedRole), cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync("tuned: HI sent — waiting for the meter to request a burst...")
            .ConfigureAwait(false);

        try
        {
            await foreach (var telegram in link.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (telegram.Verb)
                {
                    case TuningVerb.Hello when telegram.Args == MeterRole:
                        await output.WriteLineAsync("tuned: meter connected").ConfigureAwait(false);
                        break;

                    case TuningVerb.BurstRequest:
                        int frames = int.TryParse(telegram.Args, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                            ? n
                            : 5;
                        await output.WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture, $"tuned: transmitting {frames}-frame burst..."))
                            .ConfigureAwait(false);
                        int sent = await stimulus.FireBurstAsync(frames, cancellationToken).ConfigureAwait(false);
                        await output.WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture, $"tuned: burst done ({sent}/{frames} frames keyed)"))
                            .ConfigureAwait(false);
                        break;

                    case TuningVerb.Measurement:
                        if (MeterReport.TryParse(telegram.Args, out var report) && report is not null)
                        {
                            trend.Add((report, null));
                        }
                        else
                        {
                            await output.WriteLineAsync($"tuned: unreadable MS args '{telegram.Args}'")
                                .ConfigureAwait(false);
                        }
                        break;

                    case TuningVerb.Advice:
                        var advice = DeviationAdvisor.FromWire(telegram.Args);
                        if (trend.Count > 0)
                        {
                            trend[^1] = (trend[^1].Report, advice);
                        }
                        WriteTrendTable(output, trend);

                        bool again = await prompt.ContinueAsync(cancellationToken).ConfigureAwait(false);
                        if (!again)
                        {
                            await link.SendAsync(new TuningTelegram(seq++, TuningVerb.Bye, string.Empty), cancellationToken)
                                .ConfigureAwait(false);
                            await output.WriteLineAsync("tuned: BY sent — session over").ConfigureAwait(false);
                            return 0;
                        }
                        await link.SendAsync(new TuningTelegram(seq++, TuningVerb.Hello, TunedRole), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case TuningVerb.Bye:
                        await output.WriteLineAsync("tuned: meter said BY — session over").ConfigureAwait(false);
                        return 0;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TrySendByeAsync(link, seq).ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync("tuned: link closed without BY").ConfigureAwait(false);
        return 1;
    }

    private static void WriteTrendTable(TextWriter output, List<(MeterReport Report, TuningAdvice? Advice)> trend)
    {
        output.WriteLine();
        output.WriteLine("  burst   decoded   fec Δ    clip Δ   rssi dBm   advice");
        for (int i = 0; i < trend.Count; i++)
        {
            var (r, advice) = trend[i];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {i + 1,5}   {r.DecodedFrames,3}/{r.RequestedFrames,-3}   {Fmt(r.FecCorrectedBytesDelta),6}   {Fmt(r.LostAdcSamplesDelta),6}   {FmtRssi(r.RssiDbm),8}   {(advice is { } a ? DeviationAdvisor.ToWire(a) : "—")}"));
        }
        output.WriteLine();
    }

    private static string Fmt(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static string FmtRssi(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";

    private static async Task TrySendByeAsync(ITuningLink link, int seq)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await link.SendAsync(new TuningTelegram(seq, TuningVerb.Bye, string.Empty), cts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
