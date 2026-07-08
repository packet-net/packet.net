using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Packet.Kiss.NinoTnc.Tests;

/// <summary>
/// The keep-alive poll (#580): a nino-tnc-tcp pipe on an RF-quiet channel produces no application
/// bytes at all (the head-end bridge is a pure pump; TCP keepalive carries no data), so without a
/// probe the transport's 5-min read-idle liveness budget faults a perfectly healthy port every
/// 5 minutes all night. The driver's periodic GETVER (default 2 min of frame-silence, mirroring
/// the Tait watchdog) generates reply bytes that feed the budget — while a genuinely dead link
/// gets no reply and still faults on the same budget.
/// </summary>
public class NinoTncKeepAliveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_quiet_link_gets_a_getver_probe_after_the_keepalive_interval()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io, clock);
        await using var nino = NinoTncSerialPort.OpenForTest(
            modem, clock, new NinoTncSerialPortOptions { KeepAliveInterval = TimeSpan.FromMinutes(2) });

        io.Writes.Should().BeEmpty("nothing probes while the interval has not elapsed");

        // Walk fake time forward in half-interval steps (the loop checks every interval/2).
        for (int i = 0; i < 8 && io.Writes.Length == 0; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(25);
        }

        await WaitUntil(() => io.Writes.Length >= 1, "the keep-alive GETVER goes out once the link has been quiet the full interval");
        io.Writes[0].Should().Equal(
            KissEncoder.Encode(0, (KissCommand)NinoTncCommands.GetVersionCommand, NinoTncCommands.BuildGetVersionPayload()),
            "the probe is a plain GETVER — harmless to the TNC, guaranteed to elicit reply bytes");
    }

    [Fact]
    public async Task Local_serial_open_has_no_keepalive_by_default()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io, clock);
        // OpenForTest with no options mirrors NinoTncSerialPort.Open (local serial): no probe —
        // a local port has no read-idle liveness budget to feed.
        await using var nino = NinoTncSerialPort.OpenForTest(modem, clock);

        for (int i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10);
        }

        io.Writes.Should().BeEmpty("no options ⇒ no keep-alive loop");
    }

    [Fact]
    public async Task Keepalive_replies_keep_a_quiet_tcp_link_alive_past_the_idle_budget_and_a_dead_link_still_faults()
    {
        var clock = new FakeTimeProvider();
        using var head = new LoopbackHeadEnd();
        // OpenTcp defaults: keep-alive ON at 2 min; the transport's read-idle budget is 5 min.
        await using var nino = await NinoTncSerialPort.OpenTcp("127.0.0.1", head.Port, clock);
        var server = await head.Accepted.WaitAsync(Timeout);

        // Head-end side: answer each probe with a GETVER version reply while `answering`.
        int answering = 1;
        int answered = 0;
        var responder = Task.Run(async () =>
        {
            var buffer = new byte[256];
            while (true)
            {
                int read;
                try
                {
                    read = await server.ReceiveAsync(buffer.AsMemory());
                }
                catch
                {
                    return;
                }
                if (read == 0)
                {
                    return;
                }
                if (Volatile.Read(ref answering) == 1)
                {
                    await server.SendAsync(KissEncoder.Encode(14, KissCommand.Data, "3.41"u8.ToArray()).AsMemory());
                    Interlocked.Increment(ref answered);
                }
            }
        });

        Exception? streamFault = null;
        var stream = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in nino.ReadFramesAsync())
                {
                }
            }
            catch (Exception ex)
            {
                streamFault = ex;
            }
        });

        // March fake time past the 5-min idle budget, but never let the fake clock outrun the
        // real probe round-trip: whenever 4 fake minutes have passed since the last observed
        // reply (probes are due every 2), HOLD the clock and wait for the reply to land — so a
        // spurious idle fault (a pure test-timing artefact) is impossible by construction.
        var step = TimeSpan.FromSeconds(30);
        var fakeElapsed = TimeSpan.Zero;
        var lastReplyAt = TimeSpan.Zero;
        int seen = 0;
        while (fakeElapsed < TimeSpan.FromMinutes(6.5) || Volatile.Read(ref answered) < 2)
        {
            if (Volatile.Read(ref answered) > seen)
            {
                seen = Volatile.Read(ref answered);
                lastReplyAt = fakeElapsed;
            }
            if (fakeElapsed - lastReplyAt >= TimeSpan.FromMinutes(4))
            {
                int before = Volatile.Read(ref answered);
                await WaitUntil(() => Volatile.Read(ref answered) > before, "the due keep-alive probe is answered");
                continue;
            }
            clock.Advance(step);
            fakeElapsed += step;
            await Task.Delay(25);
        }

        Volatile.Read(ref answered).Should().BeGreaterThanOrEqualTo(2, "the probe repeats for as long as the link is quiet");
        stream.IsCompleted.Should().BeFalse(
            "6+ RF-quiet minutes exceed the 5-min idle budget — only the keep-alive replies kept the link alive");

        // The head-end dies half-open: it stops answering (no FIN, no bytes, ever again).
        Volatile.Write(ref answering, 0);
        for (int i = 0; i < 14 && !stream.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(120);   // let the transport's real 100 ms read-timeout tick observe the fake idle
        }

        await stream.WaitAsync(Timeout);
        streamFault.Should().BeOfType<IOException>(
            "with no reply bytes the read-idle budget must still fault the dead link — the reconnect wrapper depends on it");
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"condition not met within {Timeout.TotalSeconds:0}s: {because}");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>A loopback listener standing in for the head-end's raw pipe.</summary>
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

    /// <summary>A scripted, thread-safe <see cref="ISerialPortIo"/> capturing writes.</summary>
    private sealed class FakeSerialPortIo : ISerialPortIo
    {
        private readonly BlockingCollection<byte[]?> steps = new();
        private readonly List<byte[]> writes = [];
        private readonly object writeGate = new();

        public string PortName => "FAKENINO";

        public byte[][] Writes
        {
            get
            {
                lock (writeGate)
                {
                    return writes.ToArray();
                }
            }
        }

        public void FeedBytes(byte[] data) => steps.Add(data);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!steps.TryTake(out var data, 25))
            {
                throw new TimeoutException();
            }
            if (data is null)
            {
                throw new IOException("port closed");
            }
            int n = Math.Min(count, data.Length);
            Array.Copy(data, 0, buffer, offset, n);
            return n;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (writeGate)
            {
                writes.Add(buffer[offset..(offset + count)]);
            }
        }

        public void Dispose() => steps.CompleteAdding();
    }
}
