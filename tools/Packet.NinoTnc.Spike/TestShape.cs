using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

internal static class TestShape
{
    // Mimics the xUnit test verbatim — no drain.
    public static async Task<int> Run(string portA, string portB)
    {
        Console.WriteLine("TestShape — running xUnit-shaped flow without drain");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);

        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        await RoundTripOnce(a, b, "A→B");
        await RoundTripOnce(b, a, "B→A");
        return 0;
    }

    private static async Task RoundTripOnce(NinoTncSerialPort tx, NinoTncSerialPort rx, string label)
    {
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("TEST", 2),
            source: new Callsign("M0LTE", 1),
            info: Encoding.ASCII.GetBytes($"LOOP {label}"));
        var seen = new List<KissFrame>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in rx.ReadFramesAsync(cts.Token))
                {
                    seen.Add(frame);
                    Console.WriteLine($"  [{label}] saw cmd={frame.Command} len={frame.Payload.Length}");
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
            catch (OperationCanceledException) { }
            throw new InvalidOperationException($"{label}: no match. saw {seen.Count}");
        }, CancellationToken.None);

        await tx.SendFrameAsync(ax25.ToBytes());
        Console.WriteLine($"  [{label}] sent {ax25.ToBytes().Length}B");
        var parsed = await receiveTask;
        Console.WriteLine($"  [{label}] match info=\"{Encoding.ASCII.GetString(parsed.Info.Span)}\"");
    }
}
