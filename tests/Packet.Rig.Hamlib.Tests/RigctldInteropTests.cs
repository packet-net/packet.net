using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Packet.Rig;

namespace Packet.Rig.Hamlib.Tests;

/// <summary>
/// Integration against a REAL <c>rigctld</c> driving hamlib's dummy rig (model 1) — the
/// ecosystem's standard client-test harness (hamlib's own pytest suite does exactly this).
/// Skipped cleanly when <c>rigctld</c> isn't installed; <c>apt install libhamlib-utils</c>
/// lights these up. <c>--set-conf=static_data=1</c> makes the dummy's meters deterministic
/// (RFPOWER_METER 0.5, WATTS 50.0 — stable across hamlib 4.3→master).
/// </summary>
public sealed class RigctldInteropTests
{
    private static readonly string? RigctldPath = FindRigctld();

    private static string? FindRigctld()
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "rigctld"))
            .FirstOrDefault(File.Exists);

    private static async Task<(Process Daemon, RigctldRig Rig)> StartAsync(params string[] extraArgs)
    {
        // Grab a free port, then hand it to rigctld. (Bind-release has a nominal race; in
        // practice the pattern is what the whole ecosystem uses — rigctld can't bind port 0.)
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var daemon = Process.Start(new ProcessStartInfo
        {
            FileName = RigctldPath!,
            Arguments = $"-m 1 -T 127.0.0.1 -t {port} --set-conf=static_data=1 {string.Join(' ', extraArgs)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        try
        {
            // Wait for the listener: ConnectAsync itself is the readiness probe.
            RigException? last = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions { Port = port });
                    return (daemon, rig);
                }
                catch (RigConnectionException ex)
                {
                    last = ex;
                    await Task.Delay(100);
                }
            }

            throw new InvalidOperationException($"rigctld did not come up on port {port}.", last);
        }
        catch
        {
            KillQuietly(daemon);
            throw;
        }
    }

    private static void KillQuietly(Process daemon)
    {
        try
        {
            if (!daemon.HasExited)
            {
                daemon.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        daemon.Dispose();
    }

    [SkippableFact]
    public async Task Connects_And_Probes_The_Dummy_Rig()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");
        var (daemon, rig) = await StartAsync();
        try
        {
            await using var _ = rig;
            rig.Info.Model.Should().Be("Dummy");
            rig.Info.Manufacturer.Should().Be("Hamlib");
            rig.Capabilities.Should().HaveFlag(RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet);
            rig.Capabilities.Should().HaveFlag(RigCapabilities.ModeGet | RigCapabilities.ModeSet);
            rig.Capabilities.Should().HaveFlag(
                RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeter | RigCapabilities.RfPowerMeterWatts);
        }
        finally
        {
            KillQuietly(daemon);
        }
    }

    [SkippableFact]
    public async Task Frequency_And_Mode_Roundtrip_Against_Real_Rigctld()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");
        var (daemon, rig) = await StartAsync();
        try
        {
            await using var _ = rig;

            // Dummy fresh state, stable across hamlib versions.
            (await rig.GetFrequencyAsync()).Should().Be(145_000_000);
            (await rig.GetModeAsync()).Should().Be(new RigModeState(RigMode.Fm, 15_000));

            await rig.SetFrequencyAsync(14_074_000);
            (await rig.GetFrequencyAsync()).Should().Be(14_074_000);

            await rig.SetModeAsync(RigMode.PktUsb, 3000);
            (await rig.GetModeAsync()).Should().Be(new RigModeState(RigMode.PktUsb, 3000));

            // Passband null → the rig default for the mode.
            await rig.SetModeAsync(RigMode.Usb);
            var state = await rig.GetModeAsync();
            state.Mode.Should().Be(RigMode.Usb);
            state.PassbandHz.Should().BeGreaterThan(0);
        }
        finally
        {
            KillQuietly(daemon);
        }
    }

    [SkippableFact]
    public async Task Meters_Read_Deterministic_Static_Data_Values()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");
        var (daemon, rig) = await StartAsync();
        try
        {
            await using var _ = rig;

            // static_data=1 pins the simulated meters; SWR isn't simulated at all on the dummy
            // (reads as last-set, initially 0) — asserting exact values proves the level plumbing.
            (await rig.ReadRfPowerAsync()).Should().Be(0.5);
            (await rig.ReadRfPowerWattsAsync()).Should().Be(50.0);
            (await rig.ReadSwrAsync()).Should().Be(0.0);
            (await rig.ReadLevelAsync("STRENGTH")).Should().Be(-12);
        }
        finally
        {
            KillQuietly(daemon);
        }
    }

    [SkippableFact]
    public async Task Vfo_Mode_Daemon_Works_Through_CurrVfo_Injection()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");
        var (daemon, rig) = await StartAsync("-o");
        try
        {
            await using var _ = rig;

            await rig.SetFrequencyAsync(7_074_000);
            (await rig.GetFrequencyAsync()).Should().Be(7_074_000);
            (await rig.GetModeAsync()).Mode.Should().Be(RigMode.Fm);
        }
        finally
        {
            KillQuietly(daemon);
        }
    }

    [SkippableFact]
    public async Task Unknown_Level_Yields_A_Typed_Command_Error()
    {
        Skip.If(RigctldPath is null, "rigctld not installed (apt install libhamlib-utils)");
        var (daemon, rig) = await StartAsync();
        try
        {
            await using var _ = rig;

            var act = async () => await rig.ReadLevelAsync("NOSUCHLEVEL");
            (await act.Should().ThrowAsync<RigCommandException>())
                .Which.BackendErrorCode.Should().Be(1); // RIG_EINVAL
        }
        finally
        {
            KillQuietly(daemon);
        }
    }
}
