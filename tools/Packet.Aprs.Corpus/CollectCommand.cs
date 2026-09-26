using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Receive-only APRS-IS capture. Logs in with passcode -1 (unverified, read-only: the server
/// never accepts anything from us), reads the full feed and hands every line's raw bytes to
/// <see cref="HourlyGzipWriter"/>. Reconnects with backoff across a list of servers, treats
/// 60 s of silence as a dead link (servers send a keepalive every 20 s), and stops writing
/// when free disk or the corpus size cap says so.
/// </summary>
internal static class CollectCommand
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SpaceCheckInterval = TimeSpan.FromMinutes(1);

    public static async Task<int> RunAsync(string[] args)
    {
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "aprs-corpus");
        string login = "M0LTE";
        var servers = new List<string>();
        double minFreeGb = 10;
        double maxGb = 25;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--out-dir": outDir = Next(); break;
                case "--login": login = Next(); break;
                case "--server": servers.Add(Next()); break;
                case "--min-free-gb": minFreeGb = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--max-gb": maxGb = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine($"unknown option {args[i]}");
                    return 2;
            }
        }

        if (servers.Count == 0)
        {
            servers.AddRange(["euro.aprs2.net:10152", "rotate.aprs2.net:10152", "noam.aprs2.net:10152"]);
        }

        Directory.CreateDirectory(outDir);

        // SIGINT/SIGTERM (Ctrl-C, systemctl stop) cancel the loop instead of killing the
        // process, so the open hour's file is closed and renamed rather than left .partial.
        using var cts = new CancellationTokenSource();
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            cts.Cancel();
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

        using var writer = new HourlyGzipWriter(outDir);
        var guard = new SpaceGuard(outDir, minFreeGb, maxGb);
        var backoff = TimeSpan.FromSeconds(5);
        int serverIndex = 0;

        Log($"collect: out-dir {outDir}, login {login} (pass -1), min free {minFreeGb} GB, cap {maxGb} GB");

        while (!cts.IsCancellationRequested)
        {
            if (!guard.MayWrite(out string why))
            {
                Log($"collect: not writing ({why}); checking again in 10 min");
                writer.Close();
                await Delay(TimeSpan.FromMinutes(10), cts.Token).ConfigureAwait(false);
                continue;
            }

            string server = servers[serverIndex++ % servers.Count];
            try
            {
                long lines = await CaptureAsync(server, login, writer, guard, cts.Token).ConfigureAwait(false);
                if (lines > 1000)
                {
                    backoff = TimeSpan.FromSeconds(5);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // any failure of one connection is logged and retried
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log($"collect: {server}: {ex.GetType().Name}: {ex.Message}");
            }

            writer.Close();
            Log($"collect: reconnecting in {backoff.TotalSeconds:0} s");
            await Delay(backoff, cts.Token).ConfigureAwait(false);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
        }

        Log("collect: stopped");
        return 0;
    }

    private static async Task<long> CaptureAsync(string server, string login, HourlyGzipWriter writer, SpaceGuard guard, CancellationToken ct)
    {
        string[] hostPort = server.Split(':');
        using var tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            await tcp.ConnectAsync(hostPort[0], int.Parse(hostPort[1], CultureInfo.InvariantCulture), connectCts.Token).ConfigureAwait(false);
        }

        Log($"collect: connected to {server} ({tcp.Client.RemoteEndPoint})");
        using NetworkStream stream = tcp.GetStream();
        byte[] loginLine = Encoding.ASCII.GetBytes($"user {login} pass -1 vers Packet.Aprs.Corpus 0.1\r\n");
        await stream.WriteAsync(loginLine, ct).ConfigureAwait(false);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var pending = new ArrayBufferWriter<byte>(4096);
        long lines = 0;
        long bytes = 0;
        var statsStart = DateTime.UtcNow;
        long statsLines = 0;
        var lastSpaceCheck = DateTime.UtcNow;
        try
        {
            while (true)
            {
                int read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(StallTimeout);
                    try
                    {
                        read = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Log($"collect: {server}: no data for {StallTimeout.TotalSeconds:0} s, dropping link");
                        return lines;
                    }
                }

                if (read == 0)
                {
                    Log($"collect: {server}: closed by server after {lines} lines");
                    return lines;
                }

                bytes += read;
                var now = DateTime.UtcNow;
                ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                int nl;
                while ((nl = chunk.IndexOf((byte)'\n')) >= 0)
                {
                    pending.Write(chunk[..nl]);
                    ReadOnlySpan<byte> line = pending.WrittenSpan;
                    if (line.Length > 0 && line[^1] == (byte)'\r')
                    {
                        line = line[..^1];
                    }

                    if (line.Length > 0)
                    {
                        writer.Write(now, line);
                        lines++;
                        statsLines++;
                    }

                    pending.Clear();
                    chunk = chunk[(nl + 1)..];
                }

                pending.Write(chunk);

                if (now - lastSpaceCheck >= SpaceCheckInterval)
                {
                    lastSpaceCheck = now;
                    if (!guard.MayWrite(out string why))
                    {
                        Log($"collect: stopping capture ({why})");
                        return lines;
                    }
                }

                if (now - statsStart >= StatsInterval)
                {
                    double secs = (now - statsStart).TotalSeconds;
                    Log($"collect: {server}: {statsLines / secs:0.0} lines/s over last {secs / 60:0} min, {lines} lines / {bytes / 1e6:0.0} MB this connection");
                    statsStart = now;
                    statsLines = 0;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {message}");

    private sealed class SpaceGuard(string outDir, double minFreeGb, double maxGb)
    {
        public bool MayWrite(out string why)
        {
            double freeGb = new DriveInfo(Path.GetFullPath(outDir)).AvailableFreeSpace / 1e9;
            if (freeGb < minFreeGb)
            {
                why = $"only {freeGb:0.0} GB free, floor is {minFreeGb} GB";
                return false;
            }

            double usedGb = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1e9;
            if (usedGb >= maxGb)
            {
                why = $"corpus holds {usedGb:0.0} GB, cap is {maxGb} GB";
                return false;
            }

            why = "";
            return true;
        }
    }
}
