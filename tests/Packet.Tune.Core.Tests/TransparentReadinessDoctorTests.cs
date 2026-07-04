using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Radio.Tait;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The Transparent-readiness doctor (<see cref="TransparentReadinessDoctor"/>) over a scripted
/// radio and in-memory transports — the FAIL/stuck paths the task calls out (the 0/06 "not
/// enabled", the escape-ignored wedge, the baud garble) without touching hardware. The happy
/// paths are validated on the bench rig.
/// </summary>
public sealed class TransparentReadinessDoctorTests
{
    private static string EnterCommand() => new CcdiFrame('t', "+0").Encode();
    private static string ModelQuery() => new CcdiFrame('q', "").Encode();
    private const string ModelReply = ".m0813203.02A2\r.";

    private static readonly TransparentReadinessOptions ShortOpts = new()
    {
        EscapeAttempts = 2,
        EscapeGuard = TimeSpan.FromMilliseconds(10),
        VerifyTimeout = TimeSpan.FromMilliseconds(200),
        LoopbackTimeout = TimeSpan.FromSeconds(2),
    };

    private static TaitCcdiRadio ScriptedRadio(ScriptedSerialIo io) =>
        TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions { KeepAliveInterval = null });

    [Fact]
    public async Task Enabled_probe_FAILS_with_the_data_form_remedy_when_entry_is_rejected_0_06()
    {
        var io = new ScriptedSerialIo();
        io.RespondTo(EnterCommand(), ".e03006A2\r."); // radio programmed with Transparent disabled
        await using var radio = ScriptedRadio(io);

        var result = await TransparentReadinessDoctor.RunEnableAndEscapeProbesAsync(radio, ShortOpts);

        var enabled = result.Probes.Single(p => p.Name == TransparentReadinessDoctor.EnabledProbe);
        enabled.Outcome.Should().Be(DoctorOutcome.Fail);
        enabled.Detail.Should().Contain("0/06");
        enabled.Remedy.Should().Be(TransparentReadinessDoctor.EnabledRemedy);
        // The escape probe is not run — the radio never entered Transparent, and nothing is wedged.
        result.Probes.Single(p => p.Name == TransparentReadinessDoctor.EscapeProbe)
            .Outcome.Should().Be(DoctorOutcome.Unknown);
        result.RadioWedged.Should().BeFalse();
        result.EnteredAndRecovered.Should().BeFalse();
    }

    [Fact]
    public async Task Escape_probe_PASSES_when_the_escape_recovers_command_mode()
    {
        var io = new ScriptedSerialIo();
        io.RespondTo(EnterCommand(), ".");            // Transparent enabled → enters
        io.RespondTo(ModelQuery(), ModelReply);        // +++ recovers → the MODEL query answers
        await using var radio = ScriptedRadio(io);

        var result = await TransparentReadinessDoctor.RunEnableAndEscapeProbesAsync(radio, ShortOpts);

        result.Probes.Single(p => p.Name == TransparentReadinessDoctor.EnabledProbe)
            .Outcome.Should().Be(DoctorOutcome.Pass);
        result.Probes.Single(p => p.Name == TransparentReadinessDoctor.EscapeProbe)
            .Outcome.Should().Be(DoctorOutcome.Pass);
        result.RadioWedged.Should().BeFalse();
        result.EnteredAndRecovered.Should().BeTrue();
        radio.Mode.Should().Be(TaitProtocolMode.Command);
    }

    [Fact]
    public async Task Escape_probe_FAILS_and_surfaces_a_WEDGED_radio_when_the_escape_is_ignored()
    {
        var io = new ScriptedSerialIo();
        io.RespondTo(EnterCommand(), ".");             // enters Transparent
        // No MODEL reply scripted: the escape is ignored (Ignore-Escape ON) — the radio wedges.
        await using var radio = ScriptedRadio(io);

        var result = await TransparentReadinessDoctor.RunEnableAndEscapeProbesAsync(radio, ShortOpts);

        var escape = result.Probes.Single(p => p.Name == TransparentReadinessDoctor.EscapeProbe);
        escape.Outcome.Should().Be(DoctorOutcome.Fail);
        escape.Detail.Should().Contain("WEDGED");
        escape.Remedy.Should().Be(TransparentReadinessDoctor.EscapeRemedy);
        result.RadioWedged.Should().BeTrue();
        result.EnteredAndRecovered.Should().BeFalse();
    }

    [Fact]
    public async Task BaudClean_probe_PASSES_when_the_frame_round_trips_byte_for_byte()
    {
        var (local, peer) = InMemoryTransport.Pair();

        var probe = await TransparentReadinessDoctor.RunBaudCleanProbeAsync(local, peer, ShortOpts);

        probe.Outcome.Should().Be(DoctorOutcome.Pass);
        probe.Detail.Should().Contain("byte-for-byte");
    }

    [Fact]
    public async Task BaudClean_probe_FAILS_with_the_baud_remedy_when_a_byte_is_altered()
    {
        var (local, peer) = InMemoryTransport.Pair();
        local.CorruptSent = bytes =>
        {
            var copy = (byte[])bytes.Clone();
            copy[^1] ^= 0xFF; // one byte garbled in transit — a baud/FFSK mismatch signature
            return copy;
        };

        var probe = await TransparentReadinessDoctor.RunBaudCleanProbeAsync(local, peer, ShortOpts);

        probe.Outcome.Should().Be(DoctorOutcome.Fail);
        probe.Detail.Should().Contain("garbled");
        probe.Remedy.Should().Be(TransparentReadinessDoctor.BaudCleanRemedy);
    }

    [Fact]
    public async Task BaudClean_probe_is_UNKNOWN_when_nothing_arrives_at_the_peer()
    {
        var (local, peer) = InMemoryTransport.Pair();
        local.Deliver = false; // the peer hears nothing (out of range / not in Transparent)

        var probe = await TransparentReadinessDoctor.RunBaudCleanProbeAsync(
            local, peer, ShortOpts with { LoopbackTimeout = TimeSpan.FromMilliseconds(300) });

        probe.Outcome.Should().Be(DoctorOutcome.Unknown);
        probe.Remedy.Should().BeNull();
    }

    [Fact]
    public void BaudCleanNeedsPeer_is_an_unknown_row_pointing_at_the_peer_requirement()
    {
        var probe = TransparentReadinessDoctor.BaudCleanNeedsPeer();

        probe.Name.Should().Be(TransparentReadinessDoctor.BaudCleanProbe);
        probe.Outcome.Should().Be(DoctorOutcome.Unknown);
        probe.Detail.Should().Contain("peer");
    }

    /// <summary>An in-memory <see cref="IAx25Transport"/> pair: <see cref="SendAsync"/> delivers to
    /// the peer's inbound stream, optionally corrupting or dropping the frame to simulate a garbled
    /// or absent link.</summary>
    private sealed class InMemoryTransport : IAx25Transport
    {
        private readonly Channel<Ax25InboundFrame> rx = Channel.CreateUnbounded<Ax25InboundFrame>();

        public InMemoryTransport Peer { get; private set; } = null!;

        /// <summary>When false, sends are black-holed (the peer receives nothing).</summary>
        public bool Deliver { get; set; } = true;

        /// <summary>Optional in-transit corruption applied to each sent frame.</summary>
        public Func<byte[], byte[]>? CorruptSent { get; set; }

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            if (Deliver)
            {
                byte[] bytes = ax25.ToArray();
                if (CorruptSent is not null)
                {
                    bytes = CorruptSent(bytes);
                }
                Peer.rx.Writer.TryWrite(new Ax25InboundFrame(bytes, PortId: 0, ReceivedAt: DateTimeOffset.UtcNow));
            }
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
            rx.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            rx.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public static (InMemoryTransport Local, InMemoryTransport Peer) Pair()
        {
            var a = new InMemoryTransport();
            var b = new InMemoryTransport();
            a.Peer = b;
            b.Peer = a;
            return (a, b);
        }
    }

    /// <summary>A scripted <see cref="ISerialIo"/> for driving a <see cref="TaitCcdiRadio"/>:
    /// blocking finite-timeout reads against a queue, with canned replies keyed on the written
    /// command line (minus its trailing CR). Mirrors the Radio.Tait test fake.</summary>
    private sealed class ScriptedSerialIo : ISerialIo
    {
        private readonly BlockingCollection<byte[]> incoming = [];
        private readonly ConcurrentDictionary<string, string> responses = new();
        private readonly StringBuilder written = new();
        private readonly Lock gate = new();

        public string PortName => "scripted";

        public string WrittenAscii
        {
            get
            {
                lock (gate)
                {
                    return written.ToString();
                }
            }
        }

        public void RespondTo(string commandWithoutCr, string responseAscii) =>
            responses[commandWithoutCr] = responseAscii;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!incoming.TryTake(out var chunk, TimeSpan.FromMilliseconds(25)))
            {
                throw new TimeoutException();
            }
            chunk.CopyTo(buffer.AsSpan(offset, count));
            return chunk.Length;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            string ascii = Encoding.Latin1.GetString(buffer, offset, count);
            lock (gate)
            {
                written.Append(ascii);
            }
            if (responses.TryGetValue(ascii.TrimEnd('\r'), out string? reply))
            {
                incoming.Add(Encoding.Latin1.GetBytes(reply));
            }
        }

        public void SetBaudRate(int baudRate)
        {
        }

        public void Dispose() => incoming.Dispose();
    }
}
