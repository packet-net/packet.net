using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Tune.Core;

/// <summary>
/// <see cref="ITuningLink"/> over a WebSocket, for the internet flavour of a
/// remote tuning session: both ends connect to a <see cref="RendezvousRelay"/>
/// with the same spoken 6-digit PIN, and the relay forwards frames verbatim.
/// Telegrams travel as JSON text frames <c>{"v":1,"seq":…,"verb":"…","args":"…"}</c>
/// (room to grow: timestamps, GETALL deltas). The underlying TCP stream is
/// reliable and ordered, so <see cref="SendAsync"/> needs no retry layer;
/// inbound sequence numbers are still deduplicated for symmetry with
/// <see cref="SdmTuningLink"/>.
/// </summary>
public sealed class WebSocketTuningLink : ITuningLink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WebSocket socket;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly HashSet<int> seenSequences = [];
    private readonly Queue<int> seenOrder = new();
    private int disposed;

    private WebSocketTuningLink(WebSocket socket) => this.socket = socket;

    /// <summary>
    /// Connect to a rendezvous relay and join the session for
    /// <paramref name="pin"/>. The first client parks; the second pairs and
    /// the session is live.
    /// </summary>
    /// <param name="rendezvous">The relay endpoint, e.g. <c>ws://host:8735</c>
    /// (a bare authority gets the standard <c>/ws</c> path appended).</param>
    /// <param name="pin">The session's 6-digit PIN.</param>
    /// <param name="role">This end's role, <c>tuned</c> or <c>meter</c> (informational).</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <exception cref="TuningLinkException">The relay refused the connection
    /// (bad PIN, PIN already used, relay down).</exception>
    public static async Task<WebSocketTuningLink> ConnectAsync(
        Uri rendezvous, string pin, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvous);
        ArgumentException.ThrowIfNullOrEmpty(pin);
        ArgumentException.ThrowIfNullOrEmpty(role);

        var builder = new UriBuilder(rendezvous);
        if (builder.Path is "" or "/")
        {
            builder.Path = "/ws";
        }
        builder.Query = $"pin={Uri.EscapeDataString(pin)}&role={Uri.EscapeDataString(role)}";

        var client = new ClientWebSocket();
        try
        {
            await client.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            client.Dispose();
            throw new TuningLinkException($"could not join rendezvous session at {builder.Uri}: {ex.Message}", ex);
        }
        return new WebSocketTuningLink(client);
    }

    /// <summary>Wrap an already-open WebSocket (e.g. a relay-side or test socket).</summary>
    public static WebSocketTuningLink FromSocket(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return new WebSocketTuningLink(socket);
    }

    /// <inheritdoc/>
    public async Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var frame = new WireFrame(1, telegram.Sequence, TuningTelegram.VerbToWire(telegram.Verb), telegram.Args);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
        {
            throw new TuningLinkException($"tuning link lost while sending: {ex.Message}", ex);
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[4096];
        var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                // Relay died / peer socket dropped: the session is over.
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (TryDecode(text) is { } telegram && MarkSeen(telegram.Sequence))
            {
                yield return telegram;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
        socket.Dispose();
        sendGate.Dispose();
    }

    private static TuningTelegram? TryDecode(string text)
    {
        WireFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize<WireFrame>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (frame is null || frame.V != 1)
        {
            return null;
        }
        // Reuse the text codec's verb table by round-tripping the pipe form.
        string head = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{TuningTelegram.VersionPrefix}|{frame.Seq}|{frame.Verb}");
        string wire = frame.Args.Length == 0 ? head : head + "|" + frame.Args;
        return TuningTelegram.TryParse(wire, out var telegram) ? telegram : null;
    }

    private bool MarkSeen(int sequence)
    {
        lock (seenSequences)
        {
            if (!seenSequences.Add(sequence))
            {
                return false;
            }
            seenOrder.Enqueue(sequence);
            while (seenOrder.Count > 64)
            {
                seenSequences.Remove(seenOrder.Dequeue());
            }
            return true;
        }
    }

    private sealed record WireFrame(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("seq")] int Seq,
        [property: JsonPropertyName("verb")] string Verb,
        [property: JsonPropertyName("args")] string Args);
}
