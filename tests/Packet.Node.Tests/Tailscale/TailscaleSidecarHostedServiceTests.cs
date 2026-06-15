using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Tailscale;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Tailscale;

/// <summary>
/// The <see cref="TailscaleSidecarHostedService"/> against a fake sidecar (a real <c>/bin/sh</c>
/// child, Linux-only like the rest of the process-spawning suite): the enable→start lifecycle
/// and JSON-status-line parsing into <see cref="ITailscaleStatus"/> (starting→running with the
/// FQDN; needs-login with the auth URL), restart-on-failure with backoff when the child exits,
/// reconcile on config change (disable stops it; a relevant-field change restarts it), and a
/// graceful SIGTERM teardown on stop. Status transitions are polled (<see cref="Wait"/>) because
/// the child + its stdout pump run on real background tasks against the system clock.
/// </summary>
[Trait("Category", "Node")]
public sealed class TailscaleSidecarHostedServiceTests : IDisposable
{
    private readonly string dir;

    public TailscaleSidecarHostedServiceTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-tsnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static NodeConfig Node(TailscaleConfig tailscale) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Tailscale = tailscale,
    };

    private static TailscaleConfig Enabled(string stateDir) => new()
    {
        Enabled = true,
        Hostname = "pdn",
        StateDir = stateDir,
        Target = "127.0.0.1:8080",
    };

    /// <summary>Write an executable fake sidecar that emits the given status lines, then either
    /// idles until SIGTERM (exit 0) or exits with the given code (to exercise restart-on-exit).
    /// When <paramref name="argsLog"/> is set it appends its argv there (one line) before idling,
    /// so a test can assert the flags it was launched with. When <paramref name="hupLog"/> is set
    /// it traps SIGHUP and appends a line there per signal (without exiting) — so a test can assert
    /// the live forwards-reload path SIGHUPs the same child instead of restarting it.</summary>
    private string WriteFakeSidecar(
        string name,
        IReadOnlyList<string> statusLines,
        int? exitCode = null,
        string? argsLog = null,
        string? hupLog = null)
    {
        var path = Path.Combine(dir, name);
        var lines = new List<string> { "#!/bin/sh" };
        if (argsLog is not null)
        {
            // Record the whole argv as one space-joined line, for the flag-contract assertion.
            lines.Add($"echo \"$@\" >> \"{argsLog}\"");
        }
        if (hupLog is not null)
        {
            // Trap SIGHUP: record the reload (proving the supervisor signalled, not restarted) and
            // keep running. The trap fires the handler when sleep is interrupted by the signal.
            lines.Add($"trap 'echo hup >> \"{hupLog}\"' HUP");
        }
        foreach (var s in statusLines)
        {
            // single-quote the JSON so the shell passes it through verbatim.
            lines.Add($"echo '{s}'");
        }
        lines.Add(exitCode is { } c
            ? $"exit {c}"
            // Idle until SIGTERM, which terminates the sh with the default disposition → the
            // supervisor's graceful stop observes the exit. Short sleeps so a trapped HUP that
            // interrupts the sleep is handled promptly.
            : "while :; do sleep 0.1; done");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        MakeExecutable(path);
        return path;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static TailscaleSidecarHostedService NewService(
        IConfigProvider config,
        ITailscaleStatus status,
        string binaryPath,
        TimeSpan? backoffBase = null,
        TimeSpan? stopGrace = null,
        IAppPackageCatalog? packages = null) => new(
            config,
            status,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            binaryPath,
            backoffBase ?? TimeSpan.FromMilliseconds(25),
            stopGrace ?? TimeSpan.FromSeconds(2),
            packages);

    /// <summary>Write a package under <paramref name="root"/> declaring one tailnet forward, so a
    /// real <see cref="AppPackageCatalog"/> surfaces it. The state/package dirs live under the
    /// test temp dir (overridden roots).</summary>
    private static string WritePackageWithForward(string root, string id, int listen, string target)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AppPackageCatalog.ManifestFileName), $"""
            manifest: 1
            id: {id}
            service:
              command: /bin/{id}
            forward:
              - listen: {listen}
                target: {target}
                tls: terminate
            """);
        return dir;
    }

    /// <summary>A NodeConfig with tailscale enabled, an app-package root, and (optionally) the
    /// apps: overrides that enable the discovered packages.</summary>
    private static NodeConfig NodeWithApps(
        TailscaleConfig tailscale, string appRoot, params AppOverrideConfig[] apps) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Tailscale = tailscale,
        AppPackageRoots = [appRoot],
        Apps = apps,
    };

    [Fact]
    public async Task Enabled_launches_the_child_and_status_transitions_to_running_with_the_fqdn()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bin = WriteFakeSidecar("ts-ok", [
            "{\"state\":\"starting\"}",
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ]);
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => status.Current.State == "running", "status running");

        Assert.True(status.Current.Enabled);
        Assert.Equal("pdn.test.ts.net", status.Current.Fqdn);
        Assert.Null(status.Current.AuthUrl);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Needs_login_status_surfaces_the_auth_url()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bin = WriteFakeSidecar("ts-login", [
            "{\"state\":\"starting\"}",
            "{\"state\":\"needs-login\",\"authURL\":\"https://login.tailscale.com/a/abc123\"}",
        ]);
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => status.Current.State == "needs-login", "status needs-login");
        Assert.Equal("https://login.tailscale.com/a/abc123", status.Current.AuthUrl);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_config_runs_nothing_and_status_stays_disabled()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Point at a path that does not exist — if the service tried to launch it'd error;
        // because the config is disabled it must never launch, so status stays disabled.
        var bin = Path.Combine(dir, "does-not-exist");
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(new TailscaleConfig { Enabled = false }));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(150);   // bounded observation window for a (forbidden) launch
        Assert.False(status.Current.Enabled);
        Assert.Equal("disabled", status.Current.State);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_child_that_exits_triggers_a_backoff_restart()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The fake records each launch, emits running, then exits 1 — so each respawn appends a
        // line. Two-plus launches proves the supervisor restarted on the unexpected exit.
        var runsFile = Path.Combine(dir, "runs.txt");
        var bin = WriteFakeSidecar("ts-flap", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], exitCode: 1, argsLog: runsFile);
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        await using var svc = NewService(config, status, bin, backoffBase: TimeSpan.FromMilliseconds(20));

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(runsFile) && File.ReadAllLines(runsFile).Length >= 2,
            "the child was respawned after it exited");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabling_via_config_change_stops_the_running_child()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bin = WriteFakeSidecar("ts-toggle", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ]);
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => status.Current.State == "running", "running");

        // Flip the config to disabled and raise OnChange (the SqliteConfigProvider web-edit seam).
        config.Apply(Node(new TailscaleConfig { Enabled = false }));
        await Wait.ForAsync(() => status.Current.State == "disabled", "disabled after the config change");
        Assert.False(status.Current.Enabled);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_relevant_field_change_restarts_the_child()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runsFile = Path.Combine(dir, "runs.txt");
        var bin = WriteFakeSidecar("ts-reconf", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: runsFile);
        var status = new TailscaleStatusHolder();
        var cfg = Enabled(Path.Combine(dir, "state"));
        var config = new TestConfigProvider(Node(cfg));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => File.Exists(runsFile) && File.ReadAllLines(runsFile).Length >= 1, "first launch");

        // Change a spawn-relevant field (hostname) → the child must be torn down and relaunched.
        config.Apply(Node(cfg with { Hostname = "pdn2" }));
        await Wait.ForAsync(() => File.ReadAllLines(runsFile).Length >= 2, "relaunched on the hostname change");
        // The new launch carries the new hostname.
        Assert.Contains(File.ReadAllLines(runsFile), l => l.Contains("--hostname pdn2", StringComparison.Ordinal));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_child_is_launched_with_the_pinned_flag_contract()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runsFile = Path.Combine(dir, "args.txt");
        var bin = WriteFakeSidecar("ts-flags", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: runsFile);
        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var keyFile = Path.Combine(dir, "authkey");
        File.WriteAllText(keyFile, "tskey-abc");
        var cfg = new TailscaleConfig
        {
            Enabled = true,
            Hostname = "pdn",
            StateDir = stateDir,
            Target = "127.0.0.1:9090",
            AuthKeyFile = keyFile,
            Funnel = true,
        };
        var config = new TestConfigProvider(Node(cfg));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => File.Exists(runsFile) && File.ReadAllLines(runsFile).Length >= 1, "launched");
        var argv = File.ReadAllLines(runsFile)[0];
        Assert.Contains("--hostname pdn", argv, StringComparison.Ordinal);
        Assert.Contains($"--state-dir {stateDir}", argv, StringComparison.Ordinal);
        Assert.Contains("--target 127.0.0.1:9090", argv, StringComparison.Ordinal);
        Assert.Contains($"--authkey-file {keyFile}", argv, StringComparison.Ordinal);
        Assert.Contains("--funnel", argv, StringComparison.Ordinal);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_inline_auth_key_is_passed_via_a_private_temp_file_not_the_command_line()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runsFile = Path.Combine(dir, "args.txt");
        var bin = WriteFakeSidecar("ts-inlinekey", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: runsFile);
        var status = new TailscaleStatusHolder();
        var cfg = new TailscaleConfig
        {
            Enabled = true,
            Hostname = "pdn",
            StateDir = Path.Combine(dir, "state"),
            Target = "127.0.0.1:8080",
            AuthKey = "tskey-secret-value",
        };
        var config = new TestConfigProvider(Node(cfg));
        await using var svc = NewService(config, status, bin);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => File.Exists(runsFile) && File.ReadAllLines(runsFile).Length >= 1, "launched");
        var argv = File.ReadAllLines(runsFile)[0];
        // The secret must not appear on the command line; a --authkey-file path must.
        Assert.DoesNotContain("tskey-secret-value", argv, StringComparison.Ordinal);
        Assert.Contains("--authkey-file", argv, StringComparison.Ordinal);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_binary_when_enabled_yields_error_status_and_no_crash()
    {
        if (!OperatingSystem.IsLinux()) return;
        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        // Point at a non-existent binary while enabled — the surface must stay total.
        await using var svc = NewService(
            config, status, Path.Combine(dir, "nope"), backoffBase: TimeSpan.FromMilliseconds(20));

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => status.Current.State == "error", "error status on a missing binary");
        Assert.True(status.Current.Enabled);
        Assert.NotNull(status.Current.Error);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_sigterms_the_child()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The fake traps TERM and records it before exiting — proving a graceful SIGTERM stop.
        var path = Path.Combine(dir, "ts-polite");
        var marker = Path.Combine(dir, "term.marker");
        var ready = Path.Combine(dir, "ready");
        var script = string.Join('\n', [
            "#!/bin/sh",
            "trap 'echo bye > \"" + marker + "\"; exit 0' TERM",
            "echo '{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}'",
            "touch \"" + ready + "\"",
            "while :; do sleep 0.1; done",
        ]) + "\n";
        File.WriteAllText(path, script);
        MakeExecutable(path);

        var status = new TailscaleStatusHolder();
        var config = new TestConfigProvider(Node(Enabled(Path.Combine(dir, "state"))));
        await using var svc = NewService(config, status, path);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => File.Exists(ready), "trap installed");

        await svc.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(marker), "the child must see SIGTERM before any kill");
    }

    // ---- app-declared tailnet forwards (docs/network-access.md) ----------------------

    [Fact]
    public async Task An_enabled_package_forward_writes_the_forwards_file_and_passes_the_flag()
    {
        if (!OperatingSystem.IsLinux()) return;
        var argsLog = Path.Combine(dir, "args.txt");
        var bin = WriteFakeSidecar("ts-fwd", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var config = new TestConfigProvider(NodeWithApps(
            Enabled(stateDir), appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "launched");

        // The flag points at the forwards file in the state dir, and the file holds the pinned JSON.
        var forwardsFile = Path.Combine(stateDir, TailscaleSidecarHostedService.ForwardsFileName);
        var argv = File.ReadAllLines(argsLog)[0];
        Assert.Contains($"--forwards-file {forwardsFile}", argv, StringComparison.Ordinal);
        Assert.True(File.Exists(forwardsFile));
        var json = File.ReadAllText(forwardsFile);
        Assert.Equal(
            "[{\"listen\":993,\"target\":\"127.0.0.1:1430\",\"tls\":\"terminate\"}]", json);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_disabled_forward_package_contributes_no_forwards_and_no_flag()
    {
        if (!OperatingSystem.IsLinux()) return;
        var argsLog = Path.Combine(dir, "args.txt");
        var bin = WriteFakeSidecar("ts-nofwd", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        // Present but NOT enabled (no apps: override) → its forward must not be exposed.
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var config = new TestConfigProvider(NodeWithApps(Enabled(stateDir), appRoot));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "launched");

        var argv = File.ReadAllLines(argsLog)[0];
        Assert.DoesNotContain("--forwards-file", argv, StringComparison.Ordinal);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enabling_a_forward_package_live_reloads_via_sighup_without_restarting_the_sidecar()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The whole point of the fix: a forwards-only change must NOT tear down + rejoin the tailnet
        // (which would drop the operator's control-panel session). It rewrites forwards.json and
        // SIGHUPs the SAME child — proved by exactly one launch line plus a recorded HUP.
        var argsLog = Path.Combine(dir, "args.txt");
        var hupLog = Path.Combine(dir, "hup.txt");
        var bin = WriteFakeSidecar("ts-fwd-toggle", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog, hupLog: hupLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        // First launch: tailscale on, the forward package still disabled → no --forwards-file.
        var config = new TestConfigProvider(NodeWithApps(Enabled(stateDir), appRoot));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "first launch");
        Assert.DoesNotContain("--forwards-file", File.ReadAllLines(argsLog)[0], StringComparison.Ordinal);

        // Enable the forward-declaring app → forwards-only change: rewrite forwards.json + SIGHUP.
        config.Apply(NodeWithApps(
            Enabled(stateDir), appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));

        // The child saw a SIGHUP …
        await Wait.ForAsync(
            () => File.Exists(hupLog) && File.ReadAllLines(hupLog).Length >= 1,
            "the child was SIGHUPed for the forwards reload");
        // … and forwards.json was (re)written with the now-enabled forward …
        var forwardsFile = Path.Combine(stateDir, TailscaleSidecarHostedService.ForwardsFileName);
        await Wait.ForAsync(() => File.Exists(forwardsFile), "forwards.json rewritten before the SIGHUP");
        Assert.Equal(
            "[{\"listen\":993,\"target\":\"127.0.0.1:1430\",\"tls\":\"terminate\"}]",
            File.ReadAllText(forwardsFile));
        // … and crucially the child was NOT respawned (still exactly one launch line).
        Assert.Single(File.ReadAllLines(argsLog));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Changing_a_forward_target_live_reloads_via_sighup_without_restarting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var argsLog = Path.Combine(dir, "args.txt");
        var hupLog = Path.Combine(dir, "hup.txt");
        var bin = WriteFakeSidecar("ts-fwd-change", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog, hupLog: hupLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        // First launch already has the forward enabled (it's in forwards.json + the flag).
        var config = new TestConfigProvider(NodeWithApps(
            Enabled(stateDir), appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "first launch");
        Assert.Contains("--forwards-file", File.ReadAllLines(argsLog)[0], StringComparison.Ordinal);

        // Change the forward's target → still a forwards-only change → SIGHUP, no restart.
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:9999");
        config.Apply(NodeWithApps(
            Enabled(stateDir), appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));

        await Wait.ForAsync(
            () => File.Exists(hupLog) && File.ReadAllLines(hupLog).Length >= 1,
            "the child was SIGHUPed for the changed-target reload");
        var forwardsFile = Path.Combine(stateDir, TailscaleSidecarHostedService.ForwardsFileName);
        await Wait.ForAsync(
            () => File.ReadAllText(forwardsFile).Contains("127.0.0.1:9999", StringComparison.Ordinal),
            "forwards.json holds the new target");
        Assert.Single(File.ReadAllLines(argsLog));   // never respawned

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabling_a_forward_package_live_reloads_to_zero_forwards_via_sighup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var argsLog = Path.Combine(dir, "args.txt");
        var hupLog = Path.Combine(dir, "hup.txt");
        var bin = WriteFakeSidecar("ts-fwd-off", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog, hupLog: hupLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var config = new TestConfigProvider(NodeWithApps(
            Enabled(stateDir), appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        var forwardsFile = Path.Combine(stateDir, TailscaleSidecarHostedService.ForwardsFileName);
        await Wait.ForAsync(() => File.Exists(forwardsFile), "forwards.json written on start");

        // Disable the only forward-declaring app → the collected set drops to zero. Still a
        // forwards-only change (node-level fingerprint unchanged) → SIGHUP, file removed, no restart.
        config.Apply(NodeWithApps(Enabled(stateDir), appRoot));
        await Wait.ForAsync(
            () => File.Exists(hupLog) && File.ReadAllLines(hupLog).Length >= 1,
            "the child was SIGHUPed for the drop-to-zero reload");
        await Wait.ForAsync(() => !File.Exists(forwardsFile), "forwards.json removed on the drop to zero");
        Assert.Single(File.ReadAllLines(argsLog));   // never respawned

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_node_level_change_still_restarts_the_sidecar_even_with_forwards()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A node-level field (hostname → a fresh tsnet node) must still restart, never SIGHUP — the
        // forwards split must not regress the restart-on-node-change behaviour.
        var argsLog = Path.Combine(dir, "args.txt");
        var hupLog = Path.Combine(dir, "hup.txt");
        var bin = WriteFakeSidecar("ts-node-change", [
            "{\"state\":\"running\",\"fqdn\":\"pdn.test.ts.net\"}",
        ], argsLog: argsLog, hupLog: hupLog);
        var appRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(appRoot);
        WritePackageWithForward(appRoot, "mail", 993, "127.0.0.1:1430");

        var status = new TailscaleStatusHolder();
        var stateDir = Path.Combine(dir, "state");
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var cfg = Enabled(stateDir);
        var config = new TestConfigProvider(NodeWithApps(
            cfg, appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));
        await using var svc = NewService(config, status, bin, packages: catalog);

        await svc.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "first launch");

        // Change the hostname (node-level) → restart, carrying the forwards forward.
        config.Apply(NodeWithApps(
            cfg with { Hostname = "pdn2" }, appRoot, new AppOverrideConfig { Id = "mail", Enabled = true }));
        await Wait.ForAsync(
            () => File.ReadAllLines(argsLog).Length >= 2, "relaunched on the hostname change");
        Assert.Contains(File.ReadAllLines(argsLog), l => l.Contains("--hostname pdn2", StringComparison.Ordinal));
        // The relaunch carries the forwards (still --forwards-file), and no SIGHUP was used.
        Assert.Contains("--forwards-file", File.ReadAllLines(argsLog)[1], StringComparison.Ordinal);
        Assert.False(File.Exists(hupLog), "a node-level change must restart, never SIGHUP");

        await svc.StopAsync(CancellationToken.None);
    }
}
