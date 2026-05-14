using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Xunit;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// End-to-end hardware-loop tests against two USB-attached NinoTNCs whose
/// audio paths are wired to each other. These re-implement the manual spike
/// at <c>tools/Packet.NinoTnc.Spike</c> using the production driver
/// (<see cref="NinoTncSerialPort"/>) so a working CI loop run catches
/// regressions in the driver itself.
/// </summary>
/// <remarks>
/// <para>
/// MODE DIP switches must be set to 1111 ("Set from KISS") so the SETHW
/// command can choose the operating mode in software. TX-DELAY pots must
/// be at minimum so the KISS TXDELAY parameter takes effect. Both TNCs
/// are configured for mode 6 (1200 AFSK AX.25) — the slowest, most
/// audio-link-tolerant mode in the catalog.
/// </para>
/// <para>
/// We use the <c>+16</c> non-persist offset on every SETHW so the dev cycle
/// doesn't hammer the TNC's flash.
/// </para>
/// </remarks>
[Trait("Category", "HardwareLoop")]
public class NinoTncSerialPortLoopback
{
    private const byte LoopbackMode = 6;
    private const int FrameWaitSeconds = 10;

    [SkippableFact]
    public async Task FrameReceived_Event_Fires_For_Inbound_Frames()
    {
        var ports = SelectTwoPorts();
        await using var a = NinoTncSerialPort.Open(ports[0]);
        await using var b = NinoTncSerialPort.Open(ports[1]);
        await a.SetModeAsync(LoopbackMode);
        await b.SetModeAsync(LoopbackMode);
        await Task.Delay(500);

        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("TEST", 2),
            source: new Callsign("M0LTE", 1),
            info: "EVENT PROBE"u8);

        var tcs = new TaskCompletionSource();
        b.FrameReceived += (_, frame) =>
        {
            if (frame.Command == KissCommand.Data &&
                Ax25Frame.TryParse(frame.Payload, out var parsed) &&
                parsed.Info.Span.SequenceEqual(ax25.Info.Span))
            {
                tcs.TrySetResult();
            }
        };

        await a.SendFrameAsync(ax25.ToBytes());
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(FrameWaitSeconds));
    }

    [SkippableFact]
    public async Task UI_Frame_Round_Trips_A_To_B_And_Back()
    {
        var ports = SelectTwoPorts();

        await using var a = NinoTncSerialPort.Open(ports[0]);
        await using var b = NinoTncSerialPort.Open(ports[1]);

        await a.SetModeAsync(LoopbackMode);
        await b.SetModeAsync(LoopbackMode);

        // The mode switch needs a moment to settle, and we want a clean slate.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await RoundTripOnce(tx: a, rx: b, label: "A→B");
        await RoundTripOnce(tx: b, rx: a, label: "B→A");
    }

    [SkippableFact]
    public async Task AckMode_Echo_Returns_For_Each_Sequence_Tag()
    {
        var ports = SelectTwoPorts();

        await using var a = NinoTncSerialPort.Open(ports[0]);
        await using var b = NinoTncSerialPort.Open(ports[1]);

        await a.SetModeAsync(LoopbackMode);
        await b.SetModeAsync(LoopbackMode);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var frame = Ax25Frame.Ui(
            destination: new Callsign("TEST", 2),
            source: new Callsign("M0LTE", 1),
            info: "ACKMODE PROBE"u8);

        // Send three frames with distinct tags; each must be acknowledged
        // by the TX-complete echo.
        var receipts = new List<AckModeReceipt>(3);
        for (int i = 0; i < 3; i++)
        {
            var receipt = await a.SendFrameWithAckAsync(
                frame.ToBytes(),
                timeout: TimeSpan.FromSeconds(FrameWaitSeconds));
            receipts.Add(receipt);
        }

        receipts.Select(r => r.SequenceTag).Distinct().Should().HaveCount(3);
        receipts.All(r => r.Elapsed > TimeSpan.Zero).Should().BeTrue();
    }

    private static async Task RoundTripOnce(NinoTncSerialPort tx, NinoTncSerialPort rx, string label)
    {
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("TEST", 2),
            source: new Callsign("M0LTE", 1),
            info: Encoding.ASCII.GetBytes($"LOOP {label}"));
        byte[] sent = ax25.ToBytes();
        var seen = new List<KissFrame>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(FrameWaitSeconds));
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in rx.ReadFramesAsync(cts.Token))
                {
                    seen.Add(frame);
                    if (frame.Command != KissCommand.Data) continue;
                    if (!Ax25Frame.TryParse(frame.Payload, out var parsed)) continue;
                    if (parsed.Source.Callsign == ax25.Source.Callsign &&
                        parsed.Destination.Callsign == ax25.Destination.Callsign &&
                        parsed.Info.Span.SequenceEqual(ax25.Info.Span))
                    {
                        return parsed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // fall through to the diagnostic throw below
            }
            string diag = string.Join("; ",
                seen.Select(f => $"port={f.Port} cmd={f.Command} len={f.Payload.Length} payload={Convert.ToHexString(f.Payload)}"));
            throw new InvalidOperationException(
                $"{label}: no matching frame arrived within {FrameWaitSeconds}s. " +
                $"Saw {seen.Count} frame(s): [{diag}]");
        }, CancellationToken.None);

        await tx.SendFrameAsync(sent);

        var parsed = await receiveTask;
        parsed.Info.ToArray().Should().Equal(ax25.Info.ToArray(), $"{label} info payload must round-trip intact");
    }

    private static List<string> SelectTwoPorts()
    {
        var candidates = NinoTncPortDiscovery.EnumerateCandidates();
        Skip.If(
            candidates.Count < 2,
            $"Hardware-loop test: expected ≥2 NinoTNC-class serial devices, " +
            $"found {candidates.Count}. Connect both TNCs over USB and re-run, " +
            $"or set {NinoTncPortDiscovery.PortsEnvVar}=\"<porta>,<portb>\" to pick explicitly.");

        return candidates.Take(2).Select(c => c.PortName).ToList();
    }
}
