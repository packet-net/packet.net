using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.IR;

/// <summary>
/// YAML loaders for <c>*.sdl.yaml</c> pages. State-machine pages and
/// subroutine pages share the extension; <see cref="IsSubroutinePage"/>
/// distinguishes by scanning for a top-level key.
/// </summary>
public static class Loader
{
    /// <summary>Load one state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / etc.).</summary>
    public static SdlPage LoadPage(string path)
    {
        var deserializer = NewDeserializer();
        var page = deserializer.Deserialize<SdlPage>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"could not parse {path}");
        page.SourcePath = path;
        page.Transitions ??= new();
        return page;
    }

    /// <summary>Load one subroutine page (figc4.7).</summary>
    public static SubroutinePage LoadSubroutinePage(string path)
    {
        var deserializer = NewDeserializer();
        var page = deserializer.Deserialize<SubroutinePage>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"could not parse {path}");
        page.SourcePath = path;
        return page;
    }

    /// <summary>
    /// True if <paramref name="path"/> is a subroutine page (figc4.7 style)
    /// rather than a state-machine page. Detected by a line-prefix scan
    /// for <c>subroutines:</c> before any <c>state:</c> / <c>transitions:</c>.
    /// </summary>
    public static bool IsSubroutinePage(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("subroutines:", StringComparison.Ordinal)) return true;
            if (line.StartsWith("state:",       StringComparison.Ordinal)) return false;
            if (line.StartsWith("transitions:", StringComparison.Ordinal)) return false;
        }
        return false;
    }

    private static IDeserializer NewDeserializer()
        => new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
}
