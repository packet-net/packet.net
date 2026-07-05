using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Kiss;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A loopback <see cref="TcpListener"/> standing in for a head-end's raw byte pipe (the transport
/// the head-end factory branches dial after resolving the inventory). Exposes the assigned
/// <see cref="Port"/> (put it in the stub inventory's <c>tcpPort</c>) and the accepted socket, plus a
/// tiny CCDI responder that answers each command with a prompt so a <c>TaitCcdiRadio.OpenTcp</c> +
/// <c>SetProgressMessagesAsync</c> handshake completes over the socket.
/// </summary>
public sealed class LoopbackRawPipe : IDisposable
{
    private readonly TcpListener listener;

    public LoopbackRawPipe()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Accepted = listener.AcceptSocketAsync();
    }

    /// <summary>The ephemeral loopback port the pipe listens on.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The accepted client socket (the head-end side the test scripts bytes onto).</summary>
    public Task<Socket> Accepted { get; }

    /// <summary>
    /// Answer every inbound CCDI command with a <c>.</c> prompt (0x2E) — the transaction-complete
    /// marker <c>TaitCcdiRadio</c> waits for on a prompt-completed command like the progress-enable
    /// the radio factory issues. Runs until the socket closes. Await the returned task after tearing
    /// the radio down.
    /// </summary>
    public Task RespondCcdiPromptsAsync() => Task.Run(async () =>
    {
        var socket = await Accepted.ConfigureAwait(false);
        var buffer = new byte[256];
        while (true)
        {
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch
            {
                break; // socket torn down — done
            }
            if (read == 0)
            {
                break;
            }
            try
            {
                await socket.SendAsync(Encoding.Latin1.GetBytes(".").AsMemory()).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    });

    /// <summary>
    /// Answer a NinoTNC GETVER reach-through probe: wait for the GETVER command frame, then reply
    /// with the bare ASCII <paramref name="version"/> on raw KISS command byte 0xE0 (port 14 +
    /// command 0), exactly as the firmware does — so <c>NinoTncSerialPort.GetVersionAsync</c>
    /// completes over the socket. Runs until the socket closes.
    /// </summary>
    public Task RespondGetVerAsync(string version) => Task.Run(async () =>
    {
        var socket = await Accepted.ConfigureAwait(false);
        var buffer = new byte[256];
        try
        {
            // The first bytes the head-end sees are the GETVER command frame — reply once with the
            // version frame (the probe issues no other command).
            int read = await socket.ReceiveAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            var reply = KissEncoder.Encode(14, KissCommand.Data, Encoding.ASCII.GetBytes(version));
            await socket.SendAsync(reply.AsMemory()).ConfigureAwait(false);
        }
        catch
        {
            // socket torn down — done
        }
    });

    /// <summary>
    /// Answer a Tait CCDI reach-through identify (<c>QueryIdentityAsync</c>): reply to the MODEL,
    /// RADIO_SERIAL and RADIO_VERSIONS queries with valid checksummed CCDI frames — but only while
    /// <paramref name="shouldAnswer"/> returns true, so a baud-sweep test can gate the answer on the
    /// "right" line rate having been set (a wrong clock ⇒ silence ⇒ the MODEL query times out and the
    /// sweep tries the next rate). A null gate answers unconditionally. Runs until the socket closes.
    /// </summary>
    /// <param name="ccdiVersion">The CCDI version the MODEL reply reports (e.g. <c>03.02</c>).</param>
    /// <param name="serial">The CCDI serial number the RADIO_SERIAL reply reports.</param>
    /// <param name="modelTriple">The RUTYPE/RUMODEL/RUTIER triple (default <c>132</c> ⇒ "Tait TM8110").</param>
    /// <param name="shouldAnswer">Gate: answer only when this returns true (null ⇒ always).</param>
    public Task RespondTaitIdentityAsync(
        string ccdiVersion = "03.02",
        string serial = "1G000123",
        string modelTriple = "132",
        Func<bool>? shouldAnswer = null) => Task.Run(async () =>
    {
        var gate = shouldAnswer ?? (static () => true);
        var socket = await Accepted.ConfigureAwait(false);
        var buffer = new byte[256];
        var line = new StringBuilder(64);
        while (true)
        {
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (read == 0)
            {
                break;
            }
            for (int i = 0; i < read; i++)
            {
                char c = (char)buffer[i];
                if (c == '\r')
                {
                    var raw = line.ToString();
                    line.Clear();
                    if (!await ReplyToCcdiQueryAsync(socket, raw, ccdiVersion, serial, modelTriple, gate).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                else if (c != '\n')
                {
                    line.Append(c);
                }
            }
        }
    });

    // Reply to one CCDI query line per QueryIdentityAsync's three transactions. Returns false only on
    // a send failure (socket gone). A wrong-clock gate stays silent (the query times out upstream).
    private static async Task<bool> ReplyToCcdiQueryAsync(
        Socket socket, string raw, string ccdiVersion, string serial, string modelTriple, Func<bool> gate)
    {
        if (!CcdiFrame.TryParse(raw, out var frame) || frame.Ident != 'q' || !gate())
        {
            return true;
        }

        CcdiFrame? reply = frame.Parameters switch
        {
            "" => new CcdiFrame('m', modelTriple + ccdiVersion), // MODEL (§1.10.4)
            "4" => new CcdiFrame('n', serial),                   // RADIO_SERIAL (§1.10.7)
            "3" => new CcdiFrame('v', "00" + "TM8110"),          // RADIO_VERSIONS (§1.10.8), one record
            _ => null,
        };
        if (reply is not { } r)
        {
            return true;
        }

        try
        {
            await socket.SendAsync(r.EncodeToBytes().AsMemory()).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        listener.Stop();
        if (Accepted.IsCompletedSuccessfully)
        {
            Accepted.Result.Dispose();
        }
    }
}
