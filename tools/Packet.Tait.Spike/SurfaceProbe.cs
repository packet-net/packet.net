using System.Text;
using Packet.Kiss;
using Packet.Kiss.Serial;
using Packet.Radio.Tait;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Tait.Spike;

/// <summary>
/// Bench probes for the documented-but-unmodelled CCDI surface (backlog #5): extended SDM
/// (SFI 04/05), binary SDM (GFI 1), the legacy 's' SEND_SDM, the undocumented FUNCTION 0/6,
/// and the NinoTNC KAUP8R GETSERNO query. Everything here observes with
/// <see cref="TaitCcdiRadio.TransactRawAsync"/> so the wire behaviour can be discovered
/// BEFORE the driver surface is finalised.
/// </summary>
internal static class SurfaceProbe
{
    /// <summary>Extended (SFI 04) + binary (GFI 1) + legacy 's' SDM probes, radio-to-radio.</summary>
    public static async Task<int> RunSdm(string fromPort, string toPort, string destId)
    {
        await using var sender = TaitCcdiRadio.Open(fromPort);
        await using var receiver = TaitCcdiRadio.Open(toPort);

        var receiptSeen = new SemaphoreSlim(0);
        sender.SdmDeliveryReceipt += (_, r) =>
        {
            Console.WriteLine($"  sender receipt: acknowledged={r.Acknowledged}");
            receiptSeen.Release();
        };
        sender.MessageReceived += (_, m) => Console.WriteLine($"  sender msg: {Render(m)}");

        var arrivals = new SemaphoreSlim(0);
        receiver.ProgressReceived += (_, p) =>
        {
            Console.WriteLine($"  rx progress: {p.Type} '{p.Para}'");
            if (p.Type == CcdiProgressType.FfskDataReceived)
            {
                arrivals.Release();
            }
        };
        receiver.RingReceived += (_, r) =>
        {
            Console.WriteLine($"  rx RING: cat={r.Category} type={r.RingType} status={r.Status} caller='{r.CallerId}'");
            arrivals.Release();
        };
        receiver.ErrorReceived += (_, e) => Console.WriteLine($"  rx ERROR: {e.Category}/{e.ErrorNumber:X2} {e.Describe()}");
        await sender.SetProgressMessagesAsync(true);
        await receiver.SetProgressMessagesAsync(true);

        await DrainStale(receiver);

        // ---- probe 1: extended SDM, single 'a' command, SFI 04, 100 chars ----
        string message100 = BuildAscii(int.TryParse(Environment.GetEnvironmentVariable("SDM_EXT_LEN"), out int l) ? l : 100);
        Console.WriteLine($"\n[1] extended SDM: single 'a' GFI 2 SFI 04, {message100.Length} chars → {destId}");
        try
        {
            await SendRaw(sender, 'a', $"05204{destId}{message100}");
            await ObserveParts(receiver, arrivals, receiptSeen, TimeSpan.FromSeconds(25));
        }
        catch (TaitCcdiException ex)
        {
            Console.WriteLine($"  SENDER REJECTED: {ex.Message}");
            Console.WriteLine("\n[1b] fall back: host-side multipart, 32-char parts, SFI 04 then 05");
            await Task.Delay(2500);
            for (int i = 0; i < message100.Length; i += 32)
            {
                string part = message100.Substring(i, Math.Min(32, message100.Length - i));
                string sfi = i == 0 ? "04" : "05";
                Console.WriteLine($"  part sfi={sfi} len={part.Length}");
                try
                {
                    await SendRaw(sender, 'a', $"052{sfi}{destId}{part}");
                }
                catch (TaitCcdiException ex2)
                {
                    Console.WriteLine($"  part REJECTED: {ex2.Message}");
                    break;
                }
                await WaitAndDrain(receiver, arrivals, TimeSpan.FromSeconds(8));
                await Task.Delay(2500); // ack-wedge guard: let the receiver's auto-ack clear
            }
            if (await receiptSeen.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("  (receipt seen)");
            }
        }

        await Task.Delay(3000);
        await DrainStale(receiver);

        // ---- probe 2: binary SDM, GFI 1 SFI 00, short payload ----
        Console.WriteLine($"\n[2] binary SDM: 'a' GFI 1 SFI 00, bytes 01 42 7F FE → {destId}");
        string binary = "\x01" + "B" + "\x7F" + "\xFE";
        try
        {
            await SendRaw(sender, 'a', $"05100{destId}{binary}");
            await ObserveParts(receiver, arrivals, receiptSeen, TimeSpan.FromSeconds(12));
        }
        catch (TaitCcdiException ex)
        {
            Console.WriteLine($"  SENDER REJECTED: {ex.Message}");
        }

        await Task.Delay(3000);
        await DrainStale(receiver);

        // ---- probe 2b: binary EXTENDED SDM, GFI 1 SFI 04, 40 bytes ----
        Console.WriteLine($"\n[2b] binary extended SDM: 'a' GFI 1 SFI 04, 40 bytes → {destId}");
        string binary40 = string.Concat(Enumerable.Range(0, 40).Select(i => ((char)(0x20 + (i * 7) % 0x5F)).ToString()));
        try
        {
            await SendRaw(sender, 'a', $"05104{destId}{binary40}");
            await ObserveParts(receiver, arrivals, receiptSeen, TimeSpan.FromSeconds(15));
        }
        catch (TaitCcdiException ex)
        {
            Console.WriteLine($"  SENDER REJECTED: {ex.Message}");
        }

        await Task.Delay(3000);
        await DrainStale(receiver);

        // ---- probe 3: legacy SEND_SDM 's' ----
        Console.WriteLine($"\n[3] legacy SEND_SDM: 's' → {destId} \"LEGACY-S\"");
        try
        {
            await SendRaw(sender, 's', $"05{destId}LEGACY-S");
            await ObserveParts(receiver, arrivals, receiptSeen, TimeSpan.FromSeconds(12));
        }
        catch (TaitCcdiException ex)
        {
            Console.WriteLine($"  SENDER REJECTED: {ex.Message}");
        }

        Console.WriteLine("\ndone");
        return 0;
    }

    /// <summary>One-shot FUNCTION 0/6 probe: f0601, capture, f0600, capture — revert to off.</summary>
    public static async Task<int> RunF06(string ccdiPort)
    {
        await using var radio = TaitCcdiRadio.Open(ccdiPort);
        radio.MessageReceived += (_, m) => Console.WriteLine($"  msg: {Render(m)}");
        await radio.SetProgressMessagesAsync(true);

        foreach (string qualifier in (Environment.GetEnvironmentVariable("F06_PARAMS") ?? "061,060").Split(','))
        {
            Console.WriteLine($"\nsending f {qualifier} ({new CcdiFrame('f', qualifier).Encode()})");
            try
            {
                var results = await radio.TransactRawAsync('f', qualifier, TimeSpan.FromMilliseconds(500));
                Console.WriteLine($"  transaction OK, {results.Count} solicited responses");
                foreach (var m in results)
                {
                    Console.WriteLine($"  response: {Render(m)}");
                }
            }
            catch (TaitCcdiException ex)
            {
                Console.WriteLine($"  REJECTED: {ex.Message}");
            }
            await Task.Delay(1500); // let any late unsolicited output show up in the msg tap
        }
        Console.WriteLine("\ndone (sent f0600 last — feature left disabled)");
        return 0;
    }

    /// <summary>KAUP8R GETSERNO (KISS 0x0E) read-only probe against a NinoTNC.</summary>
    public static async Task<int> RunSerno(string tncPort)
    {
        await using var modem = KissSerialModem.Open(tncPort);
        modem.FrameReceived += (_, f) =>
        {
            byte raw = (byte)(((f.Port & 0x0F) << 4) | ((byte)f.Command & 0x0F));
            Console.WriteLine(
                $"  reply: cmdByte=0x{raw:X2} len={f.Payload.Length} " +
                $"hex=[{Convert.ToHexString(f.Payload)}] ascii=\"{ToPrintable(f.Payload)}\"");
        };
        Console.WriteLine("sending GETSERNO (KISS cmd 0x0E, payload 0x00)...");
        await modem.SendKissAsync((KissCommand)0x0E, new byte[] { 0x00 });
        await Task.Delay(3000);
        Console.WriteLine("done");
        return 0;
    }

    /// <summary>Driver-API validation: the same extended/binary sends through the shipped
    /// surface (SendExtendedSdmAsync / SendBinarySdmAsync / TaitSdmSideChannel extended flag).</summary>
    public static async Task<int> RunSdmDriver(string fromPort, string toPort, string destId)
    {
        await using var sender = TaitCcdiRadio.Open(fromPort);
        await using var receiver = TaitCcdiRadio.Open(toPort);
        await sender.SetProgressMessagesAsync(true);
        await receiver.SetProgressMessagesAsync(true);

        using var txChannel = new TaitSdmSideChannel(
            sender, new TaitSdmSideChannelOptions { EnableExtendedSdm = true });
        using var rxChannel = new TaitSdmSideChannel(receiver);

        var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        txChannel.DeliveryReceipt += (_, acked) => receipt.TrySetResult(acked);
        var arrivals = new SemaphoreSlim(0);
        rxChannel.DatagramArrived += (_, _) => arrivals.Release();

        // drain stale
        _ = await rxChannel.ReadBufferedAsync();

        string payload = BuildAscii(100);
        Console.WriteLine($"[driver-1] side-channel extended send, {payload.Length} chars (budget {txChannel.MaxPayloadLength})");
        await txChannel.SendAsync(destId, payload);
        string? got = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && got is null)
        {
            if (await arrivals.WaitAsync(TimeSpan.FromMilliseconds(500)))
            {
                await Task.Delay(150);
                got = await rxChannel.ReadBufferedAsync();
            }
        }
        bool acked = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"  received: {(got is null ? "NOTHING" : $"{got.Length} chars, match={got == payload}")}; receipt acked={acked}");
        bool ok1 = got == payload && acked;

        await Task.Delay(3000);
        _ = await rxChannel.ReadBufferedAsync();

        Console.WriteLine("[driver-2] SendBinarySdmAsync, 4 bytes 01 42 7F FE");
        var receipt2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReceipt2(object? s, TaitSdmReceipt r) => receipt2.TrySetResult(r.Acknowledged);
        sender.SdmDeliveryReceipt += OnReceipt2;
        await sender.SendBinarySdmAsync(destId, new byte[] { 0x01, 0x42, 0x7F, 0xFE });
        string? gotBin = null;
        sw.Restart();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && gotBin is null)
        {
            if (await arrivals.WaitAsync(TimeSpan.FromMilliseconds(500)))
            {
                await Task.Delay(150);
                gotBin = await rxChannel.ReadBufferedAsync();
            }
        }
        bool acked2 = await receipt2.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sender.SdmDeliveryReceipt -= OnReceipt2;
        string expected = "\u0001B\u007F\u00FE";
        Console.WriteLine($"  received: {(gotBin is null ? "NOTHING" : ToPrintable(gotBin))}; match={gotBin == expected}; receipt acked={acked2}");
        bool ok2 = gotBin == expected && acked2;

        Console.WriteLine(ok1 && ok2 ? "\nDRIVER VALIDATION PASS" : "\nDRIVER VALIDATION FAIL");
        return ok1 && ok2 ? 0 : 1;
    }

    /// <summary>
    /// AUTHORIZED live CCR-over-SDM experiment (Tom, 2026-07-03): put the TARGET radio into
    /// CCR mode via its local CCDI, send it SAFE CCR commands (volume J104, pulse QP - never
    /// R/T frequency, never S Selcall-encode, never P power) over the air from the sender via
    /// GFI 2 / SFI 03 SDMs, observe where responses route, then exit CCR (soft reset) and
    /// verify full recovery (identity, RSSI, normal SDM with receipts both ways).
    /// </summary>
    public static async Task<int> RunCcrOverSdm(string fromPort, string targetPort, string destId, string senderId)
    {
#pragma warning disable PKTTAIT001 // experimental CCR-over-SDM - this IS the authorized bench validation
        await using var sender = TaitCcdiRadio.Open(fromPort);
        await using var target = TaitCcdiRadio.Open(targetPort);
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        void Log(string who, string what) =>
            Console.WriteLine($"  [{t0.Elapsed.TotalSeconds,7:0.000}] {who}: {what}");

        sender.MessageReceived += (_, m) => Log("sender", Render(m));
        target.MessageReceived += (_, m) => Log("TARGET", Render(m));
        var receipts = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var receiptSeen = new SemaphoreSlim(0);
        sender.SdmDeliveryReceipt += (_, r) => { receipts.Enqueue(r.Acknowledged); receiptSeen.Release(); };
        await sender.SetProgressMessagesAsync(true);
        await target.SetProgressMessagesAsync(true);

        Console.WriteLine("\n[c1] target radio -> CCR mode (local CCDI)");
        var ccr = await target.EnterCcrModeAsync();
        Log("TARGET", $"in CCR mode (driver mode = {target.Mode})");
        bool pulse = await ccr.PulseAsync();
        Log("TARGET", $"local pulse answered, minimum-config={pulse}");
        await Task.Delay(2000);

        Console.WriteLine("\n[c2] over-air CCR volume command J104 (wire J03104BE) inside an SDM");
        await sender.UnsafeSendCcrOverSdmAsync(destId, new CcdiFrame('J', "104"));
        bool r1 = await receiptSeen.WaitAsync(TimeSpan.FromSeconds(12));
        Log("sender", r1 && receipts.TryDequeue(out bool a1)
            ? $"delivery receipt: acknowledged={a1}"
            : "NO delivery receipt within 12 s");
        await Task.Delay(3000);

        Console.WriteLine("\n[c3] over-air CCR pulse QP (wire Q01PFE) inside an SDM");
        await sender.UnsafeSendCcrOverSdmAsync(destId, new CcdiFrame('Q', "P"));
        bool r2 = await receiptSeen.WaitAsync(TimeSpan.FromSeconds(12));
        Log("sender", r2 && receipts.TryDequeue(out bool a2)
            ? $"delivery receipt: acknowledged={a2}"
            : "NO delivery receipt within 12 s");
        await Task.Delay(3000);

        Console.WriteLine("\n[c4] exit CCR (soft reset, ~6 s) and verify recovery");
        await ccr.ExitAsync();
        await Task.Delay(8000);
        try
        {
            var identity = await target.QueryIdentityAsync();
            Log("TARGET", $"identity OK: {identity.ProductName} s/n {identity.SerialNumber}");
            float rssi = await target.ReadRssiDbmAsync();
            Log("TARGET", $"RSSI OK: {rssi:0.0} dBm");
        }
        catch (Exception ex)
        {
            Log("TARGET", $"RECOVERY FAILED: {ex.Message}");
            return 1;
        }

        Console.WriteLine("\n[c5] normal SDM sanity both ways with receipts");
        _ = await SafeRead(target);
        await sender.SendSdmAsync(destId, "POST-CCR A->B");
        bool r3 = await receiptSeen.WaitAsync(TimeSpan.FromSeconds(12));
        bool a3 = r3 && receipts.TryDequeue(out bool v3) && v3;
        string? got = await SafeRead(target);
        Log("sender", $"A->B: buffered=\"{got}\" receipt-acked={a3}");

        await Task.Delay(3000); // ack-wedge guard before the target transmits
        var receiptsB = new SemaphoreSlim(0);
        bool ackB = false;
        target.SdmDeliveryReceipt += (_, r) => { ackB = r.Acknowledged; receiptsB.Release(); };
        _ = await SafeRead(sender);
        await target.SendSdmAsync(senderId, "POST-CCR B->A");
        bool r4 = await receiptsB.WaitAsync(TimeSpan.FromSeconds(12));
        string? gotA = await SafeRead(sender);
        Log("TARGET", $"B->A: buffered-at-A=\"{gotA}\" receipt-acked={r4 && ackB}");

        bool pass = a3 && got == "POST-CCR A->B" && r4 && ackB && gotA == "POST-CCR B->A";
        Console.WriteLine(pass ? "\nRECOVERY VERIFIED - rig healthy" : "\nCHECK RIG - post-CCR sanity incomplete");
        return pass ? 0 : 1;
#pragma warning restore PKTTAIT001
    }

    private static async Task SendRaw(TaitCcdiRadio radio, char ident, string parameters)
    {
        Console.WriteLine($"  tx: {new CcdiFrame(ident, parameters).Encode()}");
        await radio.TransactRawAsync(ident, parameters, TimeSpan.FromMilliseconds(200));
    }

    private static async Task ObserveParts(
        TaitCcdiRadio receiver, SemaphoreSlim arrivals, SemaphoreSlim receiptSeen, TimeSpan window)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < window)
        {
            if (await arrivals.WaitAsync(TimeSpan.FromMilliseconds(500)))
            {
                await Task.Delay(150); // let the buffer settle after the event
                string? data = await SafeRead(receiver);
                Console.WriteLine(data is null
                    ? "  rx buffer: (empty)"
                    : $"  rx buffer ({data.Length} ch): \"{ToPrintable(data)}\"");
            }
            if (receiptSeen.CurrentCount > 0)
            {
                await receiptSeen.WaitAsync();
                // keep observing a little longer for late parts
                window = sw.Elapsed + TimeSpan.FromSeconds(4);
            }
        }
    }

    private static async Task WaitAndDrain(TaitCcdiRadio receiver, SemaphoreSlim arrivals, TimeSpan timeout)
    {
        if (await arrivals.WaitAsync(timeout))
        {
            await Task.Delay(150);
            string? data = await SafeRead(receiver);
            Console.WriteLine(data is null
                ? "  rx buffer: (empty)"
                : $"  rx buffer ({data.Length} ch): \"{ToPrintable(data)}\"");
        }
        else
        {
            Console.WriteLine("  (no arrival within window)");
        }
    }

    private static async Task DrainStale(TaitCcdiRadio receiver)
    {
        string? stale = await SafeRead(receiver);
        if (stale is not null)
        {
            Console.WriteLine($"  cleared stale SDM ({stale.Length} ch): \"{ToPrintable(stale)}\"");
        }
    }

    private static async Task<string?> SafeRead(TaitCcdiRadio radio)
    {
        try
        {
            return await radio.ReadBufferedSdmAsync();
        }
        catch (TaitCcdiException ex)
        {
            Console.WriteLine($"  q1 error: {ex.Message}");
            return null;
        }
    }

    private static string BuildAscii(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append((char)('A' + (i % 26)));
        }
        // stamp decade markers so part boundaries are recognisable: position every 10
        for (int i = 0; i < length; i += 10)
        {
            sb[i] = (char)('0' + (i / 10) % 10);
        }
        return sb.ToString();
    }

    private static string Render(CcdiMessage m) => m switch
    {
        CcdiSdmMessage s => $"SDM({s.Data.Length}ch) \"{ToPrintable(s.Data)}\"",
        CcdiProgressMessage p => $"PROGRESS {p.Type} '{p.Para}'",
        CcdiErrorMessage e => $"ERROR {e.Category}/{e.ErrorNumber:X2} {e.Describe()}",
        CcdiUnknownMessage u => $"UNKNOWN '{u.UnknownIdent}' \"{ToPrintable(u.Parameters)}\"",
        _ => m.ToString() ?? "",
    };

    private static string ToPrintable(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"<{(int)c:X2}>"));

    private static string ToPrintable(byte[] bytes) =>
        string.Concat(bytes.Select(b => b is >= 0x20 and <= 0x7E ? ((char)b).ToString() : $"<{b:X2}>"));
}
