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

    // ---- C076: a pipe-holding grandchild must not wedge the supervisor ---------------------

    /// <summary>
    /// The trap: the DIRECT child exits at once, but a grandchild it backgrounded inherited
    /// stdout/stderr and holds those pipes for five minutes. <c>WaitForExitAsync</c> returns
    /// immediately (manual reads, not BeginOutputReadLine), so an unbounded
    /// <c>await pumps</c> then parked the run loop waiting for an EOF that never comes -
    /// taking the restart policy, <c>ReconcileAsync</c> (which holds the gate) and
    /// <c>DisposeAsync</c> with it. The drain is now bounded and escalates to SIGKILL of the
    /// process group, so the exit is observed, the state leaves Running, and both reconcile
    /// and disposal complete promptly.
    /// </summary>
    [SkippableFact]
    public async Task A_backgrounded_grandchild_holding_the_pipes_does_not_wedge_the_supervisor()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

        using var pkg = new TempAppPackage("forker");
        pkg.WriteScript("run.sh", """
            sleep 300 &
            exit 0
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.Never));
        var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();

        // The exit is OBSERVED: the entry must not sit in Running with a dead pid while the
        // drain is in progress, and the restart policy must actually run.
        await Wait.ForAsync(
            () => StatusOf(supervisor, "forker")?.State == AppServiceState.Stopped,
            "the natural exit is observed and the policy (Never) settles the service");
        StatusOf(supervisor, "forker")!.Pid.Should().BeNull("a stopped service reports no pid");

        // The gate is free: a second reconcile completes instead of queuing behind the drain.
        await WithinAsync(supervisor.ReconcileAsync(), "reconcile must not queue behind the drain");

        // And so does teardown, which used to await the same never-completing pumps.
        await WithinAsync(supervisor.DisposeAsync().AsTask(), "disposal must not wait on a pipe nobody closes");
    }

    /// <summary>
    /// The other half of C076: with the drain unbounded the restart policy never ran at all,
    /// because the run loop was parked on the pipes before it ever got to the policy. The
    /// bounded drain escalates to SIGKILL of the process group - which is what actually
    /// releases a grandchild's grip on stdout/stderr - so an <c>always</c> service is
    /// respawned as it should be.
    /// </summary>
    [SkippableFact]
    public async Task A_pipe_holding_grandchild_does_not_stop_the_restart_policy_running()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

        using var pkg = new TempAppPackage("forker2");
        pkg.WriteScript("run.sh", """
            echo run >> "$PDN_APP_STATE/runs.txt"
            sleep 300 &
            exit 0
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", restart: AppServiceRestart.Always));
        var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();

        await Wait.ForAsync(
            () => CountRuns(pkg.StatePath("runs.txt")) >= 2,
            "a service whose grandchild holds the pipes open must still be respawned",
            TimeSpan.FromSeconds(30));

        await WithinAsync(supervisor.DisposeAsync().AsTask(), "disposal must stay bounded here too");
    }

    /// <summary>Fail loudly (rather than hanging the whole run) if an operation that must be
    /// bounded is not. The bound is generous next to the 300 s the old code would have waited.</summary>
    private static async Task WithinAsync(Task operation, string because)
    {
        var budget = TimeSpan.FromSeconds(30);
        var finished = await Task.WhenAny(operation, Task.Delay(budget));
        ReferenceEquals(finished, operation).Should().BeTrue($"{because} (waited {budget.TotalSeconds:0}s)");
        await operation;
    }

    // ---- the spawn contract ---------------------------------------------------------------

    [SkippableFact]
    public async Task Enable_starts_the_service_running_with_pdn_environment_and_state_dir_cwd()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        StatusOf(supervisor, "envy")!.Pid.Should().NotBeNull();

        // cwd.txt is written after env.txt, so once it exists env.txt is complete.
        await Wait.ForAsync(() => File.Exists(pkg.StatePath("cwd.txt")), "child wrote env + cwd");
        var env = File.ReadAllLines(pkg.StatePath("env.txt"));
        env.Should().Contain("PDN_APP_ID=envy");
        env.Should().Contain($"PDN_APP_DIR={pkg.PackageDir}");
        env.Should().Contain($"PDN_APP_STATE={pkg.StateDir}");
        env.Should().Contain("PDN_NODE_CALLSIGN=M0LTE-1"); // the node's identity, for the SSID-of-the-node-callsign convention
        env.Should().Contain("PDN_RHP_HOST=127.0.0.1");   // present: the config enables RHP
        env.Should().Contain("PDN_RHP_PORT=9123");
        env.Should().Contain("FROM_MANIFEST=m");
        env.Should().Contain("FROM_OWNER=o");
        env.Should().Contain("WINNER=override");          // owner's override wins over the manifest

        // Working dir defaults to the state dir (which the supervisor created).
        File.ReadAllLines(pkg.StatePath("cwd.txt"))[0].Should().Be(pkg.StateDir);
    }

    [SkippableFact]
    public async Task Rhp_disabled_means_no_rhp_environment()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        env.Should().NotContain(line => line.StartsWith("PDN_RHP_", StringComparison.Ordinal));
    }

    // ---- reconcile: desired vs running ----------------------------------------------------

    [SkippableFact]
    public async Task Disable_stops_the_running_service_on_reconcile()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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

        StatusOf(supervisor, "toggly")!.State.Should().Be(AppServiceState.Stopped);
        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(pid), "process gone after disable");
    }

    [SkippableFact]
    public async Task Package_removed_from_discovery_is_surplus_stopped_on_reconcile()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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

        StatusOf(supervisor, "vanish").Should().BeNull();
        await Wait.ForAsync(() => PackageTestSupport.ProcessGone(pid), "process gone after removal");
    }

    [SkippableFact]
    public async Task Fingerprint_change_restarts_a_running_service()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        StatusOf(supervisor, "shifty")!.Pid!.Value.Should().NotBe(firstPid);
        var runs = File.ReadAllLines(pkg.StatePath("runs.txt"));
        runs.Should().Equal("A", "B");
    }

    [SkippableFact]
    public async Task Concurrent_reconciles_are_serialized_and_start_the_service_once()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(1);
        StatusOf(supervisor, "once")!.State.Should().Be(AppServiceState.Running);
    }

    // ---- restart policies -------------------------------------------------------------------

    [SkippableFact]
    public async Task Crash_goes_through_backoff_then_running_again_under_on_failure()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        StatusOf(supervisor, "phoenix")!.Detail.Should().Be("exited 3");
        await Wait.ForAsync(() => StatusOf(supervisor, "phoenix")?.State == AppServiceState.Running, "running after backoff");
    }

    [SkippableFact]
    public async Task Clean_exit_under_on_failure_stays_stopped_and_a_plain_reconcile_does_not_respawn()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        StatusOf(supervisor, "oneshot")!.Detail.Should().Be("exited 0");
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(1);

        // Same desired fingerprint → the reconcile leaves the cleanly-exited service alone.
        await supervisor.ReconcileAsync();
        await Task.Delay(200);   // bounded observation window for a (forbidden) respawn
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(1);
        StatusOf(supervisor, "oneshot")!.State.Should().Be(AppServiceState.Stopped);
    }

    [SkippableFact]
    public async Task Clean_exit_under_always_restarts()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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

    [SkippableFact]
    public async Task Never_policy_never_restarts()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        StatusOf(supervisor, "fatal")!.Detail.Should().Be("exited 5");
        await Task.Delay(250);   // bounded observation window for a (forbidden) restart
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(1);
        StatusOf(supervisor, "fatal")!.State.Should().Be(AppServiceState.Stopped);
    }

    // ---- the crash-loop breaker --------------------------------------------------------------

    [SkippableFact]
    public async Task Crash_loop_faults_plain_reconcile_does_not_resurrect_restart_does()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(5);   // exactly the 5 windowed starts
        StatusOf(supervisor, "loopy")!.Detail.Should().Contain("crash loop");

        // A plain re-reconcile with the same fingerprint must NOT resurrect a faulted service.
        await supervisor.ReconcileAsync();
        await Task.Delay(200);   // bounded observation window for a (forbidden) respawn
        StatusOf(supervisor, "loopy")!.State.Should().Be(AppServiceState.Faulted);
        CountRuns(pkg.StatePath("runs.txt")).Should().Be(5);

        // RestartAsync is the owner's way out: it clears the breaker and spawns again.
        await supervisor.RestartAsync("loopy");
        await Wait.ForAsync(() => CountRuns(pkg.StatePath("runs.txt")) >= 6, "spawned again after RestartAsync");
        await Wait.ForAsync(() => StatusOf(supervisor, "loopy")?.State == AppServiceState.Faulted, "faults again (still crashing)");
    }

    [SkippableFact]
    public async Task Changed_fingerprint_resurrects_a_faulted_service_on_reconcile()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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

    [SkippableFact]
    public async Task External_services_are_never_spawned_report_external_and_reject_restart()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

        using var pkg = new TempAppPackage("ext");
        pkg.WriteScript("run.sh", """
            mkdir -p "$PDN_APP_STATE"
            touch "$PDN_APP_STATE/started"
            """);
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh", managed: AppServiceManaged.External));
        await using var supervisor = NewSupervisor(catalog);

        await supervisor.ReconcileAsync();
        StatusOf(supervisor, "ext")!.State.Should().Be(AppServiceState.External);
        await Task.Delay(150);   // bounded observation window for a (forbidden) spawn
        File.Exists(pkg.StatePath("started")).Should().BeFalse("pdn must never start an external service");

        var restartExternal = () => supervisor.RestartAsync("ext");
        var ex = (await restartExternal.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().ContainEquivalentOf("external");
    }

    [Fact]
    public async Task Restart_of_an_unknown_id_throws()
    {
        var catalog = new FakeAppPackageCatalog();
        await using var supervisor = NewSupervisor(catalog);
        var act = () => supervisor.RestartAsync("nope");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task Statuses_cover_running_external_and_disabled_services()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
        statuses.Count.Should().Be(3);
        var alive = statuses.Single(s => s.Id == "alive");
        alive.State.Should().Be(AppServiceState.Running);
        alive.Pid.Should().NotBeNull();
        statuses.Single(s => s.Id == "theirs").State.Should().Be(AppServiceState.External);
        var dormant = statuses.Single(s => s.Id == "dormant");
        dormant.State.Should().Be(AppServiceState.Stopped);
        dormant.Pid.Should().BeNull();
    }

    // ---- teardown ------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Graceful_stop_delivers_sigterm_the_child_can_act_on()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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

        File.Exists(pkg.StatePath("term.marker")).Should().BeTrue("the child must see SIGTERM before any kill");
        StatusOf(supervisor, "polite")!.State.Should().Be(AppServiceState.Stopped);
    }

    [SkippableFact]
    public async Task Dispose_stops_the_whole_process_tree_leaving_no_orphans()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake services are POSIX shell scripts");

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
