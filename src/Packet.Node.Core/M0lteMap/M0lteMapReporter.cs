using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.M0lteMap;

/// <summary>
/// Puts the node on the UK packet map (#453): the pdn form of LinBPQ's <c>EnableM0LTEMap=1</c>.
/// While <see cref="M0lteMapConfig.Enabled"/> is on it POSTs a fresh report two minutes after
/// reporting starts and every fifteen minutes after that, LinBPQ's own cadence. Registered
/// unconditionally and self-gating on the live config, so turning it on or off needs no restart.
/// See <c>docs/m0lte-map-reporting.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never load-bearing.</b> Each report is a whole snapshot, so a lost one is simply replaced by
/// the next. A report that could not be delivered (no route, timeout, 429, 5xx) is retried after
/// 1, 2, 4 and 8 minutes and then at the normal interval; one the collector refused (another 4xx)
/// waits for the next scheduled report. Each kind of failure is one warning per outage, the rest
/// are debug lines, and the recovery is one information line, so a collector outage cannot flood
/// the journal. A report with no port up is skipped rather than sent empty.
/// </para>
/// <para>
/// The loop is one <see cref="TickAsync"/> per wake-up, driven by <see cref="TimeProvider"/>; tests
/// call it directly against a fake clock rather than racing a background loop.
/// </para>
/// </remarks>
public sealed partial class M0lteMapReporter : BackgroundService
{
    /// <summary>The wait from reporting starting (node start or the switch turned on) to the first
    /// report: LinBPQ waits two minutes so its ports and rig control are up first.</summary>
    public static readonly TimeSpan FirstReportDelay = TimeSpan.FromMinutes(2);

    /// <summary>The report interval (LinBPQ's <c>REPORTINTERVAL</c>, 15 minutes).</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);

    /// <summary>The longest the reporter sleeps between config checks, so turning reporting off,
    /// or setting the grid it was waiting for, is noticed within this.</summary>
    public static readonly TimeSpan ConfigCheckInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8),
    ];

    private readonly IConfigProvider config;
    private readonly IM0lteMapStateSource state;
    private readonly IM0lteMapClient client;
    private readonly TimeProvider clock;
    private readonly string version;
    private readonly ILogger<M0lteMapReporter> logger;

    // Touched only by the single loop (or a test driving TickAsync), so no locking.
    private DateTimeOffset? nextDue;
    private int consecutiveFailures;
    // The kind of failure the current outage is in, or null when the last report got through. A
    // warning is logged when an outage starts and again only if its kind changes (unreachable to
    // refused, or back), so a new kind of problem always reaches the journal once.
    private M0lteMapPostOutcome? failing;
    private bool everAccepted;
    private bool warnedNoLocator;

    public M0lteMapReporter(
        IConfigProvider config,
        IM0lteMapStateSource state,
        IM0lteMapClient client,
        TimeProvider clock,
        string version,
        ILogger<M0lteMapReporter>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clock = clock ?? TimeProvider.System;
        this.version = string.IsNullOrWhiteSpace(version) ? "dev" : version;
        this.logger = logger ?? NullLogger<M0lteMapReporter>.Instance;
    }

    /// <summary>When the next report is due, or null while reporting is off. For tests.</summary>
    internal DateTimeOffset? NextDue => nextDue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogTickFault(ex.Message);
                wait = ConfigCheckInterval;
            }

            try
            {
                await Task.Delay(wait, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One wake-up: notice the switch, send a report if one is due, and return how long to sleep
    /// before the next wake-up (never longer than <see cref="ConfigCheckInterval"/>).
    /// </summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken cancellationToken)
    {
        var current = config.Current;
        var now = clock.GetUtcNow();

        if (!current.M0lteMap.Enabled)
        {
            if (nextDue is not null)
            {
                LogDisabled();
            }
            nextDue = null;
            consecutiveFailures = 0;
            failing = null;
            return ConfigCheckInterval;
        }

        if (nextDue is null)
        {
            nextDue = now + FirstReportDelay;
            LogEnabled((int)FirstReportDelay.TotalMinutes, (int)ReportInterval.TotalMinutes);
        }

        if (now >= nextDue)
        {
            nextDue = now + await ReportOnceAsync(current, cancellationToken).ConfigureAwait(false);
        }

        var remaining = nextDue.Value - now;
        return remaining < ConfigCheckInterval ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : ConfigCheckInterval;
    }

    // Build and send one report; return how long until the next attempt.
    private async Task<TimeSpan> ReportOnceAsync(NodeConfig current, CancellationToken ct)
    {
        M0lteMapSnapshot snapshot;
        try
        {
            snapshot = state.Capture();
        }
        catch (Exception ex)
        {
            // A report without the ports would tell the map the node has none; skip this one.
            LogCaptureFault(ex.Message);
            return RetryDelays[0];
        }

        var report = M0lteMapReportBuilder.Build(current, snapshot, version);
        if (report is null)
        {
            if (!warnedNoLocator)
            {
                LogNoLocator();
                warnedNoLocator = true;
            }
            // Nothing was sent. Look again at the next config check, so a grid set now is
            // reported within half a minute rather than a quarter of an hour.
            return ConfigCheckInterval;
        }
        warnedNoLocator = false;

        if (report.Ports.Count == 0)
        {
            // Every port is down (or the node is still starting). A report now would tell the map
            // the node has no ports for the next quarter of an hour; skip it and look again soon.
            LogNoPortsUp();
            return RetryDelays[0];
        }

        var call = current.Identity.Callsign;
        var result = await client.PostAsync(call, report, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case M0lteMapPostOutcome.Accepted:
                var neighbours = report.Neighbours?.Count ?? 0;
                if (failing is not null || !everAccepted)
                {
                    LogReported(call, report.Ports.Count, neighbours);
                }
                else
                {
                    LogReportedQuietly(call, report.Ports.Count, neighbours);
                }
                everAccepted = true;
                failing = null;
                consecutiveFailures = 0;
                return ReportInterval;

            case M0lteMapPostOutcome.Rejected:
                if (failing != M0lteMapPostOutcome.Rejected)
                {
                    LogRejected(result.StatusCode ?? 0, result.Detail ?? "no reason given", (int)ReportInterval.TotalMinutes);
                }
                else
                {
                    LogRejectedAgain(result.StatusCode ?? 0, result.Detail ?? "no reason given");
                }
                failing = M0lteMapPostOutcome.Rejected;
                consecutiveFailures = 0;
                return ReportInterval;

            default:
                var delay = RetryDelays[Math.Min(consecutiveFailures, RetryDelays.Length - 1)];
                if (consecutiveFailures >= RetryDelays.Length)
                {
                    delay = ReportInterval;
                }
                var reason = result.StatusCode is { } status ? $"HTTP {status}" : result.Detail ?? "unreachable";
                if (failing != M0lteMapPostOutcome.TransportError)
                {
                    LogFailed(reason, (int)delay.TotalMinutes);
                }
                else
                {
                    LogFailedAgain(reason, (int)delay.TotalMinutes);
                }
                failing = M0lteMapPostOutcome.TransportError;
                consecutiveFailures++;
                return delay;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "UK packet map reporting on: first report in {FirstMinutes} minutes, then every {IntervalMinutes} minutes.")]
    private partial void LogEnabled(int firstMinutes, int intervalMinutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "UK packet map reporting off.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "UK packet map: reported {Call} ({Ports} ports, {Neighbours} neighbours).")]
    private partial void LogReported(string call, int ports, int neighbours);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "UK packet map: reported {Call} ({Ports} ports, {Neighbours} neighbours).")]
    private partial void LogReportedQuietly(string call, int ports, int neighbours);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "UK packet map: the report did not get through ({Reason}); trying again in {Minutes} minutes. Further failures are logged at debug until one succeeds.")]
    private partial void LogFailed(string reason, int minutes);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "UK packet map: the report did not get through ({Reason}); trying again in {Minutes} minutes.")]
    private partial void LogFailedAgain(string reason, int minutes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "UK packet map: the collector refused the report (HTTP {Status}: {Detail}); next report in {Minutes} minutes.")]
    private partial void LogRejected(int status, string detail, int minutes);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "UK packet map: the collector refused the report again (HTTP {Status}: {Detail}).")]
    private partial void LogRejectedAgain(int status, string detail);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "UK packet map reporting is on but identity.grid is not a 6-character Maidenhead locator, so the node is not being reported. Set the grid to fix it.")]
    private partial void LogNoLocator();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "UK packet map: could not read the node's ports and neighbours ({Reason}); skipped this report, trying again in a minute.")]
    private partial void LogCaptureFault(string reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "UK packet map: no port is up, so this report was skipped; looking again in a minute.")]
    private partial void LogNoPortsUp();

    [LoggerMessage(Level = LogLevel.Debug, Message = "UK packet map: a reporting pass faulted ({Reason}); trying again shortly.")]
    private partial void LogTickFault(string reason);
}
