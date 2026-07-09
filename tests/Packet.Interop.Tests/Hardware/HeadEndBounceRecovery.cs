using System.Diagnostics;
using System.Text.Json;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Xunit;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// #576 regression on real hardware (RF-free): adopt a Tait radio through the split-station head-end
/// with the production <see cref="RadioControlFactory"/> + <see cref="HeadEndDeviceResolver"/> +
/// <see cref="ReconnectingRadioControl"/>, then bounce the head-end (kill + restart) and prove the
/// facade detects the fault, re-resolves the inventory by-path device, re-clocks, and re-adopts —
/// control resumes on a freshly-swapped driver. Needs the head-end binary (headend/dist or the
/// <c>PACKETNET_HEADEND_BIN</c> override) + a Tait radio for it to bridge.
/// </summary>
[Trait("Category", "HardwareLoop")]
[Collection(HardwareLoopCollection.Name)]
public class HeadEndBounceRecovery
{
    private const string Addr = "127.0.0.1:7300";
    private const string HeadEndId = "bench";

    [SkippableFact]
    public async Task Head_end_bounce_is_recovered_by_reconnecting_radio_control()
    {
        string bin = FindHeadEndBinary();
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Addr}"), Timeout = TimeSpan.FromSeconds(3) };

        var headEnd = StartHeadEnd(bin);
        try
        {
            var device = await WaitForTaitDeviceAsync(http);
            Skip.If(device is null, "HardwareLoop: no CP2102 (Tait) device in the head-end inventory to adopt.");

            var headEnds = new List<HeadEndConfig> { new() { Id = HeadEndId, Address = Addr } };
            HeadEndDeviceResolver NewResolver() => new(headEnds);
            var config = new PortRadioConfig
            {
                Kind = RadioKinds.TaitCcdi,
                HeadEndId = HeadEndId,
                DeviceId = device!.Value.Id,
                Baud = 28800,
            };
            var factory = RadioControlFactory.Instance;

            var initial = await factory.CreateAsync(config, TimeProvider.System, NewResolver());
            await using var reconn = new ReconnectingRadioControl(
                initial, "bench-port", config, factory, NewResolver, logger: null, TimeProvider.System,
                minBackoff: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromSeconds(4));

            int swaps = 0;
            reconn.InnerChanged += (_, _) => Interlocked.Increment(ref swaps);

            int ok = 0, fail = 0;
            bool controlAlive = false;
            using var probeCts = new CancellationTokenSource();
            var probe = Task.Run(async () =>
            {
                while (!probeCts.IsCancellationRequested)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await reconn.ReadRssiDbmAsync(cts.Token);
                        Interlocked.Increment(ref ok);
                        controlAlive = true;
                    }
                    catch
                    {
                        Interlocked.Increment(ref fail);
                        controlAlive = false;
                    }
                    try { await Task.Delay(1000, probeCts.Token); } catch { }
                }
            });

            await Task.Delay(5000);
            int okBeforeBounce = ok;

            headEnd.Kill(entireProcessTree: true);
            await headEnd.WaitForExitAsync();
            await Task.Delay(7000);
            int failDuringOutage = fail;

            headEnd = StartHeadEnd(bin);
            await WaitForTaitDeviceAsync(http);
            await Task.Delay(10000);

            await probeCts.CancelAsync();
            try { await probe; } catch { }

            okBeforeBounce.Should().BeGreaterThan(0, "control worked through the head-end before the bounce");
            failDuringOutage.Should().BeGreaterThan(0, "the fault was detected while the head-end was down");
            swaps.Should().BeGreaterThanOrEqualTo(1, "a fresh driver was swapped in on reopen");
            controlAlive.Should().BeTrue("control resumed once the head-end came back");
        }
        finally
        {
            try { headEnd.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    private static Process StartHeadEnd(string bin)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = bin,
                Arguments = "--http-port 7300 --baud 28800 --rescan-interval 0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<(string Id, int Tcp)?> WaitForTaitDeviceAsync(HttpClient http, int timeoutSec = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var json = await http.GetStringAsync("/inventory");
                using var doc = JsonDocument.Parse(json);
                foreach (var port in doc.RootElement.GetProperty("ports").EnumerateArray())
                {
                    string id = port.GetProperty("id").GetString() ?? "";
                    string byId = port.TryGetProperty("byId", out var b) ? b.GetString() ?? "" : "";
                    if (byId.Contains("CP210", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("CP210", StringComparison.OrdinalIgnoreCase))
                    {
                        return (id, port.GetProperty("tcpPort").GetInt32());
                    }
                }
            }
            catch { /* head-end not accepting yet */ }
            await Task.Delay(300);
        }
        return null;
    }

    private static string FindHeadEndBinary()
    {
        if (Environment.GetEnvironmentVariable("PACKETNET_HEADEND_BIN") is { Length: > 0 } env && File.Exists(env))
        {
            return env;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "headend", "dist", "packetnet-headend-linux-amd64");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        Skip.If(true, "HardwareLoop: head-end binary not found (build headend/dist or set PACKETNET_HEADEND_BIN).");
        return string.Empty; // unreachable — Skip.If(true) always throws
    }
}
