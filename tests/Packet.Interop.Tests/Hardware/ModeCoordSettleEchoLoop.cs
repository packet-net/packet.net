using System.Diagnostics;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;
using Xunit;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// #591 regression on the live station: <see cref="NinoTncModeCoordStation.ApplyModeAsync"/> waits
/// ~1 s after SETHW before its settle frame so the NinoTNC's ACKMODE TX-completion echo lands
/// reliably. Without that delay a settle frame sent immediately after SETHW had its echo dropped
/// ~60% of the time (bench-measured 8/12 mode-change, 7/12 same-mode), each miss then paying the
/// full settle timeout. Needs one NinoTNC + one Tait radio (the settle frame keys the radio).
/// </summary>
[Trait("Category", "HardwareLoop")]
[Collection(HardwareLoopCollection.Name)]
public class ModeCoordSettleEchoLoop
{
    [SkippableFact]
    public async Task ApplyMode_settle_echo_lands_and_applies_stay_fast()
    {
        string tncPort = SelectNinoTnc();
        string radioPort = await SelectTaitAsync();
        await using var tnc = NinoTncSerialPort.Open(tncPort);
        using var radio = TaitCcdiRadio.Open(radioPort);
        await tnc.SetModeAsync(6);
        await Task.Delay(1500);

        var station = new NinoTncModeCoordStation(tnc, radio, new Callsign("PN0TST", 1), initialMode: 6);
        int settleMisses = 0;
        station.Log = line =>
        {
            if (line.Contains("not echoed", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref settleMisses);
            }
        };

        byte[] cycle = [6, 2];
        var applyMs = new List<double>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                await station.ApplyModeAsync(cycle[i % 2]);
                applyMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                await Task.Delay(300);
            }
        }
        finally
        {
            await tnc.SetModeAsync(6); // leave the TNC on the home mode
        }

        settleMisses.Should().BeLessThanOrEqualTo(1,
            "the post-SETHW settling delay (#591) keeps the ACKMODE echo landing — ~60% missed without it");
        applyMs.Max().Should().BeLessThan(5000,
            "no apply should stall on a missing echo (the old 8 s settle timeout is gone)");
    }

    private static string SelectNinoTnc()
    {
        var candidates = NinoTncPortDiscovery.EnumerateCandidates();
        Skip.If(candidates.Count < 1,
            $"HardwareLoop: needs ≥1 NinoTNC; found {candidates.Count}. Set {NinoTncPortDiscovery.PortsEnvVar}.");
        return candidates[0].PortName;
    }

    private static async Task<string> SelectTaitAsync()
    {
        string? port = null;
        await foreach (var radio in TaitRadioPortDiscovery.DiscoverAsync())
        {
            port = radio.Port;
            break;
        }
        Skip.If(port is null,
            $"HardwareLoop: no Tait radio found. Set {TaitRadioPortDiscovery.PortsOverrideEnvVar} to pick one.");
        return port!;
    }
}
