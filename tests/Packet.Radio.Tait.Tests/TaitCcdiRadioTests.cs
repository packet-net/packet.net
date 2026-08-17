using Microsoft.Extensions.Time.Testing;
using Packet.Radio;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait.Tests;

public class TaitCcdiRadioTests
{
    // The exact CCDI wire commands, computed by the real codec so the checksum is never guessed.
    private static string EnterTransparent() => new CcdiFrame('t', "+0").Encode();
    private static string ModelQuery() => new CcdiFrame('q', "").Encode();

    /// <summary>Options with both watchdog duties off, so nothing but the test writes to the
    /// wire (the fake-clock tests advance time far enough to trip them otherwise).</summary>
    private static TaitCcdiRadioOptions Quiet() =>
        new() { KeepAliveInterval = null, StaleBusyRevalidateAfter = null };

    /// <summary>Walk <paramref name="clock"/> forward in fake-time steps until
    /// <paramref name="inflight"/> settles, so a virtualised deadline fires without a real wait.</summary>
    private static async Task AdvanceUntilSettledAsync(FakeTimeProvider clock, Task inflight)
    {
        for (int i = 0; i < 40 && !inflight.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(5);
        }

        inflight.IsCompleted.Should().BeTrue("the operation's deadline must run on the injected clock");
    }

    [Fact]
    public async Task EscapeAndVerify_Recovers_When_A_Model_Query_Answers()
    {
        using var io = new FakeSerialIo();
        io.RespondTo(EnterTransparent(), ".");                     // enter Transparent OK
        io.RespondTo(ModelQuery(), ".m0813203.02A2\r.");           // a MODEL query answers → Command mode back
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions { KeepAliveInterval = null });
        await radio.EnterTransparentModeAsync();
        radio.Mode.Should().Be(TaitProtocolMode.Transparent);

        bool recovered = await radio.EscapeAndVerifyTransparentAsync(
            attempts: 2, guardTime: TimeSpan.FromMilliseconds(10), verifyTimeout: TimeSpan.FromMilliseconds(500));

        recovered.Should().BeTrue();
        radio.Mode.Should().Be(TaitProtocolMode.Command);
        io.WrittenAscii.Should().Contain("+++", "the escape sequence must go out on the byte pipe");
    }

    [Fact]
    public async Task EscapeAndVerify_Reports_Wedged_When_No_Model_Reply_Arrives()
    {
        using var io = new FakeSerialIo();
        io.RespondTo(EnterTransparent(), ".");                     // enter Transparent OK
        // No response to the MODEL query: the radio ignores the escape (Ignore-Escape ON) and stays
        // a Transparent byte pipe, so the confirming query never answers → wedged.
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions { KeepAliveInterval = null });
        await radio.EnterTransparentModeAsync();

        bool recovered = await radio.EscapeAndVerifyTransparentAsync(
            attempts: 2, guardTime: TimeSpan.FromMilliseconds(10), verifyTimeout: TimeSpan.FromMilliseconds(150));

        recovered.Should().BeFalse();
        io.WrittenAscii.Should().Contain("+++", "the escape must be attempted before declaring the radio wedged");
    }

    [Fact]
    public async Task EscapeAndVerify_Throws_When_Not_In_Transparent_Mode()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions { KeepAliveInterval = null });

        var act = async () => await radio.EscapeAndVerifyTransparentAsync(
            guardTime: TimeSpan.FromMilliseconds(10), verifyTimeout: TimeSpan.FromMilliseconds(150));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

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
    public async Task Dispose_Unkeys_A_Transmitter_Whose_Key_Command_Timed_Out()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeSerialIo();
        // Nothing answers the key: the radio takes the 'f91' bytes and never prompts, so the
        // command fails with the transmitter quite possibly keyed (#698).
        var radio = TaitCcdiRadio.OpenForTest(io, Quiet() with { TransactionTimeout = TimeSpan.FromSeconds(2) }, clock);

        var keying = radio.SetTransmitterAsync(true).AsTask();
        await AdvanceUntilSettledAsync(clock, keying);
        var act = () => keying;
        await act.Should().ThrowAsync<TimeoutException>();

        await radio.DisposeAsync();

        io.WrittenAscii.Should().Contain("f0291CE\r", "the key bytes reach the radio before the deadline can expire");
        io.WrittenAscii.Should().EndWith(
            "f0290CF\r",
            "FUNCTION 9 keying never expires by itself, so a key command that failed after writing " +
            "its bytes must still be unkeyed on the way out");
    }

    [Fact]
    public async Task EscapeAndVerify_Bounds_Its_Verify_Wait_On_The_Injected_Clock()
    {
        var clock = new FakeTimeProvider();
        using var io = new FakeSerialIo();
        io.RespondTo(EnterTransparent(), ".");
        // No MODEL answer, and a transaction deadline far beyond the verify budget: only the
        // verify budget can end the attempt, and only if it runs on the injected clock.
        await using var radio = TaitCcdiRadio.OpenForTest(
            io,
            Quiet() with { PromptErrorGrace = TimeSpan.Zero, TransactionTimeout = TimeSpan.FromMinutes(10) },
            clock);
        await radio.EnterTransparentModeAsync();

        var escaping = radio.EscapeAndVerifyTransparentAsync(
            attempts: 1, guardTime: TimeSpan.FromSeconds(2), verifyTimeout: TimeSpan.FromSeconds(30));
        await AdvanceUntilSettledAsync(clock, escaping);

        (await escaping).Should().BeFalse("an unanswered MODEL query means the radio is wedged in Transparent");
        io.WrittenAscii.Should().Contain("+++", "the escape must be attempted before declaring the radio wedged");
    }

    [Fact]
    public async Task TransactRaw_Refuses_Parameters_Longer_Than_A_Two_Digit_Size()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io, Quiet());

        var act = async () => await radio.TransactRawAsync('q', new string('A', 256));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*two hex digits*");
        io.WrittenAscii.Should().BeEmpty("a frame no CCDI receiver could parse must never reach the wire");
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData('\u0011')]
    [InlineData('\u0013')]
    public async Task TransactRaw_Refuses_Parameters_That_Would_Corrupt_Line_Framing(char forbidden)
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io, Quiet());

        var act = async () => await radio.TransactRawAsync('q', $"AB{forbidden}CD");

        await act.Should().ThrowAsync<ArgumentException>();
        io.WrittenAscii.Should().BeEmpty("the modelled commands refuse these bytes; the escape hatch must too");
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
}
