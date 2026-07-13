using Microsoft.Extensions.Time.Testing;
using Packet.Rig;

namespace Packet.Rig.Flrig.Tests;

public class FlrigRigTests
{
    private static Task<FlrigRig> ConnectAsync(HttpMessageHandler handler, TimeProvider? time = null)
        => FlrigRig.ConnectAsync(
            new FlrigRigOptions { TimeProvider = time ?? TimeProvider.System },
            handler,
            CancellationToken.None);

    [Fact]
    public async Task Connect_Probes_Version_Identity_Scale_And_Modes()
    {
        using var handler = new FakeFlrigHandler { Xcvr = "TS-590SG", PwrMeterScale = 2.0 };
        await using var rig = await ConnectAsync(handler);

        rig.FlrigVersion.Should().Be("2.0.05");
        rig.Info.Should().Be(new RigInfo("flrig", null, "TS-590SG"));
        rig.SupportedModes.Should().Contain(["USB", "DATA-U"]);
        rig.Capabilities.Should().HaveFlag(RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet);
        rig.Capabilities.Should().HaveFlag(RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeterWatts);
    }

    [Fact]
    public async Task Frequency_Gets_A_String_And_Sets_A_Double()
    {
        using var handler = new FakeFlrigHandler { FrequencyHz = 7_074_000 };
        await using var rig = await ConnectAsync(handler);

        (await rig.GetFrequencyAsync()).Should().Be(7_074_000);

        await rig.SetFrequencyAsync(14_074_000);
        handler.FrequencyHz.Should().Be(14_074_000);

        // flrig's asymmetric typing on the wire: set must be an XML-RPC <double>.
        var setCall = handler.Calls.Single(c => c.Method == "main.set_frequency");
        setCall.Body.Should().Contain("<double>14074000</double>");
    }

    [Fact]
    public async Task Mode_Roundtrips_Using_The_Rig_Native_Vocabulary()
    {
        using var handler = new FakeFlrigHandler();
        await using var rig = await ConnectAsync(handler);

        await rig.SetModeAsync(RigMode.From("DATA-U"));
        handler.Mode.Should().Be("DATA-U");
        (await rig.GetModeAsync()).Should().Be(new RigModeState(RigMode.From("DATA-U"), null));
    }

    [Fact]
    public async Task Mode_Outside_The_Rig_Table_Throws_Instead_Of_Being_Silently_Dropped()
    {
        using var handler = new FakeFlrigHandler(); // table has no PKTUSB spelling
        await using var rig = await ConnectAsync(handler);

        var act = async () => await rig.SetModeAsync(RigMode.PktUsb);
        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("DATA-U"); // the message teaches the valid table

        handler.Mode.Should().Be("USB"); // untouched
    }

    [Fact]
    public async Task Mode_Set_With_Passband_Is_Not_Supported_On_Flrig()
    {
        using var handler = new FakeFlrigHandler();
        await using var rig = await ConnectAsync(handler);

        var act = async () => await rig.SetModeAsync(RigMode.Usb, 2400);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Ptt_Roundtrips()
    {
        using var handler = new FakeFlrigHandler();
        await using var rig = await ConnectAsync(handler);

        (await rig.GetPttAsync()).Should().BeFalse();
        await rig.SetPttAsync(true);
        handler.Ptt.Should().Be(1);
        (await rig.GetPttAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_Unkeys_A_Transmitter_We_Keyed()
    {
        using var handler = new FakeFlrigHandler();
        var rig = await ConnectAsync(handler);
        await rig.SetPttAsync(true);

        await rig.DisposeAsync();

        handler.Ptt.Should().Be(0);
    }

    [Fact]
    public async Task Swr_Prefers_The_Direct_Method()
    {
        using var handler = new FakeFlrigHandler { SwrDirect = 1.7 };
        await using var rig = await ConnectAsync(handler);

        (await rig.ReadSwrAsync()).Should().Be(1.7);
        handler.Calls.Should().NotContain(c => c.Method == "rig.get_swrmeter");
    }

    [Fact]
    public async Task Swr_Falls_Back_To_Meter_Interpolation_And_Remembers()
    {
        using var handler = new FakeFlrigHandler { SwrDirect = null, SwrMeter = 23 };
        await using var rig = await ConnectAsync(handler);

        (await rig.ReadSwrAsync()).Should().Be(2.0); // 23% deflection → 2.0:1 per hamlib's table
        (await rig.ReadSwrAsync()).Should().Be(2.0);

        // get_SWR probed exactly once, then the fallback is sticky.
        handler.Calls.Count(c => c.Method == "rig.get_SWR").Should().Be(1);
        handler.Calls.Count(c => c.Method == "rig.get_swrmeter").Should().Be(2);
    }

    [Fact]
    public async Task Power_Meters_Apply_The_Flrig_Scale_Contract()
    {
        using var handler = new FakeFlrigHandler { PwrMeter = 50, PwrMeterScale = 2.0 };
        await using var rig = await ConnectAsync(handler);

        // hamlib flrig.c: relative = deflection/100 × scale; watts = deflection × scale.
        (await rig.ReadRfPowerAsync()).Should().Be(1.0);
        (await rig.ReadRfPowerWattsAsync()).Should().Be(100.0);
    }

    [Fact]
    public async Task Missing_Scale_Method_Defaults_To_One()
    {
        using var handler = new FakeFlrigHandler { PwrMeter = 40 };
        handler.Faults["rig.get_pwrmeter_scale"] = (-1, "unknown method name");
        await using var rig = await ConnectAsync(handler);

        (await rig.ReadRfPowerWattsAsync()).Should().Be(40.0);
    }

    [Fact]
    public async Task Faults_Surface_As_RigCommandException()
    {
        using var handler = new FakeFlrigHandler();
        await using var rig = await ConnectAsync(handler);
        handler.Faults["rig.get_vfo"] = (-32601, "server error. method not found");

        var act = async () => await rig.GetFrequencyAsync();
        (await act.Should().ThrowAsync<RigCommandException>())
            .Which.Should().Match<RigCommandException>(e =>
                e.BackendErrorCode == -32601 && e.Message.Contains("method not found"));
    }

    [Fact]
    public async Task Unreachable_Flrig_Surfaces_As_RigConnectionException()
    {
        using var handler = new ThrowingHandler();
        var act = async () => await ConnectAsync(handler);
        await act.Should().ThrowAsync<RigConnectionException>();
    }

    [Fact]
    public async Task Hung_Flrig_Times_Out_Via_The_Injected_Clock()
    {
        var time = new FakeTimeProvider();
        using var handler = new FakeFlrigHandler();
        await using var rig = await FlrigRig.ConnectAsync(
            new FlrigRigOptions { TimeProvider = time, CommandTimeout = TimeSpan.FromSeconds(5) },
            handler,
            CancellationToken.None);

        handler.HangNextRequest = true;
        var pending = rig.GetFrequencyAsync().AsTask();
        while (handler.HangNextRequest)
        {
            await Task.Yield(); // let the request reach the handler
        }

        time.Advance(TimeSpan.FromSeconds(6));

        var act = async () => await pending;
        await act.Should().ThrowAsync<RigTimeoutException>();
    }

    [Fact]
    public async Task CallRawAsync_Reaches_Unmapped_Methods()
    {
        using var handler = new FakeFlrigHandler();
        await using var rig = await ConnectAsync(handler);

        (await rig.CallRawAsync("rig.get_mode")).Should().Be("USB");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
