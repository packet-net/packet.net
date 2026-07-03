using System.Globalization;
using Packet.Radio.Tait;

namespace Packet.Tune;

/// <summary>
/// <c>radio-channel</c>: report a Tait radio's current channel (FUNCTION
/// 0/5/2), or — with a channel argument — GO_TO_CHANNEL and verify the switch.
/// </summary>
internal static class RadioChannelCommand
{
    public static async Task<int> Run(string ccdiPort, string[] rest)
    {
        int? target = null;
        if (rest.Length > 0)
        {
            if (!int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ch))
            {
                Console.WriteLine($"radio-channel: bad channel '{rest[0]}'");
                return 2;
            }
            target = ch;
        }

        await using var radio = TaitCcdiRadio.Open(ccdiPort);
        if (target is { } channel)
        {
            Console.WriteLine($"radio-channel — {ccdiPort}: GO_TO_CHANNEL {channel}");
            await radio.GoToChannelAsync(channel);
        }

        var report = await radio.QueryCurrentChannelAsync();
        Console.WriteLine($"  current channel: kind '{report.Kind}' channel '{report.ChannelId}'" +
                          (report.Zone is { } z ? $" zone {z}" : string.Empty));

        if (target is { } wanted)
        {
            bool ok = int.TryParse(report.ChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out int got) && got == wanted;
            Console.WriteLine(ok ? "  verified" : "  MISMATCH — the radio did not land on the requested channel");
            return ok ? 0 : 1;
        }
        return 0;
    }
}
