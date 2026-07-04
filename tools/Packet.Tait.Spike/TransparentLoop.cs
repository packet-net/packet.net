using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Radio.Tait;

namespace Packet.Tait.Spike;

/// <summary>
/// Hardware round-trip for <see cref="TaitTransparentTransport"/>: open two Tait radios as
/// TNC-less Transparent-mode AX.25 modems, push AX.25 UI frames radio-to-radio (both directions)
/// over the FFSK byte pipe, and confirm intact round-trip, KISS-SLIP boundary framing, populated
/// timing metadata (ReceivedAt + EstimatedAirtime; RSSI/DCD null), and a clean Transparent exit
/// (both radios back in Command mode). No NinoTNC involved — the radio's own modem carries it.
/// </summary>
internal static class TransparentLoop
{
    public static async Task<int> Run(string idA, string idB, int framesPerDirection)
    {
        Console.WriteLine("=== tait-transparent hardware round-trip ===");
        Console.WriteLine($"identifiers: A='{idA}'  B='{idB}'  frames/direction={framesPerDirection}");

        // Re-scan and print the current serial→port map (device paths renumber; never trust
        // remembered /dev/ttyUSB* numbers — bind by CCDI serial).
        Console.WriteLine("scanning for Tait radios (CCDI MODEL/serial probe)...");
        var discovered = new List<TaitDiscoveredRadio>();
        await foreach (var found in TaitRadioPortDiscovery.DiscoverAsync())
        {
            discovered.Add(found);
            Console.WriteLine(
                $"  {found.Port} @ {found.BaudRate}: {found.Identity.ProductName} " +
                $"s/n {found.Identity.SerialNumber} (CCDI {found.Identity.CcdiVersion})");
        }

        string deviceA = Resolve(idA, discovered);
        string deviceB = Resolve(idB, discovered);
        Console.WriteLine($"resolved A → {deviceA}   B → {deviceB}");
        if (deviceA == deviceB)
        {
            Console.WriteLine("ERROR: both identifiers resolved to the same port.");
            return 2;
        }

        var options = new TaitTransparentTransportOptions
        {
            CommandBaud = 28800,
            TransparentBaud = 28800, // same on this rig → no re-clock (driver handles differ, though)
            FfskBaud = 2400,
            LeadIn = TimeSpan.FromMilliseconds(100),
        };

        Console.WriteLine("entering Transparent mode on both radios (t+0)...");
        await using var a = await TaitTransparentTransport.OpenAsync(deviceA, options);
        await using var b = await TaitTransparentTransport.OpenAsync(deviceB, options);
        Console.WriteLine("both radios in Transparent mode — FFSK byte pipe open.");

        using var pumpCts = new CancellationTokenSource();
        var inA = Pump(a, pumpCts.Token);
        var inB = Pump(b, pumpCts.Token);

        int ok = 0;
        int total = 0;

        Console.WriteLine();
        Console.WriteLine("--- direction A → B ---");
        for (int i = 1; i <= framesPerDirection; i++)
        {
            total++;
            if (await OneWayAsync(a, inB, new Callsign("PDNTXA", 1), new Callsign("PDNRXB", 2), i))
            {
                ok++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- direction B → A ---");
        for (int i = 1; i <= framesPerDirection; i++)
        {
            total++;
            if (await OneWayAsync(b, inA, new Callsign("PDNTXB", 1), new Callsign("PDNRXA", 2), i))
            {
                ok++;
            }
        }

        await pumpCts.CancelAsync();

        Console.WriteLine();
        Console.WriteLine($"round-trip result: {ok}/{total} frames intact both directions");

        // Clean exit: dispose escapes Transparent (+++), restoring Command mode.
        Console.WriteLine("exiting Transparent mode on both radios (+++, ~4 s each)...");
        await a.DisposeAsync();
        await b.DisposeAsync();

        // Prove both are back in Command mode: a radio still in Transparent cannot answer CCDI.
        bool aCmd = await VerifyCommandModeAsync(deviceA);
        bool bCmd = await VerifyCommandModeAsync(deviceB);
        Console.WriteLine($"post-exit CCDI check: A={(aCmd ? "Command OK" : "NOT responding")}, " +
            $"B={(bCmd ? "Command OK" : "NOT responding")}");

        bool pass = ok == total && aCmd && bCmd;
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }

    // Drain a transport's inbound stream into a channel on a background task, so the send loop can
    // await the next frame with a timeout without ever racing two MoveNextAsync on one enumerator.
    private static ChannelReader<Ax25InboundFrame> Pump(TaitTransparentTransport transport, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Ax25InboundFrame>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in transport.ReceiveAsync(ct))
                {
                    channel.Writer.TryWrite(frame);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);
        return channel.Reader;
    }

    // Send one UI frame on `tx`, await it on the other radio's inbound channel, and report the
    // match + the TX-side and RX-side timing metadata.
    private static async Task<bool> OneWayAsync(
        TaitTransparentTransport tx,
        ChannelReader<Ax25InboundFrame> rx,
        Callsign source,
        Callsign destination,
        int seq)
    {
        var info = Encoding.ASCII.GetBytes($"transparent frame {seq} " + new string('x', 8 * (seq % 4)));
        var ui = Ax25Frame.Ui(destination: destination, source: source, info: info);
        byte[] sent = ui.ToBytes();

        TaitTransparentTxTiming? txTiming = null;
        void OnTx(object? _, TaitTransparentTxTiming t) => txTiming = t;
        tx.TxTiming += OnTx;
        try
        {
            await tx.SendAsync(sent);
        }
        finally
        {
            tx.TxTiming -= OnTx;
        }

        // Await the frame on the far side (generous timeout for FFSK airtime + fragmentation).
        Ax25InboundFrame frame;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            frame = await rx.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  #{seq}: sent {sent.Length}B — NO frame received (timeout)");
            return false;
        }

        bool match = frame.Ax25.Span.SequenceEqual(sent);
        bool parsed = Ax25Frame.TryParse(frame.Ax25.Span, out _);

        string txPart = txTiming is { } tt
            ? $"tx[queued {Time(tt.Queued)} onair {Time(tt.OnAirStart)}→{Time(tt.OnAirEnd)} " +
              $"air {Ms(tt.EstimatedAirtime)} {tt.OnAirByteCount}B]"
            : "tx[?]";

        var meta = frame.Radio;
        var airtime = meta?.EstimatedAirtime;
        string onAirStart = airtime is { } air ? Time(frame.ReceivedAt - air) : "?";
        string rssi = meta?.RssiDbm is { } r ? r.ToString("0.0", CultureInfo.InvariantCulture) : "null";
        string rxPart = $"rx[recvAt {Time(frame.ReceivedAt)} air {Ms(airtime)} onairStart≈{onAirStart} rssi={rssi}]";

        Console.WriteLine($"  #{seq}: {sent.Length}B  match={match}  parses={parsed}  {txPart}  {rxPart}");
        return match && parsed;
    }

    private static async Task<bool> VerifyCommandModeAsync(string device)
    {
        try
        {
            await using var radio = TaitCcdiRadio.Open(
                device, 28800, new TaitCcdiRadioOptions { KeepAliveInterval = null });
            var id = await radio.QueryIdentityAsync();
            return !string.IsNullOrWhiteSpace(id.SerialNumber);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or TaitCcdiException)
        {
            return false;
        }
    }

    private static string Resolve(string identifier, List<TaitDiscoveredRadio> discovered)
    {
        if (identifier.StartsWith("/dev", StringComparison.Ordinal) ||
            identifier.Contains("COM", StringComparison.OrdinalIgnoreCase))
        {
            return identifier;
        }
        foreach (var d in discovered)
        {
            if (string.Equals(d.Identity.SerialNumber?.Trim(), identifier.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return d.Port;
            }
        }
        throw new InvalidOperationException(
            $"no radio with CCDI serial '{identifier}' among {discovered.Count} discovered.");
    }

    private static string Time(DateTimeOffset t) => t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan? t) =>
        t is { } v ? v.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms" : "n/a";
}
