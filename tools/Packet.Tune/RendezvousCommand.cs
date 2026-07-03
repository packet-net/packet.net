using System.Globalization;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>rendezvous --listen &lt;port&gt;</c>: run the PIN-rendezvous WebSocket
/// relay (see <see cref="RendezvousRelay"/>) until Ctrl+C. Designed for
/// localhost today and a public host later — put TLS in front before
/// exposing one publicly.
/// </summary>
internal static class RendezvousCommand
{
    public static async Task<int> Run(string[] args)
    {
        int port = 8735;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--listen" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int p))
            {
                port = p;
                i++;
            }
        }

        await using var relay = RendezvousRelay.Start(port);
        relay.Log = Console.WriteLine;
        Console.WriteLine($"rendezvous relay listening on :{relay.Port} — clients join at ws://<host>:{relay.Port} " +
                          "with a shared 6-digit PIN; Ctrl+C to stop");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.TrySetResult();
        };
        await done.Task;
        Console.WriteLine("stopping relay");
        return 0;
    }
}
