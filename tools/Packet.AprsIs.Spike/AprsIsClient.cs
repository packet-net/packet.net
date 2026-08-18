using System.Net.Sockets;
using System.Text;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Minimal APRS-IS TCP client. Connects, sends a login line, and yields
/// each received line via <see cref="ReadLinesAsync"/>.
/// </summary>
/// <remarks>
/// Read-only by convention - login passcode of <c>-1</c> tells the server
/// the client is monitoring only and won't be allowed to inject frames.
/// </remarks>
public sealed class AprsIsClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task ConnectAsync(string host, int port, string callsign, int passcode, string filter, CancellationToken ct)
    {
        await _tcp.ConnectAsync(host, port, ct);
        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.Latin1);
        _writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };

        // Discard the server banner (one line starting with '#').
        string? banner = await _reader.ReadLineAsync(ct);
        Console.Error.WriteLine($"# banner: {banner}");

        string login = $"user {callsign} pass {passcode} vers PacketNET.AprsIs.Spike 0.1 filter {filter}";
        await _writer.WriteLineAsync(login);

        // Server replies with another '#' line - log it then enter the data stream.
        string? loginAck = await _reader.ReadLineAsync(ct);
        Console.Error.WriteLine($"# login-ack: {loginAck}");
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_reader is null)
        {
            throw new InvalidOperationException("not connected");
        }

        while (!ct.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(ct);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
