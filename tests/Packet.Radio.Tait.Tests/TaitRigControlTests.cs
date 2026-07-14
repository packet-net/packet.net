using Packet.Radio.Tait.Ccdi;
using Packet.Rig;

namespace Packet.Radio.Tait.Tests;

/// <summary>
/// The station-control (<see cref="IRigControl"/>) view of the CCDI radio, over the same
/// scripted <see cref="FakeSerialIo"/> the driver's own tests use — wire commands and replies
/// are built with the real codec so no checksum is ever guessed.
/// </summary>
public class TaitRigControlTests
{
    private static string Frame(char ident, string parameters) => new CcdiFrame(ident, parameters).Encode();

    /// <summary>One prompt-disciplined reply: prompt, message frame, CR, prompt.</summary>
    private static string Answer(string frame) => $".{frame}\r.";

    private static FakeSerialIo NewIoAnsweringIdentity()
    {
        var io = new FakeSerialIo();
        io.RespondTo(Frame('q', ""), Answer(Frame('m', "13203.02")));   // MODEL: TM8110, CCDI 03.02
        io.RespondTo(Frame('q', "4"), Answer(Frame('n', "12345678"))); // RADIO_SERIAL
        io.RespondTo(Frame('q', "3"), Answer(Frame('v', "00TMAA40"))); // RADIO_VERSIONS record 00
        return io;
    }

    private static TaitCcdiRadio NewRadio(FakeSerialIo io, TimeSpan? transactionTimeout = null)
        => TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions
        {
            KeepAliveInterval = null,
            TransactionTimeout = transactionTimeout ?? TimeSpan.FromSeconds(2),
        });

    [Fact]
    public async Task CreateAsync_Queries_Identity_And_Advertises_The_Honest_Capability_Slice()
    {
        using var io = NewIoAnsweringIdentity();
        await using var radio = NewRadio(io);

        await using var rig = await TaitRigControl.CreateAsync(radio);

        rig.Info.Should().Be(new RigInfo("Tait CCDI", "Tait", "Tait TM8110"));
        rig.Capabilities.Should().Be(
            RigCapabilities.PttGet | RigCapabilities.PttSet | RigCapabilities.RfPowerMeter);
    }

    [Fact]
    public async Task Create_With_Known_Identity_Sends_No_Wire_Traffic()
    {
        using var io = new FakeSerialIo();
        await using var radio = NewRadio(io);
        var identity = new TaitRadioIdentity('1', '3', '2', "03.02", "12345678",
            new Dictionary<string, string>());

        await using var rig = TaitRigControl.Create(radio, identity);

        rig.Info.Model.Should().Be("Tait TM8110");
        io.WrittenAscii.Should().BeEmpty();
    }

    [Fact]
    public async Task Ptt_Keys_Unkeys_And_Tracks_Last_Known_State()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('f', "91"), ".");
        io.RespondTo(Frame('f', "90"), ".");
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);

        (await rig.GetPttAsync()).Should().BeFalse();

        await rig.SetPttAsync(true);
        io.WrittenAscii.Should().Contain(Frame('f', "91") + "\r");
        (await rig.GetPttAsync()).Should().BeTrue();

        await rig.SetPttAsync(false);
        io.WrittenAscii.Should().Contain(Frame('f', "90") + "\r");
        (await rig.GetPttAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task GetPtt_Observes_External_Keying_Via_Progress_Edges()
    {
        using var io = NewIoAnsweringIdentity();
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);
        var seen = new SemaphoreSlim(0);
        radio.TransmitterStateChanged += (_, _) => seen.Release();

        io.Enqueue(Answer(Frame('p', "07"))); // PTT_ACTIVATED — the fist mic, not us
        (await seen.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        (await rig.GetPttAsync()).Should().BeTrue();

        io.Enqueue(Answer(Frame('p', "08"))); // PTT_DEACTIVATED
        (await seen.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        (await rig.GetPttAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ReadRfPower_Scales_The_Forward_Detector_Over_Its_Full_Scale()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('q', "5318"), Answer(Frame('j', "318600")));
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);

        (await rig.ReadRfPowerAsync()).Should().BeApproximately(0.5, 0.001); // 600 of 1200 mV
    }

    [Fact]
    public async Task ReadRfPower_Clamps_Detector_Readings_Beyond_Full_Scale()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('q', "5318"), Answer(Frame('j', "3181300")));
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);

        (await rig.ReadRfPowerAsync()).Should().Be(1.0);
    }

    [Fact]
    public async Task Radio_Errors_Map_To_RigCommandException_With_The_Ccdi_Error_Number()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('q', "5318"), Answer(Frame('e', "t05")));
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);

        var act = async () => await rig.ReadRfPowerAsync();

        (await act.Should().ThrowAsync<RigCommandException>())
            .Which.BackendErrorCode.Should().Be(0x05);
    }

    [Fact]
    public async Task Silent_Radio_Maps_To_RigTimeoutException()
    {
        using var io = NewIoAnsweringIdentity();
        // No response scripted for CCTM 318 — the driver's transaction deadline fires.
        await using var radio = NewRadio(io, transactionTimeout: TimeSpan.FromMilliseconds(200));
        await using var rig = await TaitRigControl.CreateAsync(radio);

        var act = async () => await rig.ReadRfPowerAsync();

        await act.Should().ThrowAsync<RigTimeoutException>();
    }

    [Fact]
    public async Task Frequency_Mode_Swr_And_Watts_Are_Honestly_Unsupported()
    {
        using var io = NewIoAnsweringIdentity();
        await using var radio = NewRadio(io);
        await using var rig = await TaitRigControl.CreateAsync(radio);

        await AssertNotSupported(async () => await rig.GetFrequencyAsync());
        await AssertNotSupported(async () => await rig.SetFrequencyAsync(145_000_000));
        await AssertNotSupported(async () => await rig.GetModeAsync());
        await AssertNotSupported(async () => await rig.SetModeAsync(RigMode.Fm));
        await AssertNotSupported(async () => await rig.ReadSwrAsync());
        await AssertNotSupported(async () => await rig.ReadRfPowerWattsAsync());

        static async Task AssertNotSupported(Func<Task> act)
            => await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Dispose_Unkeys_A_Transmitter_This_Adapter_Keyed_When_It_Does_Not_Own_The_Radio()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('f', "91"), ".");
        io.RespondTo(Frame('f', "90"), ".");
        await using var radio = NewRadio(io);
        var rig = await TaitRigControl.CreateAsync(radio, ownsRadio: false);
        await rig.SetPttAsync(true);

        await rig.DisposeAsync();

        io.WrittenAscii.Should().Contain(Frame('f', "90") + "\r");

        // The radio object survives the adapter — the port supervisor still owns it.
        var act = async () => await rig.GetPttAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_With_Owned_Radio_Disposes_It_And_The_Radio_Unkeys_Itself()
    {
        using var io = NewIoAnsweringIdentity();
        io.RespondTo(Frame('f', "91"), ".");
        io.RespondTo(Frame('f', "90"), ".");
        var radio = NewRadio(io);
        var rig = await TaitRigControl.CreateAsync(radio, ownsRadio: true);
        await rig.SetPttAsync(true);

        await rig.DisposeAsync();

        // The radio's own dispose-unkey ran (it tracked that CCDI keyed the transmitter).
        io.WrittenAscii.Should().Contain(Frame('f', "90") + "\r");
    }
}
