using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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
        dir = Path.Combine(Path.GetTempPath(), "pdn-rigctld-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>The dummy-rig (model 1) node-managed block — the ecosystem's standard client
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

    private static bool ProcessIsGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;   // no such pid — fully reaped.
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

        // The supervisor respawns it — a NEW pid, but the SAME allocated port, so the
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

    [Fact]
    public async Task A_child_that_exits_immediately_is_not_ready_within_the_budget_and_does_not_throw()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var bin = WriteFakeRigctld("rigctld-flap", exitCode: 1);
        await using var daemon = Start(DummyRig(), binaryPath: bin);

        // The exit-loop keeps respawning (that's its job), but nothing ever listens: the
        // readiness probe must report false — the caller's degrade signal — never throw.
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(700))).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_binary_is_a_fast_clean_not_ready()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var daemon = Start(DummyRig(), binaryPath: Path.Combine(dir, "no-such-rigctld"));

        // A launch that can't even start bails out of the readiness wait early — the whole
        // 10 s budget must NOT be burned on a binary that will never appear.
        var sw = Stopwatch.StartNew();
        (await daemon.WaitUntilReadyAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a spawn fault fails readiness fast");
    }

    [Fact]
    public async Task The_child_is_launched_with_the_pinned_rigctld_flag_contract()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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

    [Fact]
    public async Task Serial_speed_is_omitted_when_unset()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argsLog = Path.Combine(dir, "args.txt");
        var bin = WriteFakeRigctld("rigctld-nospeed", argsLog: argsLog);
        await using var daemon = Start(DummyRig(), binaryPath: bin);

        await Wait.ForAsync(
            () => File.Exists(argsLog) && File.ReadAllLines(argsLog).Length >= 1, "launched");

        File.ReadAllLines(argsLog)[0].Should().NotContain("-s ", "null serialSpeed = hamlib's per-model default");
    }
}
