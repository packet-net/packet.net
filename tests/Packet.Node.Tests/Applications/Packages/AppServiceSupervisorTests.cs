using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The <see cref="AppServiceSupervisor"/> against real <c>/bin/sh</c> children (Linux-only,
/// like the rest of the app platform suite): the reconcile lifecycle (start missing / stop
/// surplus / fingerprint-change restart / leave matching alone), the spawn contract
/// (PDN_* environment, state-dir working directory, package-dir path resolution), the restart
/// policies with backoff, the crash-loop breaker and its two deliberate exits
/// (<see cref="AppServiceSupervisor.RestartAsync"/> / a changed fingerprint), graceful SIGTERM
/// teardown, and clean shutdown via <see cref="IAsyncDisposable"/>. Timings ride a short
/// injected backoff base; every wait is a polled deadline (<see cref="Wait"/>), never a bare
/// sleep-and-hope.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppServiceSupervisorTests
{
    private static AppServiceSupervisor NewSupervisor(
        FakeAppPackageCatalog catalog,
        NodeConfig? config = null,
        TimeSpan? backoffBase = null,
        TimeSpan? stopGrace = null) => new(
            new TestConfigProvider(config ?? PackageTestSupport.Node()),
            catalog,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            backoffBase ?? TimeSpan.FromMilliseconds(25),
            stopGrace ?? TimeSpan.FromSeconds(2));

    private static AppServiceStatus? StatusOf(AppServiceSupervisor supervisor, string id) =>
        supervisor.Statuses.FirstOrDefault(s => s.Id == id);

    private static int CountRuns(string runsFile) =>
        File.Exists(runsFile) ? File.ReadAllLines(runsFile).Length : 0;

    // ---- the spawn contract ---------------------------------------------------------------

    [Fact]
    public async Task Enable_starts_the_service_running_with_pdn_environment_and_state_dir_cwd()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("envy");
        pkg.WriteScript("run.sh", """
            env > "$PDN_APP_STATE/env.txt"
            pwd > "$PDN_APP_STATE/cwd.txt"
            while :; do sleep 0.2; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service(
            "run.sh",
            environment: new Dictionary<string, string> { ["FROM_MANIFEST"] = "m", ["WINNER"] = "manifest" },
            @override: new AppOverrideConfig
            {
                Id = "envy",
                Enabled = true,
                Environment = new Dictionary<string, string> { ["WINNER"] = "override", ["FROM_OWNER"] = "o" },
            }));
        await using var supervisor = NewSupervisor(catalog, PackageTestSupport.Node(rhpEnabled: true));

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "envy")?.State == AppServiceState.Running, "service running");
        Assert.NotNull(StatusOf(supervisor, "envy")!.Pid);

        // cwd.txt is written after env.txt, so once it exists env.txt is complete.
        await Wait.ForAsync(() => File.Exists(pkg.StatePath("cwd.txt")), "child wrote env + cwd");
        var env = File.ReadAllLines(pkg.StatePath("env.txt"));
        Assert.Contains("PDN_APP_ID=envy", env);
        Assert.Contains($"PDN_APP_DIR={pkg.PackageDir}", env);
        Assert.Contains($"PDN_APP_STATE={pkg.StateDir}", env);
        Assert.Contains("PDN_NODE_CALLSIGN=M0LTE-1", env); // the node's identity, for the SSID-of-the-node-callsign convention
        Assert.Contains("PDN_RHP_HOST=127.0.0.1", env);   // present: the config enables RHP
        Assert.Contains("PDN_RHP_PORT=9123", env);
        Assert.Contains("FROM_MANIFEST=m", env);
        Assert.Contains("FROM_OWNER=o", env);
        Assert.Contains("WINNER=override", env);          // owner's override wins over the manifest

        // Working dir defaults to the state dir (which the supervisor created).
        Assert.Equal(pkg.StateDir, File.ReadAllLines(pkg.StatePath("cwd.txt"))[0]);
    }

    [Fact]
    public async Task Rhp_disabled_means_no_rhp_environment()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("norhp");
        pkg.WriteScript("run.sh", """
            env > "$PDN_APP_STATE/env.txt"
            touch "$PDN_APP_STATE/done"
            while :; do sleep 0.2; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        await using var supervisor = NewSupervisor(catalog, PackageTestSupport.Node(rhpEnabled: false));

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => File.Exists(pkg.StatePath("done")), "child wrote env");
        var env = File.ReadAllLines(pkg.StatePath("env.txt"));
        Assert.DoesNotContain(env, line => line.StartsWith("PDN_RHP_", StringComparison.Ordinal));
    }

    // ---- reconcile: desired vs running ----------------------------------------------------

    [Fact]
    public async Task Disable_stops_the_running_service_on_reconcile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("toggly");
        pkg.WriteScript("run.sh", "while :; do sleep 0.2; done\n");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "toggly")?.State == AppServiceState.Running, "running");
        var pid = StatusOf(supervisor, "toggly")!.Pid!.Value;

        catalog.Set(pkg.Service("run.sh", enabled: false));
        await supervisor.ReconcileAsync();

        Assert.Equal(AppServiceState.Stopped, StatusOf(supervisor, "toggly")!.State);
        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(pid), "process gone after disable");
    }

    [Fact]
    public async Task Package_removed_from_discovery_is_surplus_stopped_on_reconcile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("vanish");
        pkg.WriteScript("run.sh", "while :; do sleep 0.2; done\n");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "vanish")?.State == AppServiceState.Running, "running");
        var pid = StatusOf(supervisor, "vanish")!.Pid!.Value;

        catalog.Set();   // the package dir is gone on the next scan
        await supervisor.ReconcileAsync();

        Assert.Null(StatusOf(supervisor, "vanish"));
        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(pid), "process gone after removal");
    }

    [Fact]
    public async Task Fingerprint_change_restarts_a_running_service()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("shifty");
        pkg.WriteScript("run.sh", """
            echo "$1" >> "$PDN_APP_STATE/runs.txt"
            while :; do sleep 0.2; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", extraArgs: ["A"]));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "shifty")?.State == AppServiceState.Running, "running with A");
        var firstPid = StatusOf(supervisor, "shifty")!.Pid!.Value;

        catalog.Set(pkg.Service("run.sh", extraArgs: ["B"]));   // args differ → new spawn fingerprint
        await supervisor.ReconcileAsync();

        await Wait.ForAsync(() => StatusOf(supervisor, "shifty")?.State == AppServiceState.Running, "running with B");
        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(firstPid), "old process gone");
        Assert.NotEqual(firstPid, StatusOf(supervisor, "shifty")!.Pid!.Value);
        var runs = File.ReadAllLines(pkg.StatePath("runs.txt"));
        Assert.Equal(["A", "B"], runs);
    }

    [Fact]
    public async Task Concurrent_reconciles_are_serialized_and_start_the_service_once()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("once");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            while :; do sleep 0.2; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        await using var supervisor = NewSupervisor(catalog);

        await Task.WhenAll(
            supervisor.ReconcileAsync(), supervisor.ReconcileAsync(), supervisor.ReconcileAsync(),
            supervisor.ReconcileAsync(), supervisor.ReconcileAsync());

        await Wait.ForAsync(() => StatusOf(supervisor, "once")?.State == AppServiceState.Running, "running");
        await Task.Delay(150);   // bounded observation window for a (forbidden) second spawn
        Assert.Equal(1, CountRuns(pkg.StatePath("runs.txt")));
        Assert.Equal(AppServiceState.Running, StatusOf(supervisor, "once")!.State);
    }

    // ---- restart policies -------------------------------------------------------------------

    [Fact]
    public async Task Crash_goes_through_backoff_then_running_again_under_on_failure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("phoenix");
        pkg.WriteScript("run.sh", """
            if [ ! -f "$PDN_APP_STATE/crashed-once" ]; then
              touch "$PDN_APP_STATE/crashed-once"
              exit 3
            fi
            while :; do sleep 0.2; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.OnFailure));
        // A backoff long enough for the poll to observe the Backoff state, short enough to stay fast.
        await using var supervisor = NewSupervisor(catalog, backoffBase: TimeSpan.FromMilliseconds(700));

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "phoenix")?.State == AppServiceState.Backoff, "backoff after the crash");
        Assert.Equal("exited 3", StatusOf(supervisor, "phoenix")!.Detail);
        await Wait.ForAsync(() => StatusOf(supervisor, "phoenix")?.State == AppServiceState.Running, "running after backoff");
    }

    [Fact]
    public async Task Clean_exit_under_on_failure_stays_stopped_and_a_plain_reconcile_does_not_respawn()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("oneshot");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            exit 0
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.OnFailure));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "oneshot")?.State == AppServiceState.Stopped, "stopped after clean exit");
        Assert.Equal("exited 0", StatusOf(supervisor, "oneshot")!.Detail);
        Assert.Equal(1, CountRuns(pkg.StatePath("runs.txt")));

        // Same desired fingerprint → the reconcile leaves the cleanly-exited service alone.
        await supervisor.ReconcileAsync();
        await Task.Delay(200);   // bounded observation window for a (forbidden) respawn
        Assert.Equal(1, CountRuns(pkg.StatePath("runs.txt")));
        Assert.Equal(AppServiceState.Stopped, StatusOf(supervisor, "oneshot")!.State);
    }

    [Fact]
    public async Task Clean_exit_under_always_restarts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("again");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            exit 0
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.Always));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => CountRuns(pkg.StatePath("runs.txt")) >= 2, "restarted after a clean exit");
    }

    [Fact]
    public async Task Never_policy_never_restarts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("fatal");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            exit 5
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.Never));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "fatal")?.State == AppServiceState.Stopped, "stopped after the failure");
        Assert.Equal("exited 5", StatusOf(supervisor, "fatal")!.Detail);
        await Task.Delay(250);   // bounded observation window for a (forbidden) restart
        Assert.Equal(1, CountRuns(pkg.StatePath("runs.txt")));
        Assert.Equal(AppServiceState.Stopped, StatusOf(supervisor, "fatal")!.State);
    }

    // ---- the crash-loop breaker --------------------------------------------------------------

    [Fact]
    public async Task Crash_loop_faults_plain_reconcile_does_not_resurrect_restart_does()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("loopy");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            exit 1
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.OnFailure));
        await using var supervisor = NewSupervisor(catalog, backoffBase: TimeSpan.FromMilliseconds(10));

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "loopy")?.State == AppServiceState.Faulted, "breaker tripped");
        Assert.Equal(5, CountRuns(pkg.StatePath("runs.txt")));   // exactly the 5 windowed starts
        Assert.Contains("crash loop", StatusOf(supervisor, "loopy")!.Detail, StringComparison.Ordinal);

        // A plain re-reconcile with the same fingerprint must NOT resurrect a faulted service.
        await supervisor.ReconcileAsync();
        await Task.Delay(200);   // bounded observation window for a (forbidden) respawn
        Assert.Equal(AppServiceState.Faulted, StatusOf(supervisor, "loopy")!.State);
        Assert.Equal(5, CountRuns(pkg.StatePath("runs.txt")));

        // RestartAsync is the owner's way out: it clears the breaker and spawns again.
        await supervisor.RestartAsync("loopy");
        await Wait.ForAsync(() => CountRuns(pkg.StatePath("runs.txt")) >= 6, "spawned again after RestartAsync");
        await Wait.ForAsync(() => StatusOf(supervisor, "loopy")?.State == AppServiceState.Faulted, "faults again (still crashing)");
    }

    [Fact]
    public async Task Changed_fingerprint_resurrects_a_faulted_service_on_reconcile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("healed");
        pkg.WriteScript("run.sh", """
            if [ "$1" = "good" ]; then
              while :; do sleep 0.2; done
            fi
            exit 1
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", extraArgs: ["bad"]));
        await using var supervisor = NewSupervisor(catalog, backoffBase: TimeSpan.FromMilliseconds(10));

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "healed")?.State == AppServiceState.Faulted, "breaker tripped");

        catalog.Set(pkg.Service("run.sh", extraArgs: ["good"]));   // the owner fixed the spawn
        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "healed")?.State == AppServiceState.Running, "running after the fix");
    }

    // ---- external + statuses ------------------------------------------------------------------

    [Fact]
    public async Task External_services_are_never_spawned_report_external_and_reject_restart()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("ext");
        pkg.WriteScript("run.sh", """
            mkdir -p "$PDN_APP_STATE"
            touch "$PDN_APP_STATE/started"
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", managed: AppServiceManaged.External));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        Assert.Equal(AppServiceState.External, StatusOf(supervisor, "ext")!.State);
        await Task.Delay(150);   // bounded observation window for a (forbidden) spawn
        Assert.False(File.Exists(pkg.StatePath("started")), "pdn must never start an external service");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartAsync("ext"));
        Assert.Contains("external", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restart_of_an_unknown_id_throws()
    {
        var catalog = new FakeAppPackageCatalog();
        await using var supervisor = NewSupervisor(catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.RestartAsync("nope"));
    }

    [Fact]
    public async Task Statuses_cover_running_external_and_disabled_services()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var running = new TempAppPackage("alive");
        running.WriteScript("run.sh", "while :; do sleep 0.2; done\n");
        using var external = new TempAppPackage("theirs");
        external.WriteScript("run.sh", "exit 0\n");
        using var disabled = new TempAppPackage("dormant");
        disabled.WriteScript("run.sh", "exit 0\n");

        var catalog = new FakeAppPackageCatalog();
        catalog.Set(
            running.Service("run.sh"),
            external.Service("run.sh", managed: AppServiceManaged.External),
            disabled.Service("run.sh", enabled: false));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => StatusOf(supervisor, "alive")?.State == AppServiceState.Running, "running");

        var statuses = supervisor.Statuses;
        Assert.Equal(3, statuses.Count);
        var alive = statuses.Single(s => s.Id == "alive");
        Assert.Equal(AppServiceState.Running, alive.State);
        Assert.NotNull(alive.Pid);
        Assert.Equal(AppServiceState.External, statuses.Single(s => s.Id == "theirs").State);
        var dormant = statuses.Single(s => s.Id == "dormant");
        Assert.Equal(AppServiceState.Stopped, dormant.State);
        Assert.Null(dormant.Pid);
    }

    // ---- teardown ------------------------------------------------------------------------------

    [Fact]
    public async Task Graceful_stop_delivers_sigterm_the_child_can_act_on()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("polite");
        pkg.WriteScript("run.sh", """
            trap 'echo bye > "$PDN_APP_STATE/term.marker"; exit 0' TERM
            touch "$PDN_APP_STATE/ready"
            while :; do sleep 0.1; done
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(() => File.Exists(pkg.StatePath("ready")), "trap installed");

        catalog.Set(pkg.Service("run.sh", enabled: false));
        await supervisor.ReconcileAsync();   // the stop completes inside the reconcile

        Assert.True(File.Exists(pkg.StatePath("term.marker")), "the child must see SIGTERM before any kill");
        Assert.Equal(AppServiceState.Stopped, StatusOf(supervisor, "polite")!.State);
    }

    [Fact]
    public async Task Dispose_stops_the_whole_process_tree_leaving_no_orphans()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pkg = new TempAppPackage("brood");
        pkg.WriteScript("run.sh", """
            echo $$ > "$PDN_APP_STATE/sh.pid"
            sleep 300 &
            echo $! > "$PDN_APP_STATE/grandchild.pid"
            wait
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));
        var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        await Wait.ForAsync(
            () => File.Exists(pkg.StatePath("sh.pid")) && File.Exists(pkg.StatePath("grandchild.pid")),
            "children up");
        var shPid = int.Parse(File.ReadAllText(pkg.StatePath("sh.pid")).Trim(), CultureInfo.InvariantCulture);
        var grandchildPid = int.Parse(File.ReadAllText(pkg.StatePath("grandchild.pid")).Trim(), CultureInfo.InvariantCulture);

        await supervisor.DisposeAsync();

        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(shPid), "direct child gone");
        if (PackageTestSupport.SetsidAvailable)
        {
            // Group-leader spawn: the SIGTERM reached the whole group, grandchild included.
            await Wait.ForAsync(() => PackageTestSupport.ProcessGone(grandchildPid), "grandchild gone (no orphan)");
        }
    }
}
