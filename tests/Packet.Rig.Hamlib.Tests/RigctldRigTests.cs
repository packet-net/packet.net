using Microsoft.Extensions.Time.Testing;
using Packet.Rig;

namespace Packet.Rig.Hamlib.Tests;

public class RigctldRigTests
{
    private static Task<RigctldRig> ConnectAsync(FakeRigctld fake, TimeProvider? time = null)
        => RigctldRig.ConnectAsync(new RigctldRigOptions
        {
            Port = fake.Port,
            TimeProvider = time ?? TimeProvider.System,
        });

    [Fact]
    public async Task Connect_Probes_Capabilities_And_Identity()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        rig.Capabilities.Should().Be(
            RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet |
            RigCapabilities.ModeGet | RigCapabilities.ModeSet |
            RigCapabilities.PttGet | RigCapabilities.PttSet |
            RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeter | RigCapabilities.RfPowerMeterWatts);
        rig.Info.Should().Be(new RigInfo("Hamlib rigctld", "Hamlib", "Dummy"));
    }

    [Fact]
    public async Task Frequency_Roundtrips()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        (await rig.GetFrequencyAsync()).Should().Be(145_000_000); // dummy rig fresh state
        await rig.SetFrequencyAsync(14_074_000);
        fake.FrequencyHz.Should().Be(14_074_000);
        (await rig.GetFrequencyAsync()).Should().Be(14_074_000);
    }

    [Fact]
    public async Task Mode_Roundtrips_With_Explicit_Passband()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        await rig.SetModeAsync(RigMode.PktUsb, 3000);
        (await rig.GetModeAsync()).Should().Be(new RigModeState(RigMode.PktUsb, 3000));
    }

    [Fact]
    public async Task Mode_Set_Without_Passband_Selects_Rig_Default_Width()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        await rig.SetModeAsync(RigMode.Cw);
        fake.PassbandHz.Should().Be(500); // the fake mirrors hamlib: wire passband 0 → mode default
        (await rig.GetModeAsync()).Should().Be(new RigModeState(RigMode.Cw, 500));
    }

    [Fact]
    public async Task Ptt_Roundtrips()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        (await rig.GetPttAsync()).Should().BeFalse();
        await rig.SetPttAsync(true);
        fake.Ptt.Should().Be(1);
        (await rig.GetPttAsync()).Should().BeTrue();
        await rig.SetPttAsync(false);
        fake.Ptt.Should().Be(0);
    }

    [Fact]
    public async Task Ptt_Treats_Mic_And_Data_Variants_As_Keyed()
    {
        await using var fake = new FakeRigctld { Ptt = 3 }; // RIG_PTT_ON_DATA
        await using var rig = await ConnectAsync(fake);

        (await rig.GetPttAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_Unkeys_A_Transmitter_We_Keyed()
    {
        await using var fake = new FakeRigctld();
        var rig = await ConnectAsync(fake);
        await rig.SetPttAsync(true);
        fake.Ptt.Should().Be(1);

        await rig.DisposeAsync();

        // The unkey is written on the socket before close; give the fake a beat to apply it.
        await WaitUntilAsync(() => fake.Ptt == 0);
        fake.Ptt.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_Does_Not_Touch_Ptt_We_Never_Keyed()
    {
        await using var fake = new FakeRigctld { Ptt = 1 }; // keyed by someone else
        var rig = await ConnectAsync(fake);
        await rig.DisposeAsync();

        fake.Ptt.Should().Be(1);
        fake.ReceivedCommands.Should().NotContain(c => c.Contains("T 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Meters_Read_Swr_And_Power()
    {
        await using var fake = new FakeRigctld();
        fake.Levels["SWR"] = 1.5;
        fake.Levels["RFPOWER_METER"] = 0.25;
        fake.Levels["RFPOWER_METER_WATTS"] = 25.0;
        await using var rig = await ConnectAsync(fake);

        (await rig.ReadSwrAsync()).Should().Be(1.5);
        (await rig.ReadRfPowerAsync()).Should().Be(0.25);
        (await rig.ReadRfPowerWattsAsync()).Should().Be(25.0);
    }

    [Fact]
    public async Task ReadLevelAsync_Reaches_Arbitrary_Levels()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        (await rig.ReadLevelAsync("STRENGTH")).Should().Be(-12);
    }

    [Fact]
    public async Task Rprt_Errors_Surface_As_RigCommandException_With_Hamlib_Name()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);
        fake.FailNextWithCode.Enqueue(11); // RIG_ENAVAIL

        var act = async () => await rig.GetFrequencyAsync();

        (await act.Should().ThrowAsync<RigCommandException>())
            .Which.Should().Match<RigCommandException>(e =>
                e.BackendErrorCode == 11 && e.Message.Contains("RIG_ENAVAIL"));
    }

    [Fact]
    public async Task Command_Errors_Do_Not_Kill_The_Connection()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);
        fake.FailNextWithCode.Enqueue(9);

        await ((Func<Task>)(async () => await rig.SetFrequencyAsync(7_074_000)))
            .Should().ThrowAsync<RigCommandException>();

        (await rig.GetFrequencyAsync()).Should().Be(145_000_000);
        fake.ConnectionCount.Should().Be(1); // same connection throughout
    }

    [Fact]
    public async Task Missing_Capability_Throws_NotSupported_Without_Touching_The_Wire()
    {
        await using var fake = new FakeRigctld
        {
            DumpCapsOverride =
            [
                "Model name:\tReceiverOnly",
                "Mfg name:\tHamlib",
                "Can get Frequency:\tY",
                "Can set Frequency:\tY",
                "Can get Mode:\tY",
                "Can set Mode:\tY",
                "Can get PTT:\tN",
                "Can set PTT:\tN",
                "Get level: STRENGTH(0..0/0)",
            ],
        };
        await using var rig = await ConnectAsync(fake);

        var commandsBefore = fake.ReceivedCommands.Count;
        var act = async () => await rig.SetPttAsync(true);
        await act.Should().ThrowAsync<NotSupportedException>();
        var swr = async () => await rig.ReadSwrAsync();
        await swr.Should().ThrowAsync<NotSupportedException>();
        fake.ReceivedCommands.Count.Should().Be(commandsBefore);
    }

    [Fact]
    public async Task Connection_Drop_Faults_The_Command_Then_Redials_On_The_Next()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);
        fake.DropBeforeNextReply = true;

        var act = async () => await rig.GetFrequencyAsync();
        await act.Should().ThrowAsync<RigConnectionException>();

        (await rig.GetFrequencyAsync()).Should().Be(145_000_000);
        fake.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task Vfo_Mode_Server_Gets_CurrVfo_Injected_On_Every_Rig_Command()
    {
        await using var fake = new FakeRigctld(vfoMode: true);
        await using var rig = await ConnectAsync(fake);

        await rig.SetFrequencyAsync(7_074_000);
        (await rig.GetFrequencyAsync()).Should().Be(7_074_000);
        await rig.SetModeAsync(RigMode.Usb, 2400);
        (await rig.GetModeAsync()).Mode.Should().Be(RigMode.Usb);
        (await rig.ReadSwrAsync()).Should().Be(1.0);

        fake.ReceivedCommands.Should().Contain("+F currVFO 7074000");
        fake.ReceivedCommands.Should().Contain("+f currVFO");
        fake.ReceivedCommands.Should().Contain("+M currVFO USB 2400");
        fake.ReceivedCommands.Should().Contain("+l currVFO SWR");
    }

    [Fact]
    public async Task Swallowed_Reply_Times_Out_And_Redials_On_The_Next_Command()
    {
        var time = new FakeTimeProvider();
        await using var fake = new FakeRigctld();
        await using var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions
        {
            Port = fake.Port,
            TimeProvider = time,
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

        fake.SwallowNextReply = true;
        var pending = rig.GetFrequencyAsync().AsTask();

        // Let the command reach the fake (which swallows it), then advance virtual time past
        // the budget — no real-time waiting.
        await WaitUntilAsync(() => fake.ReceivedCommands.Contains("+f"));
        time.Advance(TimeSpan.FromSeconds(6));

        var act = async () => await pending;
        await act.Should().ThrowAsync<RigTimeoutException>();

        (await rig.GetFrequencyAsync()).Should().Be(145_000_000);
        fake.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task TransactRawAsync_Returns_Payload_Lines()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        var payload = await rig.TransactRawAsync("m");

        payload.Should().Equal("Mode: FM", "Passband: 15000");
    }

    [Fact]
    public async Task TransactRawAsync_Rejects_Multi_Line_Commands()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        var act = async () => await rig.TransactRawAsync("f\nF 0");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Connect_Refused_Surfaces_As_RigConnectionException()
    {
        // Bind-then-close to obtain a port with nothing listening.
        int port;
        await using (var fake = new FakeRigctld())
        {
            port = fake.Port;
        }

        var act = async () => await RigctldRig.ConnectAsync(new RigctldRigOptions { Port = port });
        await act.Should().ThrowAsync<RigConnectionException>();
    }

    [Fact]
    public async Task SetFrequency_Rejects_Nonpositive()
    {
        await using var fake = new FakeRigctld();
        await using var rig = await ConnectAsync(fake);

        var act = async () => await rig.SetFrequencyAsync(0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // Bounded poll for cross-task visibility — not a timing dependency: the condition is
        // ordinarily already true on the first check.
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
