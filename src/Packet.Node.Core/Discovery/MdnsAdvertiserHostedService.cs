using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Discovery;

/// <summary>
/// Advertises the node on the LAN over mDNS / DNS-SD (<c>_pdn._tcp</c>) so the pdn mobile
/// app can discover it (<see cref="MdnsConfig"/>). <b>Infra, not an app.</b>
///
/// <para>
/// Registration is delegated to the system mDNS daemon by supervising an
/// <c>avahi-publish</c> child - the conflict-free path on the Linux hosts the node deb
/// targets (Avahi owns port 5353; we register through it rather than run a second
/// responder). <c>avahi-publish -s</c> emits only the PTR/SRV/TXT for our service; the SRV
/// target is the daemon's own <c>&lt;host&gt;.local</c>, whose A/AAAA record avahi-daemon
/// publishes independently - so a reachable host name depends on avahi-daemon running, not
/// on this publish. The supervision mirrors
/// <see cref="Tailscale.TailscaleSidecarHostedService"/>: event-driven off
/// <see cref="IConfigProvider.OnChange"/>, the child is (re)spawned when the desired advert
/// (by value <see cref="Signature"/>) changes, and a failed/exited child is retried with
/// capped backoff - all on the injected <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// The surface is <b>total</b>: a missing <c>avahi-publish</c> (no Avahi, or a non-Linux
/// dev box), a daemon-down error, or any defect logs and stays dormant - it never throws
/// out of the run loop or affects the node. Discovery is a convenience; manual add-by-address
/// in the app always works.
/// </para>
/// </summary>
public sealed partial class MdnsAdvertiserHostedService : BackgroundService
{
    /// <summary>Env var overriding the <c>avahi-publish</c> binary (tests point this at a fake).</summary>
    public const string PublishBinaryEnvVar = "PDN_AVAHI_PUBLISH";
    private const string DefaultPublishBinary = "avahi-publish";

    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(60);

    /// <summary>A child that survived this long counts as a clean run - the next exit
    /// starts a fresh backoff streak rather than inheriting an old one.</summary>
    private static readonly TimeSpan StableRun = TimeSpan.FromSeconds(30);

    private readonly IConfigProvider config;
    private readonly TimeProvider clock;
    private readonly string? version;
    private readonly string binary;
    private readonly ILogger<MdnsAdvertiserHostedService> logger;

    // Edge-trigger for config changes: OnChange completes the captured task and swaps a fresh one.
    private volatile TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MdnsAdvertiserHostedService(
        IConfigProvider config,
        TimeProvider clock,
        string? version,
        ILoggerFactory? loggerFactory = null)
    {
        this.config = config;
        this.clock = clock;
        this.version = version;
        binary = Environment.GetEnvironmentVariable(PublishBinaryEnvVar) is { Length: > 0 } o
            ? o
            : DefaultPublishBinary;
        logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<MdnsAdvertiserHostedService>();
    }

    private void OnConfigChanged()
    {
        Interlocked.Exchange(ref changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
    }

    /// <summary>A value key for an advert plan - so the reconcile compares by content
    /// (instance + port + TXT), not by record reference (the TXT list is a fresh instance
    /// each pass, so reference equality would restart the child on every config change).</summary>
    internal static string Signature(AdvertPlan p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.Instance}\n{p.Port}\n{string.Join('\n', p.Txt)}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var sub = config.OnChange(_ => OnConfigChanged());

        string? runningSig = null;
        Process? child = null;
        DateTimeOffset spawnedAt = default;
        DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
        int failures = 0;
        bool enoentLogged = false; // "avahi-publish not found" is logged once per dormant streak

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Capture the change signal BEFORE reading config, so a change landing during this
                // iteration's reconcile completes THIS captured task and wakes the wait (no lost wakeup).
                var changeTask = changed.Task;

                var now = clock.GetUtcNow();
                var plan = MdnsAdvert.Plan(config.Current, version, out var skip);
                var sig = plan is null ? null : Signature(plan);

                if (sig != runningSig)
                {
                    // Desired advert changed (incl. enable/disable) → restart cleanly, attempt now.
                    // This intentionally takes precedence over the unexpected-exit branch and resets
                    // the crash-backoff streak: a new desired advert is a fresh start.
                    StopChild(ref child);
                    runningSig = sig;
                    failures = 0;
                    enoentLogged = false;
                    nextAttempt = now;
                    if (plan is null)
                    {
                        LogDormant(skip);
                    }
                }
                else if (plan is not null && child is { HasExited: true })
                {
                    // Same advert, child exited unexpectedly → schedule a backoff retry. A long
                    // stable run resets the streak so one late exit doesn't inherit old backoff.
                    var ranFor = now - spawnedAt;
                    StopChild(ref child);
                    failures = ranFor >= StableRun ? 1 : failures + 1;
                    nextAttempt = now + Backoff(failures);
                }

                if (plan is not null && child is null && now >= nextAttempt)
                {
                    child = TrySpawn(plan, ref failures, ref enoentLogged);
                    if (child is not null)
                    {
                        spawnedAt = clock.GetUtcNow();
                        enoentLogged = false;
                    }
                    else
                    {
                        nextAttempt = clock.GetUtcNow() + Backoff(Math.Max(failures, 1));
                    }
                }

                // Wait for the next reconcile trigger: a config change, the child exiting, the
                // backoff window, or stop. A dormant (no plan) state waits only on a change / stop.
                var waits = new List<Task> { changeTask };
                if (child is not null)
                {
                    waits.Add(child.WaitForExitAsync(stoppingToken));
                }
                else if (plan is not null)
                {
                    var delay = nextAttempt - clock.GetUtcNow();
                    waits.Add(Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, clock, stoppingToken));
                }

                try
                {
                    await Task.WhenAny(waits).WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            LogRunLoopFailed(ex);
        }
        finally
        {
            StopChild(ref child);
        }
    }

    private Process? TrySpawn(AdvertPlan plan, ref int failures, ref bool enoentLogged)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in plan.ToAvahiArgs())
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            var p = Process.Start(psi);
            if (p is null)
            {
                failures++;
                LogSpawnNull(binary);
                return null;
            }
            DrainQuietly(p);
            LogAdvertising(plan.Instance, AdvertPlan.ServiceType, plan.Port);
            return p;
        }
        catch (Win32Exception)
        {
            // Binary not found (no Avahi / non-Linux). Log once, then keep retrying on the capped
            // backoff so it self-heals if avahi-utils is later installed (no config change needed).
            failures++;
            if (!enoentLogged)
            {
                LogBinaryMissing(binary);
                enoentLogged = true;
            }
            return null;
        }
        catch (Exception ex)
        {
            failures++;
            LogSpawnFailed(ex, failures);
            return null;
        }
    }

    private static void DrainQuietly(Process p)
    {
        // Consume the child's pipes so it never blocks on a full buffer; we don't surface them.
        p.OutputDataReceived += static (_, _) => { };
        p.ErrorDataReceived += static (_, _) => { };
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            /* already exited */
        }
    }

    private static void StopChild(ref Process? child)
    {
        if (child is null)
        {
            return;
        }

        var p = child;
        child = null;
        try
        {
            if (!p.HasExited)
            {
                p.Kill();
            }
        }
        catch
        {
            /* already gone */
        }
        finally
        {
            p.Dispose();
        }
    }

    private static TimeSpan Backoff(int failures)
    {
        var ticks = BackoffBase.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromTicks(Math.Min(ticks, BackoffCap.Ticks));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "mDNS advertise dormant: {Reason}.")]
    private partial void LogDormant(string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "mDNS advertising {Instance} {ServiceType} on port {Port}.")]
    private partial void LogAdvertising(string instance, string serviceType, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "mDNS advertise unavailable: '{Binary}' not found. Install avahi-utils to enable LAN discovery; the node is otherwise unaffected.")]
    private partial void LogBinaryMissing(string binary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS advertise: failed to start {Binary} (null process).")]
    private partial void LogSpawnNull(string binary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS advertise: spawn failed (attempt {Failures}).")]
    private partial void LogSpawnFailed(Exception ex, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "mDNS advertiser run loop failed.")]
    private partial void LogRunLoopFailed(Exception ex);
}
