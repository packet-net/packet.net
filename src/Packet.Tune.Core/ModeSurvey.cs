using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>Per-cell verdict of a mode survey: did the mode carry traffic?</summary>
public enum ModeSurveyVerdict
{
    /// <summary>Nothing decoded — the mode does not work on this channel.</summary>
    Dead,

    /// <summary>Some frames decoded, some lost — the tuning assistant's natural home.</summary>
    Marginal,

    /// <summary>Every frame decoded.</summary>
    Solid,
}

/// <summary>
/// One (channel, mode, direction) cell of an IL2P+CRC mode survey: how many
/// short UI probe frames made it across the RF path, how fast, at what
/// receiver-side RSSI, and what the receiver TNC's IL2P decode counters did.
/// </summary>
public sealed record ModeSurveyCell
{
    /// <summary>The radios' programmed channel number (e.g. 0 = narrow, 1 = wide on the bench rig).</summary>
    public required int Channel { get; init; }

    /// <summary>NinoTNC mode number (DIP-equivalent, set via SETHW).</summary>
    public required byte Mode { get; init; }

    /// <summary>Human mode name from <see cref="NinoTncCatalog"/>.</summary>
    public required string ModeName { get; init; }

    /// <summary>Traffic direction label, e.g. <c>"A→B"</c>.</summary>
    public required string Direction { get; init; }

    /// <summary>Probe frames decoded at the receiver.</summary>
    public required int Successes { get; init; }

    /// <summary>Probe frames sent.</summary>
    public required int Attempts { get; init; }

    /// <summary>Mean send-to-decode latency over the successful probes
    /// (includes CSMA deferral, TXDELAY, airtime, decode and serial delivery).
    /// <c>null</c> when nothing got through.</summary>
    public double? MeanLatencyMs { get; init; }

    /// <summary>Tait CCDI RSSI at the receiver during the bursts
    /// (busy-gated median; see <see cref="ModeSurvey.PickRssi"/>).
    /// <c>null</c> when no samples were taken.</summary>
    public double? ReceiverRssiDbm { get; init; }

    /// <summary>Receiver GETALL delta of IL2P packets received+corrected
    /// (register 08 / labelled <c>IL2PRxPkts</c>). <c>null</c> when either
    /// snapshot was unavailable.</summary>
    public long? Il2pRxPacketsDelta { get; init; }

    /// <summary>Receiver GETALL delta of IL2P packets with uncorrectable
    /// errors (register 09 / labelled <c>IL2PRxUnCr</c>). <c>null</c> when
    /// either snapshot was unavailable.</summary>
    public long? Il2pRxUncorrectableDelta { get; init; }

    /// <summary>Dead / marginal / solid, from <see cref="Successes"/> vs <see cref="Attempts"/>.</summary>
    public ModeSurveyVerdict Verdict =>
        Successes <= 0 ? ModeSurveyVerdict.Dead
        : Successes < Attempts ? ModeSurveyVerdict.Marginal
        : ModeSurveyVerdict.Solid;
}

/// <summary>
/// The reusable pieces of the IL2P+CRC mode survey (`packet-tune
/// mode-survey`): mode selection from the catalog, probe/settle frame
/// factories, per-mode receive deadlines, busy-gated RSSI reduction, and the
/// Markdown / JSON renderings of the result table. The hardware orchestration
/// (channel switching, SETHW, sending) lives in the CLI command.
/// </summary>
public static class ModeSurvey
{
    /// <summary>
    /// The exact catalog-name fragment that selects the surveyed modes.
    /// Deliberately <c>"IL2P+CRC"</c> — plain <c>"IL2P"</c> (mode 13) and the
    /// legacy AX.25 modes are excluded.
    /// </summary>
    public const string Il2pCrcNameFragment = "IL2P+CRC";

    /// <summary>
    /// After the sender's ACKMODE TX-completion echo, how much longer the
    /// receiver is given to deliver the decoded frame (decode + serial trail;
    /// bench-measured 35–115 ms, with generous margin).
    /// </summary>
    public static readonly TimeSpan PostTxGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// All catalog modes whose name contains <see cref="Il2pCrcNameFragment"/>,
    /// ordered by mode number.
    /// </summary>
    public static IReadOnlyList<NinoTncMode> SelectIl2pCrcModes() =>
        NinoTncCatalog.ByMode.Values
            .Where(m => m.Name.Contains(Il2pCrcNameFragment, StringComparison.Ordinal))
            .OrderBy(m => m.Mode)
            .ToList();

    /// <summary>
    /// The overall wait for one probe round in <paramref name="mode"/>,
    /// measured from send initiation: base 4 s (CSMA + TXDELAY + queueing)
    /// plus three times the airtime of an IL2P-expanded probe frame
    /// (<paramref name="approxWireBytes"/> default 160 covers the ~60-byte
    /// AX.25 probe plus IL2P sync/parity overhead), capped at 20 s.
    /// </summary>
    public static TimeSpan ReceiveTimeout(NinoTncMode mode, int approxWireBytes = 160)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(approxWireBytes);
        double airtimeMs = mode.TransmissionMs(approxWireBytes);
        if (double.IsInfinity(airtimeMs))
        {
            return TimeSpan.FromSeconds(20);
        }
        var timeout = TimeSpan.FromMilliseconds(4000 + 3 * airtimeMs);
        return timeout > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : timeout;
    }

    /// <summary>
    /// The mode-change throwaway frame. The NinoTNC applies a changed KISS
    /// setting (including SETHW mode) from the SECOND frame after the command
    /// (bench-observed), so one of these is transmitted — and discarded — after
    /// every mode change before anything is measured. Its info text carries no
    /// <see cref="TuningBurst.Marker"/>, so it can never be counted as a probe.
    /// </summary>
    public static Ax25Frame BuildSettleFrame(Callsign source, byte mode) =>
        Ax25Frame.Ui(
            destination: new Callsign("SETTLE"),
            source: source,
            info: Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"PSETTLE mode {mode} throwaway ")
                    .PadRight(TuningBurst.InfoLength, '.')));

    /// <summary>
    /// Reduce busy-tagged RSSI polls to the burst's figure: the median of the
    /// samples taken while the receiver reported carrier (DCD), falling back
    /// to the strongest sample when no busy-tagged sample exists (no PROGRESS
    /// / DCD source — the maximum is the best carrier estimate on an
    /// otherwise idle channel). <c>null</c> for no samples.
    /// </summary>
    public static double? PickRssi(IReadOnlyCollection<(double Dbm, bool Busy)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return null;
        }
        var busy = samples.Where(s => s.Busy).Select(s => s.Dbm).ToList();
        if (busy.Count > 0)
        {
            return Median(busy);
        }
        return samples.Max(s => s.Dbm);
    }

    /// <summary>Mean of the successful-probe latencies; <c>null</c> when there were none.</summary>
    public static double? MeanLatencyMs(IReadOnlyCollection<double> latenciesMs)
    {
        ArgumentNullException.ThrowIfNull(latenciesMs);
        return latenciesMs.Count == 0 ? null : latenciesMs.Average();
    }

    /// <summary>Render the survey as a Markdown table (one row per cell, in input order).</summary>
    public static string RenderMarkdown(IReadOnlyList<ModeSurveyCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var sb = new StringBuilder();
        sb.AppendLine("| Ch | Mode | Name | Dir | Decoded | Mean latency | RSSI @ RX | IL2P rx Δ | IL2P uncorr Δ | Verdict |");
        sb.AppendLine("|---:|-----:|------|-----|--------:|-------------:|----------:|----------:|--------------:|---------|");
        foreach (var cell in cells)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {cell.Channel} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {cell.Mode} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {cell.ModeName} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {cell.Direction} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {cell.Successes}/{cell.Attempts} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatOrNa(cell.MeanLatencyMs, "0", " ms")} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatOrNa(cell.ReceiverRssiDbm, "0.0", " dBm")} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatOrNa(cell.Il2pRxPacketsDelta)} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatOrNa(cell.Il2pRxUncorrectableDelta)} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {DescribeVerdict(cell.Verdict)} |");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Render the survey as indented JSON (camelCase, enum verdicts as strings).</summary>
    public static string RenderJson(IReadOnlyList<ModeSurveyCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return JsonSerializer.Serialize(cells, JsonOptions);
    }

    /// <summary>Lower-case verdict label used in the Markdown rendering.</summary>
    public static string DescribeVerdict(ModeSurveyVerdict verdict) => verdict switch
    {
        ModeSurveyVerdict.Dead => "dead",
        ModeSurveyVerdict.Marginal => "MARGINAL",
        ModeSurveyVerdict.Solid => "solid",
        _ => verdict.ToString(),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string FormatOrNa(double? value, string format, string unit) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : "n/a";

    private static string FormatOrNa(long? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "n/a";

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
