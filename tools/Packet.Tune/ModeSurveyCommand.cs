using System.Diagnostics;
using System.Globalization;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Radio.Tait.Ccdi;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>mode-survey</c>: for each radio channel and each IL2P+CRC NinoTNC mode,
/// fire N short UI probe frames per direction through the RF path and record
/// decode success, mean latency, receiver-side Tait RSSI, and the receiver
/// TNC's IL2P decode-counter deltas (GETALL). Emits a Markdown table (and
/// optionally JSON). The rig is ALWAYS left on channel 0 / mode 6 at the end.
/// </summary>
internal static class ModeSurveyCommand
{
    private const byte RestingMode = 6;
    private const int RestingChannel = 0;
    private static readonly TimeSpan RssiPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan InterRoundGap = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleTxTimeout = TimeSpan.FromSeconds(15);

    private static bool logProgressMessages;

    public static async Task<int> Run(string tncAPort, string tncBPort, string ccdiAPort, string ccdiBPort, string[] rest)
    {
        var (channels, rounds, jsonPath, callsign, error) = ParseFlags(rest);
        if (error is not null)
        {
            Console.WriteLine(error);
            return 2;
        }

        var source = Callsign.Parse(callsign);
        var modes = ModeSurvey.SelectIl2pCrcModes();

        Console.WriteLine($"mode-survey — TNC A {tncAPort} / TNC B {tncBPort}, CCDI A {ccdiAPort} / CCDI B {ccdiBPort}");
        Console.WriteLine($"  channels: {string.Join(",", channels)}; rounds/direction: {rounds}");
        Console.WriteLine($"  modes ({ModeSurvey.Il2pCrcNameFragment} only): " +
                          string.Join(", ", modes.Select(m => m.Mode.ToString(CultureInfo.InvariantCulture))));

        await using var tncA = NinoTncSerialPort.Open(tncAPort);
        await using var tncB = NinoTncSerialPort.Open(tncBPort);
        await using var radioA = TaitCcdiRadio.Open(ccdiAPort);
        await using var radioB = TaitCcdiRadio.Open(ccdiBPort);

        Console.WriteLine($"  TNC A firmware {await tncA.GetVersionAsync()}, TNC B firmware {await tncB.GetVersionAsync()}");

        // Status beacons off — a 60 s beacon keying mid-cell would pollute both
        // the latency figures and the receiver's IL2P counters.
        await tncA.SetBeaconIntervalAsync(0);
        await tncB.SetBeaconIntervalAsync(0);

        // PROGRESS output on (per-session): DCD busy-gates the RSSI polls, and
        // the channel-switch behaviour notes want the unsolicited traffic visible.
        await radioA.SetProgressMessagesAsync(true);
        await radioB.SetProgressMessagesAsync(true);
        radioA.ProgressReceived += (_, m) => LogProgress("radio A", m);
        radioB.ProgressReceived += (_, m) => LogProgress("radio B", m);

        var cells = new List<ModeSurveyCell>();
        try
        {
            foreach (int channel in channels)
            {
                if (!await SwitchBothRadiosAsync(radioA, radioB, channel))
                {
                    Console.WriteLine($"  channel {channel}: radios did not both verify — recovered to channel {RestingChannel}, aborting survey");
                    return 1;
                }

                foreach (var mode in modes)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ── ch {channel} / mode {mode.Mode} ({mode.Name}) ──");
                    // A mode that didn't take poisons the cell: both ends score zero and the survey
                    // reports it as "this mode doesn't work here" when the TNC was never in it
                    // (#633). Skip the cell rather than publish a fabricated zero.
                    bool aSet = await SetModeWithSettleAsync(tncA, "TNC A", mode.Mode, source);
                    bool bSet = await SetModeWithSettleAsync(tncB, "TNC B", mode.Mode, source);
                    if (!aSet || !bSet)
                    {
                        Console.WriteLine($"    ch {channel} / mode {mode.Mode}: SKIPPED — the mode never took on " +
                                          (aSet ? "TNC B" : bSet ? "TNC A" : "either TNC") +
                                          "; results for this cell would measure the wrong mode");
                        continue;
                    }

                    cells.Add(await RunDirectionAsync("A→B", tncA, tncB, radioB, mode, channel, rounds, source));
                    cells.Add(await RunDirectionAsync("B→A", tncB, tncA, radioA, mode, channel, rounds, source));
                }
            }
        }
        finally
        {
            await RestoreRigAsync(tncA, tncB, radioA, radioB, source);
        }

        Console.WriteLine();
        Console.WriteLine(ModeSurvey.RenderMarkdown(cells));

        if (jsonPath is not null)
        {
            string json = ModeSurvey.RenderJson(cells);
            if (jsonPath.Length == 0)
            {
                Console.WriteLine(json);
            }
            else
            {
                await File.WriteAllTextAsync(jsonPath, json);
                Console.WriteLine($"  JSON written to {jsonPath}");
            }
        }
        return 0;
    }

    private static (List<int> Channels, int Rounds, string? JsonPath, string Callsign, string? Error) ParseFlags(string[] rest)
    {
        var channels = new List<int> { 0, 1 };
        int rounds = 5;
        string? jsonPath = null;
        string callsign = "N0CALL";
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--channels" when i + 1 < rest.Length:
                    channels = [];
                    foreach (string part in rest[++i].Split(','))
                    {
                        if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int ch))
                        {
                            return ([], 0, null, callsign, $"bad --channels value '{part}'");
                        }
                        channels.Add(ch);
                    }
                    break;
                case "--rounds" when i + 1 < rest.Length:
                    if (!int.TryParse(rest[++i], NumberStyles.None, CultureInfo.InvariantCulture, out rounds) || rounds is < 1 or > 50)
                    {
                        return ([], 0, null, callsign, "bad --rounds value (1–50)");
                    }
                    break;
                case "--callsign" when i + 1 < rest.Length:
                    callsign = rest[++i];
                    break;
                case "--json":
                    jsonPath = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? rest[++i]
                        : string.Empty;
                    break;
                default:
                    return ([], 0, null, callsign, $"unknown flag '{rest[i]}'");
            }
        }
        return (channels, rounds, jsonPath, callsign, null);
    }

    private static void LogProgress(string name, CcdiProgressMessage message)
    {
        if (logProgressMessages)
        {
            Console.WriteLine($"    [{name}] PROGRESS {message.Type} para '{message.Para}'");
        }
    }

    /// <summary>
    /// GO_TO_CHANNEL both radios and verify each with FUNCTION 0/5/2. If a
    /// radio verifies on the wrong channel after a retry, recover by
    /// commanding BOTH back to channel 0 so the rig is never left split.
    /// </summary>
    private static async Task<bool> SwitchBothRadiosAsync(TaitCcdiRadio radioA, TaitCcdiRadio radioB, int channel)
    {
        Console.WriteLine();
        Console.WriteLine($"  switching both radios to channel {channel} (unsolicited PROGRESS during switch shown below)");
        logProgressMessages = true;
        try
        {
            bool okA = await SwitchOneAsync(radioA, "radio A", channel);
            bool okB = await SwitchOneAsync(radioB, "radio B", channel);
            if (okA && okB)
            {
                return true;
            }

            Console.WriteLine($"  RECOVERY: commanding both radios to channel {RestingChannel}");
            bool recoveredA = await SwitchOneAsync(radioA, "radio A", RestingChannel);
            bool recoveredB = await SwitchOneAsync(radioB, "radio B", RestingChannel);
            Console.WriteLine($"  recovery verify: radio A {(recoveredA ? "OK" : "STILL WRONG")}, radio B {(recoveredB ? "OK" : "STILL WRONG")}");
            return false;
        }
        finally
        {
            logProgressMessages = false;
        }
    }

    private static async Task<bool> SwitchOneAsync(TaitCcdiRadio radio, string name, int channel)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await radio.GoToChannelAsync(channel);
            var report = await radio.QueryCurrentChannelAsync();
            bool ok = int.TryParse(report.ChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out int reported) &&
                      reported == channel;
            Console.WriteLine($"    {name}: GO_TO_CHANNEL {channel} → reports kind '{report.Kind}' channel '{report.ChannelId}'" +
                              (report.Zone is { } z ? $" zone {z}" : string.Empty) +
                              (ok ? string.Empty : " — MISMATCH" + (attempt == 1 ? ", retrying" : string.Empty)));
            if (ok)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// SETHW the mode (+16, RAM only) — verified through the driver's GETALL
    /// readback and retried until it takes (#633) — then transmit one throwaway
    /// settle frame, the NinoTNC applying a changed setting from the SECOND
    /// frame. Returns <c>false</c> when the mode never took, so the caller can
    /// skip the cell instead of measuring the wrong mode.
    /// </summary>
    private static async Task<bool> SetModeWithSettleAsync(NinoTncSerialPort tnc, string name, byte mode, Callsign source)
    {
        try
        {
            await tnc.SetModeAsync(mode, persistToFlash: false);
            Console.WriteLine($"    {name}: SETHW {mode}+16 → GETALL confirms mode {mode} running");
        }
        catch (NinoTncModeNotAppliedException ex)
        {
            Console.WriteLine($"    {name}: SETHW {mode}+16 DID NOT TAKE — {ex.Message}");
            return false;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"    {name}: SETHW {mode}+16 sent; GETALL verify timed out — treating the mode as unset");
            return false;
        }

        byte[] settle = ModeSurvey.BuildSettleFrame(source, mode).ToBytes();
        try
        {
            await tnc.SendFrameWithAckAsync(settle, SettleTxTimeout);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"    {name}: settle frame TX-completion not echoed within {SettleTxTimeout.TotalSeconds:0} s (continuing)");
        }
        return true;
    }

    /// <summary>
    /// One survey cell: N sequential probe frames sender→receiver. Each round
    /// waits for the receiver's decode (up to the mode-scaled deadline, or
    /// TX-completion + grace once the sender has finished keying) while
    /// polling the receiver radio's RSSI, busy-tagged by its DCD.
    /// </summary>
    private static async Task<ModeSurveyCell> RunDirectionAsync(
        string direction,
        NinoTncSerialPort sender,
        NinoTncSerialPort receiver,
        TaitCcdiRadio receiverRadio,
        NinoTncMode mode,
        int channel,
        int rounds,
        Callsign source)
    {
        NinoTncStatusFrame? before = await TryGetAllAsync(receiver);

        var latencies = new List<double>();
        var rssiSamples = new List<(double Dbm, bool Busy)>();
        int successes = 0;
        TimeSpan receiveTimeout = ModeSurvey.ReceiveTimeout(mode);

        for (int round = 1; round <= rounds; round++)
        {
            var received = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = Stopwatch.StartNew();
            void OnFrame(object? _, KissFrame frame)
            {
                if (TuningBurst.IsBurstFrame(frame))
                {
                    received.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            receiver.FrameReceived += OnFrame;
            using var pollCts = new CancellationTokenSource();
            Task pollTask = PollRssiAsync(receiverRadio, rssiSamples, pollCts.Token);
            try
            {
                byte[] wire = TuningBurst.BuildFrame(source, round, rounds).ToBytes();
                Task sendTask = sender.SendFrameWithAckAsync(wire, receiveTimeout);
                await Task.WhenAny(received.Task, DeadlineAsync(sendTask, receiveTimeout));
                if (received.Task.IsCompletedSuccessfully)
                {
                    successes++;
                    latencies.Add(await received.Task);
                }
                await ObserveAsync(sendTask);
            }
            finally
            {
                receiver.FrameReceived -= OnFrame;
                await pollCts.CancelAsync();
                await pollTask;
            }
            await Task.Delay(InterRoundGap);
        }

        NinoTncStatusFrame? after = await TryGetAllAsync(receiver);
        long? rxPackets = null;
        long? rxUncorrectable = null;
        if (before is not null && after is not null)
        {
            var delta = NinoTncStatusDelta.Between(before, after);
            rxPackets = delta.Il2pRxCorrectable;
            rxUncorrectable = delta.Il2pRxUncorrectable;
        }

        var cell = new ModeSurveyCell
        {
            Channel = channel,
            Mode = mode.Mode,
            ModeName = mode.Name,
            Direction = direction,
            Successes = successes,
            Attempts = rounds,
            MeanLatencyMs = ModeSurvey.MeanLatencyMs(latencies),
            ReceiverRssiDbm = ModeSurvey.PickRssi(rssiSamples),
            Il2pRxPacketsDelta = rxPackets,
            Il2pRxUncorrectableDelta = rxUncorrectable,
        };
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    {direction}: {cell.Successes}/{cell.Attempts}" +
            $", mean {(cell.MeanLatencyMs is { } l ? $"{l:0} ms" : "n/a")}" +
            $", RSSI {(cell.ReceiverRssiDbm is { } r ? $"{r:0.0} dBm" : "n/a")}" +
            $", IL2P rx Δ{Fmt(cell.Il2pRxPacketsDelta)} / uncorr Δ{Fmt(cell.Il2pRxUncorrectableDelta)}" +
            $" → {ModeSurvey.DescribeVerdict(cell.Verdict)}"));
        return cell;
    }

    /// <summary>Round deadline: once the sender's TX-completion echo arrives,
    /// only <see cref="ModeSurvey.PostTxGrace"/> more; if the echo never comes
    /// (or errors), the full mode-scaled window.</summary>
    private static async Task DeadlineAsync(Task sendTask, TimeSpan overall)
    {
        var overallDelay = Task.Delay(overall);
        var first = await Task.WhenAny(sendTask, overallDelay);
        if (first == sendTask && sendTask.IsCompletedSuccessfully)
        {
            await Task.Delay(ModeSurvey.PostTxGrace);
            return;
        }
        await overallDelay;
    }

    private static async Task ObserveAsync(Task sendTask)
    {
        try
        {
            await sendTask;
        }
        catch (TimeoutException)
        {
            // No TX-completion echo — the frame may or may not have keyed; the
            // receive verdict (not the echo) is what the survey scores.
        }
    }

    private static async Task PollRssiAsync(
        TaitCcdiRadio radio, List<(double Dbm, bool Busy)> samples, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool busy = radio.ChannelBusy == true;
                try
                {
                    samples.Add((await radio.ReadRssiDbmAsync(token), busy));
                }
                catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
                {
                    // Skip the sample; the poll cadence continues.
                }
                await Task.Delay(RssiPollInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Window over — normal completion.
        }
    }

    private static async Task<NinoTncStatusFrame?> TryGetAllAsync(NinoTncSerialPort tnc)
    {
        try
        {
            return await tnc.GetAllAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            Console.WriteLine("    GETALL got no reply — IL2P counter deltas unavailable for this cell");
            return null;
        }
    }

    /// <summary>Always leave the rig on channel 0 / mode 6 (radios first, then
    /// TNCs + settle frames), whatever happened above.</summary>
    private static async Task RestoreRigAsync(
        NinoTncSerialPort tncA, NinoTncSerialPort tncB, TaitCcdiRadio radioA, TaitCcdiRadio radioB, Callsign source)
    {
        Console.WriteLine();
        Console.WriteLine($"  restoring rig: both radios → channel {RestingChannel}, both TNCs → mode {RestingMode}");
        try
        {
            await SwitchBothRadiosAsync(radioA, radioB, RestingChannel);
        }
        catch (Exception ex) when (ex is TimeoutException or TaitCcdiException or IOException)
        {
            Console.WriteLine($"  RESTORE WARNING: channel restore failed ({ex.Message}) — check the radios manually");
        }
        try
        {
            await SetModeWithSettleAsync(tncA, "TNC A", RestingMode, source);
            await SetModeWithSettleAsync(tncB, "TNC B", RestingMode, source);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Console.WriteLine($"  RESTORE WARNING: mode restore failed ({ex.Message}) — check the TNCs manually");
        }
    }

    private static string Fmt(long? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "n/a";
}
