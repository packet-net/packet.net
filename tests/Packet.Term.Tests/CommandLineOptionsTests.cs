using CommandLine;
using Packet.Term;

namespace Packet.Term.Tests;

/// <summary>
/// Smoke tests that the CommandLineParser bindings actually land the values
/// where the rest of the app reads them.
/// </summary>
public class CommandLineOptionsTests
{
    [Fact]
    public void Parses_All_Three_Flags()
    {
        var result = Parser.Default.ParseArguments<CommandLineOptions>(new[]
        {
            "--mycall", "M0LTE-1",
            "--port", "/dev/ttyUSB0",
            "--connect", "G1AAA",
        });

        var parsed = (Parsed<CommandLineOptions>)result;
        parsed.Value.MyCall.Should().Be("M0LTE-1");
        parsed.Value.Port.Should().Be("/dev/ttyUSB0");
        parsed.Value.Connect.Should().Be("G1AAA");
    }

    [Fact]
    public void Parses_Only_Mycall_And_Port()
    {
        var result = Parser.Default.ParseArguments<CommandLineOptions>(new[]
        {
            "--mycall", "M0LTE-1",
            "--port", "/dev/ttyUSB0",
        });

        var parsed = (Parsed<CommandLineOptions>)result;
        parsed.Value.MyCall.Should().Be("M0LTE-1");
        parsed.Value.Port.Should().Be("/dev/ttyUSB0");
        parsed.Value.Connect.Should().BeNull();
    }

    [Fact]
    public void Parses_Empty_Args()
    {
        var result = Parser.Default.ParseArguments<CommandLineOptions>(Array.Empty<string>());
        var parsed = (Parsed<CommandLineOptions>)result;
        parsed.Value.MyCall.Should().BeNull();
        parsed.Value.Port.Should().BeNull();
        parsed.Value.Connect.Should().BeNull();
    }
}
