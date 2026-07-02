using Packet.Radio.Tait;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Tait.Spike;

/// <summary>
/// Radio-to-radio short data message (SDM) over the air — no TNC involved: the radios' own
/// FFSK modems carry it. Sends from one radio, watches the other for the FFSK-received
/// PROGRESS / RING, then drains its SDM buffer.
/// </summary>
internal static class SdmProbe
{
    public static async Task<int> Run(string fromPort, string toPort)
    {
        await using var sender = TaitCcdiRadio.Open(fromPort);
        await using var receiver = TaitCcdiRadio.Open(toPort);

        receiver.ProgressReceived += (_, p) => Console.WriteLine($"  rx radio progress: {p.Type} {p.Para}");
        receiver.RingReceived += (_, r) =>
            Console.WriteLine($"  rx radio RING: category={r.Category} type={r.RingType} status={r.Status}");
        await receiver.SetProgressMessagesAsync(true);

        // Clear any stale buffered SDM first (the buffer is one-deep).
        string? stale = await SafeReadSdmAsync(receiver);
        if (stale is not null)
        {
            Console.WriteLine($"  cleared stale SDM: \"{stale}\"");
        }

        Console.WriteLine("sending SDM \"HELLO FROM PACKET.NET\" to ID 12345678...");
        await sender.SendSdmAsync("12345678", "HELLO FROM PACKET.NET");

        await Task.Delay(3000); // lead-in + FFSK airtime + processing

        string? received = await SafeReadSdmAsync(receiver);
        Console.WriteLine(received is not null
            ? $"RECEIVED over RF: \"{received}\""
            : "no SDM buffered on the receiver (ID mismatch or FFSK not decoded)");
        return received is not null ? 0 : 1;
    }

    private static async Task<string?> SafeReadSdmAsync(TaitCcdiRadio radio)
    {
        try
        {
            return await radio.ReadBufferedSdmAsync();
        }
        catch (TaitCcdiException ex) when (ex.Error.ErrorNumber == 0x06)
        {
            return null; // SDMs not enabled in this radio's programming
        }
    }
}
