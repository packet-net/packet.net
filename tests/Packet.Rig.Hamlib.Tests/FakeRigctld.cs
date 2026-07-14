using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Packet.Rig.Hamlib.Tests;

/// <summary>
/// An in-process, scriptable NET-rigctl server — the seam the client tests drive, in the same
/// spirit as the scripted <c>ISerialIo</c> fakes elsewhere in the repo. Reply text mirrors the
/// wire format captured from a real <c>rigctld</c> 4.5.5 dummy rig (see
/// <c>docs/research/rig-control-spike.md</c>), so the parser is tested against bytes the real
/// daemon emits. Fault injection covers the paths the real daemon makes awkward: scripted
/// <c>RPRT</c> errors, swallowed replies (timeouts), and mid-command disconnects.
/// </summary>
internal sealed class FakeRigctld : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptLoop;

    internal FakeRigctld(bool vfoMode = false)
    {
        VfoMode = vfoMode;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptLoopAsync();
    }

    internal int Port { get; }

    internal bool VfoMode { get; }

    // Rig state — the real dummy rig's fresh values.
    internal long FrequencyHz = 145_000_000;
    internal string Mode = "FM";
    internal int PassbandHz = 15_000;
    internal int Ptt;
    internal readonly ConcurrentDictionary<string, double> Levels = new()
    {
        ["SWR"] = 1.0,
        ["RFPOWER_METER"] = 0.5,
        ["RFPOWER_METER_WATTS"] = 50.0,
        ["STRENGTH"] = -12,
    };

    /// <summary>Every command line received, in order — lets tests assert exact wire syntax.</summary>
    internal readonly ConcurrentQueue<string> ReceivedCommands = new();

    /// <summary>Connections accepted so far — reconnect assertions.</summary>
    internal int ConnectionCount;

    /// <summary>Scripted <c>RPRT -code</c> failures applied to upcoming rig commands (not to
    /// chk_vfo/dump_caps, so connects stay healthy).</summary>
    internal readonly ConcurrentQueue<int> FailNextWithCode = new();

    /// <summary>When set, the next rig command's reply is swallowed (read, never answered) —
    /// the client should time out.</summary>
    internal volatile bool SwallowNextReply;

    /// <summary>When set, the connection is closed instead of answering the next rig command —
    /// the client should surface a connection fault and redial on the following call.</summary>
    internal volatile bool DropBeforeNextReply;

    /// <summary>Replaces the canned <c>dump_caps</c> payload — capability-shaping tests.</summary>
    internal string[]? DumpCapsOverride;

    private static readonly string[] DefaultDumpCaps =
    [
        "Caps dump for model: 1",
        "Model name:\tDummy",
        "Mfg name:\tHamlib",
        "Backend version:\t20221128.0",
        "Can set Frequency:\tY",
        "Can get Frequency:\tY",
        "Can set Mode:\tY",
        "Can get Mode:\tY",
        "Can set PTT:\tY",
        "Can get PTT:\tY",
        "Get level: SWR(0..0/0) RFPOWER_METER(0..0/0) RFPOWER_METER_WATTS(0..0/0) STRENGTH(0..0/0)",
    ];

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref ConnectionCount);
                _ = HandleConnectionAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                ReceivedCommands.Enqueue(line);
                var reply = BuildReply(line);
                if (reply is null)
                {
                    continue; // swallowed — client times out
                }

                if (reply.Length == 0)
                {
                    return; // scripted disconnect
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection torn down — fine either way.
        }
    }

    /// <summary>null = swallow (no reply); "" = drop the connection; else the bytes to send.</summary>
    private string? BuildReply(string line)
    {
        var extended = line.StartsWith('+');
        var body = extended ? line[1..] : line;
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return "RPRT -1\n";
        }

        var cmd = tokens[0];

        // Connect-phase commands are exempt from fault injection so redials succeed.
        if (cmd == "\\chk_vfo")
        {
            // 4.x wire shape: bare digit, no RPRT — ever.
            return VfoMode ? "1\n" : "0\n";
        }

        if (cmd == "\\dump_caps")
        {
            var sb = new StringBuilder();
            if (extended)
            {
                sb.Append("dump_caps:\n");
            }

            foreach (var capsLine in DumpCapsOverride ?? DefaultDumpCaps)
            {
                sb.Append(capsLine).Append('\n');
            }

            sb.Append("RPRT 0\n");
            return sb.ToString();
        }

        if (SwallowNextReply)
        {
            SwallowNextReply = false;
            return null;
        }

        if (DropBeforeNextReply)
        {
            DropBeforeNextReply = false;
            return "";
        }

        var args = tokens.Skip(1).ToList();
        if (VfoMode)
        {
            // In --vfo mode every rig command carries the VFO as its first argument.
            if (args.Count == 0 || args[0] is not ("currVFO" or "VFOA" or "VFOB" or "Main" or "Sub"))
            {
                return Error(extended, cmd, args, 1); // RIG_EINVAL, roughly what the real daemon does
            }

            args.RemoveAt(0);
        }

        if (FailNextWithCode.TryDequeue(out var failCode))
        {
            return Error(extended, cmd, args, failCode);
        }

        return cmd switch
        {
            "f" => Reply(extended, "get_freq", args,
                $"Frequency: {FrequencyHz.ToString(CultureInfo.InvariantCulture)}"),
            "F" when args.Count == 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) =>
                Apply(() => FrequencyHz = (long)hz, extended, "set_freq", args),
            "m" => Reply(extended, "get_mode", args,
                $"Mode: {Mode}", $"Passband: {PassbandHz.ToString(CultureInfo.InvariantCulture)}"),
            "M" when args.Count == 2 && int.TryParse(args[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pb) =>
                Apply(
                    () =>
                    {
                        Mode = args[0];
                        // Real hamlib: 0 = the mode's default width, -1 = leave unchanged.
                        PassbandHz = pb switch { 0 => DefaultPassband(args[0]), -1 => PassbandHz, _ => pb };
                    },
                    extended, "set_mode", args),
            "t" => Reply(extended, "get_ptt", args, $"PTT: {Ptt.ToString(CultureInfo.InvariantCulture)}"),
            "T" when args.Count == 1 && int.TryParse(args[0], out var ptt) =>
                Apply(() => Ptt = ptt, extended, "set_ptt", args),
            "l" when args.Count == 1 && Levels.TryGetValue(args[0], out var level) =>
                Reply(extended, "get_level", args, level.ToString("F6", CultureInfo.InvariantCulture)),
            "l" => Error(extended, "get_level", args, 1),
            "L" when args.Count == 2 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) =>
                Apply(() => Levels[args[0]] = value, extended, "set_level", args),
            "q" => "RPRT 0\n",
            _ => Error(extended, cmd, args, 1),
        };

        static int DefaultPassband(string mode) => mode switch
        {
            "FM" => 15_000,
            "AM" => 8_000,
            "CW" => 500,
            _ => 2_400,
        };
    }

    private static string Reply(bool extended, string echoName, List<string> args, params string[] payload)
    {
        var sb = new StringBuilder();
        if (extended)
        {
            sb.Append(echoName).Append(':');
            if (args.Count > 0)
            {
                sb.Append(' ').Append(string.Join(' ', args));
            }

            sb.Append('\n');
        }

        foreach (var line in payload)
        {
            // Default protocol carries bare values; extended keeps the "Key: value" labels.
            // (get_level payloads are bare in both — the caller passes them label-free.)
            var text = extended ? line : StripLabel(line);
            sb.Append(text).Append('\n');
        }

        if (extended)
        {
            sb.Append("RPRT 0\n");
        }

        return sb.ToString();

        static string StripLabel(string line)
        {
            var idx = line.IndexOf(": ", StringComparison.Ordinal);
            return idx >= 0 ? line[(idx + 2)..] : line;
        }
    }

    private static string Apply(Action mutate, bool extended, string echoName, List<string> args)
    {
        mutate();
        return extended
            ? $"{echoName}:{(args.Count > 0 ? " " + string.Join(' ', args) : "")}\nRPRT 0\n"
            : "RPRT 0\n";
    }

    private static string Error(bool extended, string echoName, List<string> args, int code)
        => extended
            ? $"{echoName}:{(args.Count > 0 ? " " + string.Join(' ', args) : "")}\nRPRT -{code}\n"
            : $"RPRT -{code}\n";

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
        }

        cts.Dispose();
    }
}
