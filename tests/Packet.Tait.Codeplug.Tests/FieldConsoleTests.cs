using AwesomeAssertions;
using Packet.Tait.Codeplug;
using Xunit;

namespace Packet.Tait.Codeplug.Tests;

public class FieldConsoleTests
{
    private static CodeplugFields Fields()
    {
        var image = new CodeplugImage(
            new[] { new KeyValuePair<string, string>("DBVer", "0095") },
            new[]
            {
                new CodeplugRecord(0x05, 0, new byte[23]),
                new CodeplugRecord(0x09, 0, new byte[37]),
                new CodeplugRecord(0x3B, 0, new byte[16]),
            });
        return CodeplugFields.Open(image);
    }

    [Fact]
    public void Set_and_get_a_channel_field_by_name()
    {
        CodeplugFields f = Fields();
        FieldConsole.Set(f, "ch0.bandwidth", "Wide");
        FieldConsole.Get(f, "ch0.bandwidth").Should().Be("Wide");

        FieldConsole.Set(f, "ch0.txfreq", "146900000");
        FieldConsole.Get(f, "ch0.txfreq").Should().Be("146900000");
    }

    [Fact]
    public void Set_and_get_global_fields_by_name()
    {
        CodeplugFields f = Fields();
        FieldConsole.Set(f, "sdm", "on");
        FieldConsole.Get(f, "sdm").Should().Be("true");

        FieldConsole.Set(f, "rxtap", "R2");
        FieldConsole.Get(f, "rxtap").Should().Be("R2");

        FieldConsole.Set(f, "txtap", "13"); // bare number also accepted
        FieldConsole.Get(f, "txtap").Should().Be("T13");

        FieldConsole.Set(f, "dataport", "aux"); // case-insensitive enum
        FieldConsole.Get(f, "dataport").Should().Be("Aux");
    }

    [Fact]
    public void Describe_lists_every_field()
    {
        var names = FieldConsole.Describe(Fields()).Select(r => r.Name).ToList();
        names.Should().Contain(new[] { "channels", "ch0.bandwidth", "ch0.power", "sdm", "transparent", "rxtap", "txtap", "tapunmute" });
    }

    [Fact]
    public void Unknown_field_and_bad_value_throw()
    {
        CodeplugFields f = Fields();
        ((Action)(() => FieldConsole.Get(f, "nope"))).Should().Throw<FormatException>();
        ((Action)(() => FieldConsole.Set(f, "sdm", "maybe"))).Should().Throw<FormatException>();
        ((Action)(() => FieldConsole.Set(f, "ch0.bandwidth", "Enormous"))).Should().Throw<FormatException>();
    }
}
