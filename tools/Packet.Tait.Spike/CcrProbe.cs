using Packet.Radio.Tait;

namespace Packet.Tait.Spike;

/// <summary>
/// Live CCR-mode validation: enter CCR, pulse, set volume + TX power (the power control the
/// CCDI side lacks), optionally key briefly to prove the power step, then exit (soft reset).
/// Run against the TX-side radio so the RX radio can observe the power difference.
/// </summary>
internal static class CcrProbe
{
    public static async Task<int> Run(string ccrPort, string? observerPort, string? kissTxPort = null)
    {
        await using var radio = TaitCcdiRadio.Open(ccrPort);
        TaitCcdiRadio? observer = observerPort is null ? null : TaitCcdiRadio.Open(observerPort);
        try
        {
            Console.WriteLine($"identity: {(await radio.QueryIdentityAsync()).ProductName}");
            Console.WriteLine("entering CCR mode...");
            var ccr = await radio.EnterCcrModeAsync();
            Console.WriteLine($"  in CCR (mode={radio.Mode})");

            bool configured = await ccr.PulseAsync();
            Console.WriteLine($"  pulse: minimum-config={configured} (expect False before an RX-frequency set)");

            await ccr.SetVolumeAsync(10);
            Console.WriteLine("  volume set to 10: ACK");

            await ccr.SetTransmitterPowerAsync(TaitTxPower.VeryLow);
            Console.WriteLine("  TX power VeryLow: ACK (latched for next TX)");

            if (kissTxPort is not null && observer is not null)
            {
                // Does the data-line PTT still key the radio while it sits in CCR mode? If so,
                // the TX inherits the channel frequency (CCR inherits the active channel's
                // parameters) and a CCR power-step SNR sweep needs no frequency knowledge.
                Console.WriteLine("  keying via NinoTNC data PTT while in CCR...");
                await using var tnc = Packet.Kiss.NinoTnc.NinoTncSerialPort.Open(kissTxPort);
                float before = await observer.ReadRssiDbmAsync();
                await tnc.SendFrameAsync(new byte[60]);
                float peak = before;
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(60);
                    peak = Math.Max(peak, await observer.ReadRssiDbmAsync());
                }
                Console.WriteLine($"  observer RSSI: idle {before:0.0} dBm, peak during CCR-keyed TX {peak:0.0} dBm");
                Console.WriteLine(peak > before + 10
                    ? "  => data PTT DOES key the radio in CCR mode (power-step sweep is viable)"
                    : "  => no carrier observed; PTT appears suppressed in CCR mode");
            }

            Console.WriteLine("exiting CCR (soft reset — radio reboots)...");
            await ccr.ExitAsync();
            await Task.Delay(6000);

            // After the reset the radio should be back in Command mode at power-up state.
            var id = await radio.QueryIdentityAsync();
            Console.WriteLine($"back in Command mode after reset: {id.ProductName} s/n {id.SerialNumber}");
            float rssi = await radio.ReadRssiDbmAsync();
            Console.WriteLine($"post-reset RSSI: {rssi:0.0} dBm");
            return 0;
        }
        finally
        {
            if (observer is not null)
            {
                await observer.DisposeAsync();
            }
        }
    }
}
