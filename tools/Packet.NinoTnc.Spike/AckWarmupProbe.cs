using System.Diagnostics;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Investigation harness for the "first ACKMODE after Open+SetMode takes
/// ~15 seconds" finding. Runs four scenarios back-to-back and prints the
/// timing of the first and second ACKMODE-with-echo round trips:
///
///   1) Vanilla — Open, SetMode, 500ms, send ACKMODE.
///   2) Long settle — Open, SetMode, 5s, send ACKMODE.
///   3) Prime with KISS Data first — Open, SetMode, 500ms, send Data
///      (await over-air), then ACKMODE.
///   4) Prime with first ACKMODE — Open, SetMode, 500ms, send ACKMODE
///      to consume the warmup, then send the "real" ACKMODE we measure.
///
/// Each scenario opens fresh modems so port state doesn't leak across.
/// </summary>
internal static class AckWarmupProbe
{
    public static async Task<int> Run(string portA, string portB)
    {
        Console.WriteLine("AckWarmupProbe — investigating the 15 s first-ACK quirk");

        await Scenario1_Vanilla(portA, portB);
        await Scenario2_LongSettle(portA, portB);
        await Scenario3_PrimeWithData(portA, portB);
        await Scenario4_PrimeWithAck(portA, portB);
        await Scenario5_BackedUpQueue(portA, portB);

        return 0;
    }

    private static async Task Scenario5_BackedUpQueue(string portA, string portB)
    {
        Console.WriteLine("\n=== Scenario 5: Open + SetMode + 20 back-to-back KISS Data (no await) + ACKMODE ===");
        Console.WriteLine("    mimics the soak's throughput-then-ackmode sequence");
        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        var burstFrame = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), new byte[200]);
        var burstBytes = burstFrame.ToBytes();
        Console.WriteLine("  bursting 20 200B frames...");
        var burstSw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await a.SendFrameAsync(burstBytes);
        }
        Console.WriteLine($"  burst submission returned in {burstSw.ElapsedMilliseconds} ms");

        await SendAndTime(a, "first ACK (after burst)");
        await SendAndTime(a, "second ACK");
    }

    private static async Task Scenario1_Vanilla(string portA, string portB)
    {
        Console.WriteLine("\n=== Scenario 1: Open + SetMode + 500ms + ACKMODE ===");
        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        await SendAndTime(a, "first ACK");
        await SendAndTime(a, "second ACK");
    }

    private static async Task Scenario2_LongSettle(string portA, string portB)
    {
        Console.WriteLine("\n=== Scenario 2: Open + SetMode + 5s + ACKMODE ===");
        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(5000);

        await SendAndTime(a, "first ACK");
        await SendAndTime(a, "second ACK");
    }

    private static async Task Scenario3_PrimeWithData(string portA, string portB)
    {
        Console.WriteLine("\n=== Scenario 3: Open + SetMode + 500ms + KISS Data prime + ACKMODE ===");
        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        var primeFrame = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), "PRIME"u8);
        var sw = Stopwatch.StartNew();
        var rxTcs = new TaskCompletionSource();
        b.FrameReceived += FrameReceivedCheck;
        await a.SendFrameAsync(primeFrame.ToBytes());
        try
        {
            await rxTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"  prime: TIMEOUT after {sw.ElapsedMilliseconds} ms");
            b.FrameReceived -= FrameReceivedCheck;
            return;
        }
        b.FrameReceived -= FrameReceivedCheck;
        Console.WriteLine($"  prime (KISS Data) received on B after {sw.ElapsedMilliseconds} ms");

        await SendAndTime(a, "first ACK");
        await SendAndTime(a, "second ACK");

        void FrameReceivedCheck(object? sender, KissFrame frame)
        {
            if (frame.Command == KissCommand.Data &&
                Ax25Frame.TryParse(frame.Payload, out var p) &&
                p.Info.Span.SequenceEqual(primeFrame.Info.Span))
            {
                rxTcs.TrySetResult();
            }
        }
    }

    private static async Task Scenario4_PrimeWithAck(string portA, string portB)
    {
        Console.WriteLine("\n=== Scenario 4: Open + SetMode + 500ms + warmup ACKMODE + measured ACKMODE ===");
        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        await SendAndTime(a, "warmup ACK");
        await SendAndTime(a, "first 'real' ACK");
        await SendAndTime(a, "second 'real' ACK");
    }

    private static async Task SendAndTime(NinoTncSerialPort tnc, string label)
    {
        var ax25 = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), System.Text.Encoding.ASCII.GetBytes(label));
        try
        {
            var receipt = await tnc.SendFrameWithAckAsync(ax25.ToBytes(), TimeSpan.FromSeconds(30));
            Console.WriteLine($"  {label}: {receipt.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"  {label}: TIMEOUT");
        }
    }
}
