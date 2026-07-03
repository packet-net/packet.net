using Packet.Radio.Tait;

namespace Packet.Tune;

/// <summary>
/// <c>radio-reset</c>: soft-reset a Tait radio through CCDI by entering and
/// immediately exiting CCR mode (the CCR exit is a documented soft reset,
/// ~6 s; TM8100-series only). The known use: un-wedging the radio's SDM
/// auto-acknowledge engine after a PTT-vs-ack collision (see
/// <see cref="Packet.Tune.Core.SdmTuningLink"/> remarks) — symptom: the
/// radio still receives SDMs but peers only ever see "not delivered".
/// </summary>
internal static class RadioResetCommand
{
    public static async Task<int> Run(string ccdiPort)
    {
        Console.WriteLine($"radio-reset — {ccdiPort}: CCR enter + exit (soft reset, ~6 s reboot)");
        await using (var radio = TaitCcdiRadio.Open(ccdiPort))
        {
            var identity = await radio.QueryIdentityAsync();
            Console.WriteLine($"  radio: {identity.ProductName} s/n {identity.SerialNumber}");
            var ccr = await radio.EnterCcrModeAsync();
            await ccr.ExitAsync();
            Console.WriteLine("  reset issued — the radio drops off CCDI while it reboots");
        }

        // Wait for it to come back and prove it answers.
        await Task.Delay(TimeSpan.FromSeconds(8));
        await using (var radio = TaitCcdiRadio.Open(ccdiPort))
        {
            float rssi = await radio.ReadRssiDbmAsync();
            Console.WriteLine($"  radio is back (RSSI {rssi.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} dBm)");
        }
        return 0;
    }
}
