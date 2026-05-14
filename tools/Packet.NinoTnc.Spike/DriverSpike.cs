using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

internal static class DriverSpike
{
    public static async Task<int> Run(string portA, string portB)
    {
        Console.WriteLine($"DriverSpike — A={portA}, B={portB}");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);

        Console.WriteLine("setting mode 6 on both");
        await a.SetModeAsync(6);
        await b.SetModeAsync(6);
        await Task.Delay(500);

        Console.WriteLine("drain any boot/SETHW chatter from both inbound channels");
        await DrainChannel(a, "A");
        await DrainChannel(b, "B");

        // A → B
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("TEST", 2),
            source: new Callsign("M0LTE", 1),
            info: Encoding.ASCII.GetBytes("DRIVER LOOP"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rxTask = Task.Run(async () =>
        {
            await foreach (var frame in b.ReadFramesAsync(cts.Token))
            {
                Console.WriteLine($"  B got cmd={frame.Command} len={frame.Payload.Length}");
                if (frame.Command == KissCommand.Data &&
                    Ax25Frame.TryParse(frame.Payload, out var parsed) &&
                    parsed.Info.Span.SequenceEqual(ax25.Info.Span))
                {
                    return parsed;
                }
            }
            throw new InvalidOperationException("rx never matched");
        }, CancellationToken.None);

        Console.WriteLine("A: sending AX.25 UI frame");
        await a.SendFrameAsync(ax25.ToBytes());
        Console.WriteLine("A: send returned");

        try
        {
            var got = await rxTask;
            Console.WriteLine($"OK — round-trip succeeded, info=\"{Encoding.ASCII.GetString(got.Info.Span)}\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL — {ex.Message}");
            return 1;
        }
    }

    private static async Task DrainChannel(NinoTncSerialPort tnc, string label)
    {
        using var quickCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var f in tnc.ReadFramesAsync(quickCts.Token))
            {
                Console.WriteLine($"  drained {label} cmd={f.Command} len={f.Payload.Length}");
            }
        }
        catch (OperationCanceledException) { }
    }
}
