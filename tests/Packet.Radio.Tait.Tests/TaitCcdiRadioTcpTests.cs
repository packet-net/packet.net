using System.Net;
using System.Net.Sockets;
using System.Text;
using Packet.Radio;

namespace Packet.Radio.Tait.Tests;

/// <summary>
/// Drives <see cref="TaitCcdiRadio"/> over the TCP-backed <see cref="ISerialIo"/>
/// (<c>TaitCcdiRadio.OpenTcp</c> / <c>TcpSerialIo</c>) against a loopback
/// <see cref="TcpListener"/> standing in for the split-station head-end, proving CCDI
/// transactions, DCD carrier-sense edges and the no-response timeout all work over a raw
/// socket. Mirrors the scripted-IO style of <see cref="TaitCcdiRadioTests"/>, with the
/// head-end scripted the way <c>FakeSerialIo</c> scripts the local seam.
/// </summary>
public class TaitCcdiRadioTcpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // The radio never pings during these tests: a raw-pipe head-end has no radio to answer.
    private static TaitCcdiRadioOptions NoWatchdog(TimeSpan? txnTimeout = null) =>
        new() { KeepAliveInterval = null, TransactionTimeout = txnTimeout ?? TimeSpan.FromSeconds(2) };

    [Fact]
    public async Task OpenTcp_round_trips_a_CCDI_RSSI_transaction_over_the_socket()
    {
        using var head = new LoopbackHeadEnd();
        await using var radio = await TaitCcdiRadio.OpenTcp("127.0.0.1", head.Port, options: NoWatchdog());
        var server = await head.Accepted.WaitAsync(Timeout);

        var rssiTask = radio.ReadRssiDbmAsync().AsTask();

        // Head-end: consume the CCDI query (q0450645C\r), then answer with the canned RSSI reply.
        // Reading the command first guarantees the driver has already registered the in-flight
        // transaction, so the reply correlates rather than routing as an unsolicited message.
        await ReceiveSomeAsync(server);
        await server.SendAsync(Latin1(".j07064-456C9\r."));

        (await rssiTask.WaitAsync(Timeout)).Should().BeApproximately(-45.6f, 0.001f);
    }

    [Fact]
    public async Task Unsolicited_progress_over_TCP_raises_CarrierSense_and_flips_ChannelBusy()
    {
        using var head = new LoopbackHeadEnd();
        await using var radio = await TaitCcdiRadio.OpenTcp("127.0.0.1", head.Port, options: NoWatchdog());
        var server = await head.Accepted.WaitAsync(Timeout);

        var edges = new List<bool>();
        var seen = new SemaphoreSlim(0);
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e.Busy);
            }
            seen.Release();
        };

        // A PROGRESS "receiver busy" byte sequence pushed by the head-end (unsolicited, no command
        // first) is the DCD carrier-sense edge — it must raise the event and flip the cached bool.
        await server.SendAsync(Latin1(".p0205C9\r."));
        (await seen.WaitAsync(Timeout)).Should().BeTrue();
        radio.ChannelBusy.Should().BeTrue();

        await server.SendAsync(Latin1(".p0206C8\r."));
        (await seen.WaitAsync(Timeout)).Should().BeTrue();
        radio.ChannelBusy.Should().BeFalse();

        lock (edges)
        {
            edges.Should().Equal(new[] { true, false }, "the DCD edges cross the wire as PROGRESS bytes");
        }
    }

    [Fact]
    public async Task A_no_response_transaction_over_TCP_propagates_TimeoutException()
    {
        using var head = new LoopbackHeadEnd();
        await using var radio = await TaitCcdiRadio.OpenTcp(
            "127.0.0.1", head.Port, options: NoWatchdog(txnTimeout: TimeSpan.FromMilliseconds(300)));
        _ = await head.Accepted.WaitAsync(Timeout);   // accept, but the head-end never answers

        // The per-read socket timeout throws TimeoutException every 100 ms (swallowed by the
        // pump); the transaction engine's own deadline is what surfaces the no-response.
        var act = async () => await radio.ReadRssiDbmAsync();

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task OpenTcp_clocks_the_initial_baud_through_the_injected_callback()
    {
        using var head = new LoopbackHeadEnd();
        var clocked = new List<int>();
        await using var radio = await TaitCcdiRadio.OpenTcp(
            "127.0.0.1", head.Port, baudRate: 19200,
            setBaud: (baud, _) => { lock (clocked) { clocked.Add(baud); } return Task.CompletedTask; },
            options: NoWatchdog());
        _ = await head.Accepted.WaitAsync(Timeout);

        lock (clocked)
        {
            clocked.Should().Equal(new[] { 19200 }, "the head-end owns the physical clock, set via the verb at open");
        }
    }

    [Fact]
    public async Task SetBaudRate_routes_to_the_callback_and_a_null_callback_is_a_no_op()
    {
        using var head = new LoopbackHeadEnd();

        var clocked = new List<int>();
        using (var io = await TcpSerialIo.ConnectAsync(
            "127.0.0.1", head.Port,
            setBaud: (baud, _) => { lock (clocked) { clocked.Add(baud); } return Task.CompletedTask; }))
        {
            io.SetBaudRate(28800);
            lock (clocked)
            {
                clocked.Should().Equal(28800);
            }
        }

        // The Stage-1 default: no callback ⇒ SetBaudRate is a silent no-op so a raw pipe works.
        using var raw = await TcpSerialIo.ConnectAsync("127.0.0.1", head.Port);
        var act = () => raw.SetBaudRate(9600);
        act.Should().NotThrow();
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static async Task ReceiveSomeAsync(Socket socket)
    {
        var buffer = new byte[256];
        await socket.ReceiveAsync(buffer.AsMemory()).AsTask().WaitAsync(Timeout);
    }

    /// <summary>A loopback <see cref="TcpListener"/> standing in for the split-station head-end:
    /// the accepted socket is the head-end side the test scripts CCDI bytes onto.</summary>
    private sealed class LoopbackHeadEnd : IDisposable
    {
        private readonly TcpListener listener;

        public LoopbackHeadEnd()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Accepted = listener.AcceptSocketAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Task<Socket> Accepted { get; }

        public void Dispose()
        {
            listener.Stop();
            if (Accepted.IsCompletedSuccessfully)
            {
                Accepted.Result.Dispose();
            }
        }
    }
}
