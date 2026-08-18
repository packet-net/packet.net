using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Rigs;
using Packet.Node.Tests.Support;
using Packet.Rig.Hamlib;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// The node-managed rigctld supervisor (<see cref="ManagedRigDaemon"/>): against a REAL
/// <c>rigctld</c> driving hamlib's dummy rig where the real thing is best (spawn → readiness →
/// a real <c>RigctldRig</c> client attaches; crash → respawn on the SAME port; dispose → no
/// orphan), and against fake <c>/bin/sh</c> children for the failure paths (exit-loop → not
/// ready within budget; missing binary → fast clean not-ready) plus the pinned argument
/// contract. Linux-only like the rest of the process-spawning suite (the tsnet tests' seam).
/// </summary>
[Trait("Category", "Node")]
public sealed class ManagedRigDaemonTests : IDisposable
{
    private static readonly string? RigctldPath = FindRigctld();

    private readonly string dir;

    public ManagedRigDaemonTests()
    {
        dir = TestPaths.NewPath("pdn-rigctld");
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static string? FindRigctld()
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "rigctld"))
            .FirstOrDefault(File.Exists);

    /// <summary>The dummy-rig (model 1) node-managed block - the ecosystem's standard client
    /// test harness; the dummy ignores the device it is handed.</summary>
    private static PortRigConfig DummyRig() => new()
    {
        Kind = "hamlib",
        Device = "/dev/null",
        Model = 1,
    };

    private static ManagedRigDaemon Start(PortRigConfig config, string? binaryPath = null) =>
        ManagedRigDaemon.Start(
            "hf", config, NullLoggerFactory.Instance, TimeProvider.System,
            binaryPath, backoffBase: TimeSpan.FromMilliseconds(25), stopGrace: TimeSpan.FromSeconds(2));

    /// <summary>Write an executable fake rigctld that (optionally) appends its argv to
    /// <paramref name="argsLog"/>, then either idles until SIGTERM or exits with the given code
    /// (to exercise the respawn/not-ready paths). Mirrors the tsnet suite's fake-sidecar seam.</summary>
    private string WriteFakeRigctld(string name, int? exitCode = null, string? argsLog = null)
    {
        var path = Path.Combine(dir, name);
        var lines = new List<string> { "#!/bin/sh" };
        if (argsLog is not null)
        {
            lines.Add($"echo \"$@\" >> \"{argsLog}\"");
        }
        lines.Add(exitCode is { } c ? $"exit {c}" : "while :; do sleep 0.1; done");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    /// <summary>
    /// #727 item 12: the "respawning in {Seconds}s" line states the delay the daemon actually
    /// awaits, not the one it held a moment earlier.
    /// </summary>
    /// <remarks>
    /// WP8 added the healthy-run backoff reset AFTER the log call, so a rigctld that had been
    /// flapping (backoff doubled to 8 s or more), then ran healthily for an hour and died,
    /// logged "respawning in 8s" and respawned in 1. An operator timing a device recovery from
    /// the log drew the wrong conclusion. The whole run rides a FakeTimeProvider, so the
    /// backoff growth and the healthy uptime are both exact.
    /// </remarks>
    [SkippableFact]
    public async Task After_a_healthy_run_the_log_states_the_delay_it_actually_awaits()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake rigctld is a POSIX shell script");

        var clock = new FakeTimeProvider();
        var logs = new CapturingLoggerFactory();
        var binary = Path.Combine(dir, "late-rigctld");
        var marker = Path.Combine(dir, "stop.marker");

        await using var daemon = ManagedRigDaemon.Start(
            "hf", DummyRig(), logs, clock, binary,
            backoffBase: TimeSpan.FromSeconds(1), stopGrace: TimeSpan.FromSeconds(2));

        // 1. The binary is not there yet, so every attempt is a spawn FAILURE and the backoff
        //    doubles (1 -> 2 -> 4 -> 8 s). A failed spawn is never a "healthy run", so nothing
        //    resets it. Each poll advances the injected clock past the pending backoff.
        await Wait.ForAsync(
            () => { clock.Advance(TimeSpan.FromSeconds(30)); return Count(logs, "failed to launch") >= 4; },
            "four failed launches, which is enough doubling to tell 1s from the accumulated delay");

        // 2. Now the binary appears: a child that idles until the test tells it to die.
        WriteFakeScript(binary, $"while [ ! -f \"{marker}\" ]; do sleep 0.05; done\nexit 3\n");
        await Wait.ForAsync(
            () => { clock.Advance(TimeSpan.FromSeconds(30)); return daemon.ChildPid is not null; },
            "the child launches once the binary exists");

        // 3. It runs healthily (well past ProcessSupervision.HealthyRunThreshold) and then dies
        //    on its own - the exit path, not the stop path.
        clock.Advance(TimeSpan.FromMinutes(5));
        File.WriteAllText(marker, "go");

        await Wait.ForAsync(() => Count(logs, "respawning in") >= 1, "the unexpected exit is logged");

        var exitLine = logs.Lines.First(l => l.Contains("respawning in", StringComparison.Ordinal));
        exitLine.Should().Contain("respawning in 1s",
            "the healthy run resets the backoff to its base, and that is what the daemon then awaits");
    }

    private static int Count(CapturingLoggerFactory logs, string fragment) =>
        logs.Lines.Count(l => l.Contains(fragment, StringComparison.Ordinal));

    // An executable /bin/sh script at an arbitrary path (WriteFakeRigctld builds the standard
    // shapes; this one takes a body, and deliberately writes it AFTER the daemon has started).
    private static void WriteFakeScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static bool ProcessIsGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;   // no such pid - fully reaped.
        }
    }

    // ---- against the real rigctld -----------------------------------------------------------

    [SkippableFact]
    public async Task Spawns_rigctld_and_a_real_client_attaches_via_the_client_config()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");

        await using var daemon = Start(DummyRig());

        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        daemon.ClientConfig.Host.Should().Be("127.0.0.1");
        daemon.ClientConfig.Port.Should().Be(daemon.Port, "the clients dial the allocated port");

        // The real proof: the production client connects to what we spawned and the
        // \dump_caps-derived identity is the dummy's.
        await using var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions
        {
            Host = daemon.ClientConfig.Host,
            Port = daemon.ClientConfig.Port!.Value,
        });
        rig.Info.Model.Should().Be("Dummy");
        rig.Info.Manufacturer.Should().Be("Hamlib");
    }

    [SkippableFact]
    public async Task A_crashed_child_is_respawned_on_the_same_port_and_a_fresh_client_attaches()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");

        await using var daemon = Start(DummyRig());
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var firstPid = daemon.ChildPid!.Value;

        // Simulate a crash (an unplugged USB device kills rigctld the same way).
        using (var child = Process.GetProcessById(firstPid))
        {
            child.Kill();
        }

        // The supervisor respawns it - a NEW pid, but the SAME allocated port, so the
        // re-dialling clients recover without reconfiguration.
        await Wait.ForAsync(
            () => daemon.ChildPid is { } pid && pid != firstPid,
            "the child was respawned after the crash");
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await using var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions { Port = daemon.Port });
        rig.Info.Model.Should().Be("Dummy");
    }

    [SkippableFact]
    public async Task Dispose_stops_the_child_and_leaves_no_orphan()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");

        var daemon = Start(DummyRig());
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var pid = daemon.ChildPid!.Value;

        await daemon.DisposeAsync();

        ProcessIsGone(pid).Should().BeTrue("dispose must SIGTERM (then reap) the child");
        daemon.ChildPid.Should().BeNull();
        // Idempotent double-dispose.
        var act = async () => await daemon.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // ---- failure paths + the argument contract (fake children) -------------------------------

    [SkippableFact]
    public async Task A_child_that_exits_immediately_is_not_ready_within_the_budget_and_does_not_throw()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake rigctld is a POSIX shell script");

        var bin = WriteFakeRigctld("rigctld-flap", exitCode: 1);
        await using var daemon = Start(DummyRig(), binaryPath: bin);

        // The exit-loop keeps respawning (that's its job), but nothing ever listens: the
        // readiness probe must report false - the caller's degrade signal - never throw.
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(700))).Should().BeFalse();
    }

    [SkippableFact]
    public async Task A_missing_binary_is_a_fast_clean_not_ready()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake rigctld is a POSIX shell script");

        await using var daemon = Start(DummyRig(), binaryPath: Path.Combine(dir, "no-such-rigctld"));

        // A launch that can't even start bails out of the readiness wait early - the whole
        // 10 s budget must NOT be burned on a binary that will never appear.
        var sw = Stopwatch.StartNew();
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a spawn fault fails readiness fast");
    }

    [SkippableFact]
    public async Task The_child_is_launched_with_the_pinned_rigctld_flag_contract()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake rigctld is a POSIX shell script");

        var argsLog = Path.Combine(dir, "args.txt");
        var bin = WriteFakeRigctld("rigctld-flags", argsLog: argsLog);
        await using var daemon = Start(new PortRigConfig
        {
            Kind = "hamlib",
            Device = "/dev/ttyUSB9",
            Model = 3073,
            SerialSpeed = 19200,
        }, binaryPath: bin);

        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "launched");

        var argv = File.ReadAllLines(argsLog)[0];
        argv.Should().Contain("-m 3073");
        argv.Should().Contain("-r /dev/ttyUSB9");
        argv.Should().Contain("-s 19200");
        argv.Should().Contain("-T 127.0.0.1");
        argv.Should().Contain($"-t {daemon.Port}");
    }

    [SkippableFact]
    public async Task Serial_speed_is_omitted_when_unset()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the fake rigctld is a POSIX shell script");

        var argsLog = Path.Combine(dir, "args.txt");
        var bin = WriteFakeRigctld("rigctld-nospeed", argsLog: argsLog);
        await using var daemon = Start(DummyRig(), binaryPath: bin);

        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "launched");

        File.ReadAllLines(argsLog)[0].Should().NotContain("-s ", "null serialSpeed = hamlib's per-model default");
    }
}
