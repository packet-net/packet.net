using System.Threading.Channels;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Long-running APRS-IS collector. Connects, subscribes to the filter,
/// persists every TNC2 line to daily-rotated SQLite files. Reconnect on
/// disconnect with exponential backoff; graceful shutdown on SIGINT/SIGTERM.
/// </summary>
public static class CollectMode
{
    static long _reconnectCount;

    public static async Task<int> RunAsync(Options opts)
    {
        Directory.CreateDirectory(opts.OutDir);
        string clientId = $"packet-aprs-collector-{Environment.MachineName}-{Random.Shared.Next():x}";

        Console.Error.WriteLine($"# collect: {opts.Host}:{opts.Port}  filter={opts.Filter}");
        Console.Error.WriteLine($"# callsign: {opts.Callsign}  out-dir: {opts.OutDir}  client-id: {clientId}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("# ^C — graceful shutdown");
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.Error.WriteLine("# SIGTERM — graceful shutdown");
            cts.Cancel();
        };

        var channel = Channel.CreateUnbounded<CapturedLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        await using var sink = new SqliteSink(opts.OutDir, opts.FilenamePrefix, clientId, opts.Filter);

        // Consumer drains channel → SQLite, periodically flushes the in-flight
        // transaction so a long-stalled batch still gets committed.
        var consumer = Task.Run(async () =>
        {
            var lastLog = DateTime.UtcNow;
            try
            {
                await foreach (var line in channel.Reader.ReadAllAsync(cts.Token))
                {
                    await sink.WriteAsync(line, cts.Token);

                    if ((DateTime.UtcNow - lastLog).TotalSeconds >= 30)
                    {
                        await sink.FlushAsync(cts.Token);
                        Console.Error.WriteLine($"# heartbeat: {sink.TotalLines} lines persisted");
                        lastLog = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                try { await sink.FlushAsync(CancellationToken.None); } catch { }
            }
        }, CancellationToken.None);

        // Reconnect loop with exponential backoff.
        int backoffMs = 1000;
        while (!cts.IsCancellationRequested)
        {
            await using var client = new AprsIsClient();
            try
            {
                Console.Error.WriteLine("# connecting...");
                await client.ConnectAsync(opts.Host, opts.Port, opts.Callsign, -1, opts.Filter, cts.Token);
                Console.Error.WriteLine("# connected");
                backoffMs = 1000;

                await foreach (var rawLine in client.ReadLinesAsync(cts.Token))
                {
                    if (rawLine.Length == 0 || rawLine[0] == '#')
                    {
                        // Server keepalive / comment - track but don't persist.
                        continue;
                    }

                    var captured = ParseLine(rawLine);
                    await channel.Writer.WriteAsync(captured, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _reconnectCount);
                Console.Error.WriteLine($"# connection lost ({ex.GetType().Name}: {ex.Message}); reconnect #{_reconnectCount}");
            }

            if (cts.IsCancellationRequested)
            {
                break;
            }

            Console.Error.WriteLine($"# reconnecting in {backoffMs} ms...");
            try { await Task.Delay(backoffMs, cts.Token); } catch { break; }
            backoffMs = Math.Min(backoffMs * 2, 60_000);
        }

        Console.Error.WriteLine("# shutting down");
        channel.Writer.Complete();
        await consumer;
        Console.Error.WriteLine($"# total lines persisted: {sink.TotalLines}");
        return 0;
    }

    static CapturedLine ParseLine(string raw)
    {
        long ts = SqliteSink.NowUs();
        if (!Tnc2Parser.TryParse(raw, out var parsed))
        {
            return new CapturedLine(ts, null, null, null, 0, Array.Empty<byte>(), raw);
        }

        string? digiPath = parsed.Digipeaters.Count == 0
            ? null
            : string.Join(',', parsed.Digipeaters.Select(d => d.HasBeenRepeated ? $"{d.Callsign}*" : d.Callsign));

        return new CapturedLine(
            ts,
            parsed.Source,
            parsed.Destination,
            digiPath,
            parsed.Digipeaters.Count,
            parsed.Info.ToArray(),
            raw);
    }
}
