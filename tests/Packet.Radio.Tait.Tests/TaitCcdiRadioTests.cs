using System.Collections.Concurrent;
using System.Text;
using Packet.Radio;

namespace Packet.Radio.Tait.Tests;

public class TaitCcdiRadioTests
{
    [Fact]
    public async Task ReadRssi_Sends_Query_And_Parses_Result()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("q0450645C", ".j07064-456C9\r.");
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        float rssi = await radio.ReadRssiDbmAsync();

        rssi.Should().BeApproximately(-45.6f, 0.001f);
        io.WrittenAscii.Should().Contain("q0450645C\r");
    }

    [Fact]
    public async Task Unsolicited_Progress_Raises_CarrierSense_And_Tracks_ChannelBusy()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        var edges = new List<CarrierSenseChange>();
        var seen = new SemaphoreSlim(0);
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e);
            }
            seen.Release();
        };

        io.Enqueue(".p0205C9\r.");
        (await seen.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        radio.ChannelBusy.Should().BeTrue();

        io.Enqueue(".p0206C8\r.");
        (await seen.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        radio.ChannelBusy.Should().BeFalse();

        lock (edges)
        {
            edges.Select(e => e.Busy).Should().Equal(true, false);
        }
    }

    [Fact]
    public async Task Error_Response_Throws_TaitCcdiException()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("q0450645C", ".e03001A7\r.");
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        var act = async () => await radio.ReadRssiDbmAsync();

        (await act.Should().ThrowAsync<TaitCcdiException>())
            .Which.Error.Describe().Should().Be("unsupported command");
    }

    [Fact]
    public async Task Prompt_Then_Trailing_Error_Still_Fails_The_Set_Command()
    {
        // Hardware-observed: rejected commands answer prompt-FIRST, then the ERROR
        // (".e03006A2\r." for an SDM the radio's programming disables).
        using var io = new FakeSerialIo();
        io.RespondTo("f0281CF", ".e03006A2\r.");
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        var act = async () => await radio.SetMonitorAsync(true);

        (await act.Should().ThrowAsync<TaitCcdiException>())
            .Which.Error.ErrorNumber.Should().Be(0x06);
    }

    [Fact]
    public async Task SetTransmitter_Completes_On_Prompt()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("f0291CE", ".");
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SetTransmitterAsync(true);

        io.WrittenAscii.Should().Contain("f0291CE\r");
    }

    [Fact]
    public async Task Dispose_Unkeys_A_Transmitter_We_Keyed()
    {
        var io = new FakeSerialIo();
        io.RespondTo("f0291CE", ".");
        var radio = TaitCcdiRadio.OpenForTest(io);
        await radio.SetTransmitterAsync(true);

        await radio.DisposeAsync();

        io.WrittenAscii.Should().EndWith("f0290CF\r");
    }

    [Fact]
    public async Task Corrupt_Lines_Are_Ignored_And_Do_Not_Break_The_Pump()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        io.Enqueue("\xEB\xF9garbage\r");
        io.RespondTo("q0450645C", ".j07064-899BE\r.");

        float rssi = await radio.ReadRssiDbmAsync();

        rssi.Should().BeApproximately(-89.9f, 0.001f);
    }

    /// <summary>Scripted <see cref="ISerialIo"/>: blocking reads against an in-memory queue,
    /// with optional canned responses keyed on written command lines.</summary>
    private sealed class FakeSerialIo : ISerialIo
    {
        private readonly BlockingCollection<byte[]> incoming = [];
        private readonly ConcurrentDictionary<string, string> responses = new();
        private readonly StringBuilder written = new();
        private readonly Lock gate = new();

        public string PortName => "fake";

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

        public void Enqueue(string ascii) => incoming.Add(Encoding.Latin1.GetBytes(ascii));

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
                Enqueue(reply);
            }
        }

        public void Dispose() => incoming.Dispose();
    }
}
