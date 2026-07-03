using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune;

/// <summary>
/// <c>deviation</c>: interactive TX-deviation tuning loop — LOCAL bench
/// flavour, both TNCs on this host.
/// </summary>
/// <remarks>
/// <para>
/// The tone comes FROM the end under tune: the REMOTE TNC is armed (a
/// <c>[TARPNstat</c> frame through its own serial), then each run the LOCAL
/// TNC transmits a CQBEEP-8 request; the armed remote answers with ~8 s of
/// 440 Hz tone, which the local TNC meters via GETRSSI (RX-audio RMS dB —
/// the received tone level tracks the remote's TX deviation). Between runs
/// the operator adjusts the remote's TX-DEV pot and presses Enter; a trend
/// table shows the level moving.
/// </para>
/// <para>
/// GETRSSI was removed in firmware 3.44, so this loop only runs against
/// 3.41 TNCs; on newer firmware it reports n/a and points at the remote
/// flavours — <c>deviation-sdm</c> / <c>deviation-remote</c> — which meter
/// by decode-rate + FEC/clip deltas + CCDI RSSI instead (see
/// <c>Packet.Tune.Core</c>'s <c>TuningSession</c>).
/// </para>
/// </remarks>
internal static class Deviation
{
    private const int BeepSeconds = 8;
    private const int BaselineSamples = 5;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(11);

    /// <summary>A tone sample must sit this far from the idle baseline to count as in-tone.</summary>
    private const float ToneThresholdDb = 6f;

    public static async Task<int> Run(string localTncPort, string remoteTncPort, string callsign)
    {
        Console.WriteLine($"deviation — local (trigger+meter) {localTncPort}, remote (under tune) {remoteTncPort}");
        Console.WriteLine("adjust the TX-DEV pot on the REMOTE TNC between runs");
        var source = Callsign.Parse(callsign);

        await using var local = NinoTncSerialPort.Open(localTncPort);
        await using var remote = NinoTncSerialPort.Open(remoteTncPort);

        Console.WriteLine($"  local firmware:  {await local.GetVersionAsync()}");
        Console.WriteLine($"  remote firmware: {await remote.GetVersionAsync()}");

        // Arm the remote's CQBEEP responder through its own serial port. The
        // arming frame also goes out over the air; arming is volatile, so it
        // happens every session.
        Console.WriteLine($"  arming remote CQBEEP responder ([TARPNstat from {source})...");
        await remote.ArmCqBeepResponderAsync(source);
        await Task.Delay(2000); // let the arming frame finish keying

        var baseline = new List<float>();
        try
        {
            for (int i = 0; i < BaselineSamples; i++)
            {
                baseline.Add(await local.GetRssiAsync(TimeSpan.FromSeconds(2)));
                await Task.Delay(200);
            }
        }
        catch (TimeoutException)
        {
            // GETRSSI was an undocumented 3.41 feature, removed in 3.44 —
            // this GETRSSI-metered local loop cannot run on 3.44 firmware.
            Console.WriteLine("  GETRSSI: n/a on this firmware (removed in 3.44; 3.41 only)");
            Console.WriteLine("  this local tone-metering loop needs GETRSSI — use deviation-sdm or");
            Console.WriteLine("  deviation-remote instead (they meter by decode-rate/FEC/clip/CCDI-RSSI)");
            return 1;
        }
        float idle = Median(baseline);
        Console.WriteLine($"  local idle RX-audio baseline: {Fmt(idle)} dB (n={BaselineSamples})");
        Console.WriteLine();

        var trend = new List<(int Run, float ToneDb, int Samples)>();
        for (int run = 1; ; run++)
        {
            Console.WriteLine($"run {run}: triggering CQBEEP-{BeepSeconds} from the local TNC...");
            await local.SendCqBeepRequestAsync(source, BeepSeconds);

            var samples = new List<(TimeSpan At, float Level)>();
            var started = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - started < SampleWindow)
            {
                float level = await local.GetRssiAsync();
                var at = DateTimeOffset.UtcNow - started;
                samples.Add((at, level));
                bool inTone = Math.Abs(level - idle) > ToneThresholdDb;
                Console.WriteLine($"    t={at.TotalSeconds,5:0.0}s  {Fmt(level),8} dB {(inTone ? " ← tone" : string.Empty)}");
                await Task.Delay(SampleInterval);
            }

            // Best-effort safety: ask the remote to stop transmitting. (On
            // firmware 3.41 STOPTX does not cut a CQBEEP tone short — bench
            // verified — but the sample window outlasts the tone anyway.)
            await remote.StopTxAsync();

            var tone = samples.Where(s => Math.Abs(s.Level - idle) > ToneThresholdDb).ToList();
            if (tone.Count == 0)
            {
                Console.WriteLine("  ! no tone window detected — is the remote armed and in range? (re-arm happens next run)");
                await remote.ArmCqBeepResponderAsync(source);
            }
            else
            {
                float toneDb = Median(tone.Select(s => s.Level).ToList());
                trend.Add((run, toneDb, tone.Count));
                Console.WriteLine($"  tone level: {Fmt(toneDb)} dB over {tone.Count} samples " +
                                  $"(~{tone.Count * SampleInterval.TotalSeconds:0.0}s of tone; idle {Fmt(idle)} dB)");
            }

            PrintTrend(trend);
            Console.WriteLine();
            Console.Write("adjust the remote TX-DEV pot, then [Enter] to re-run — or q[Enter] to finish: ");
            string? input = Console.ReadLine();
            if (input is null || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("final trend:");
        PrintTrend(trend);
        return trend.Count > 0 ? 0 : 1;
    }

    private static void PrintTrend(List<(int Run, float ToneDb, int Samples)> trend)
    {
        if (trend.Count == 0)
        {
            return;
        }
        Console.WriteLine("  run   tone dB   Δ prev   Δ first");
        for (int i = 0; i < trend.Count; i++)
        {
            string prev = i > 0 ? FmtSigned(trend[i].ToneDb - trend[i - 1].ToneDb) : "  —  ";
            string first = i > 0 ? FmtSigned(trend[i].ToneDb - trend[0].ToneDb) : "  —  ";
            Console.WriteLine($"  {trend[i].Run,3}   {Fmt(trend[i].ToneDb),7}   {prev,6}   {first,7}");
        }
    }

    private static float Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    private static string Fmt(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FmtSigned(float value) => value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
}
