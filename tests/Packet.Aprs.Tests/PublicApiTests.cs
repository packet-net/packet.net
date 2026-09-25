using System.Runtime.CompilerServices;
using PublicApiGenerator;

namespace Packet.Aprs.Tests;

/// <summary>
/// Locks the library's public API surface. <see cref="PublicApiGenerator"/> renders every public
/// type and member of the <c>Packet.Aprs</c> assembly to text; the test asserts it matches the
/// committed <c>PublicApi.approved.txt</c> snapshot. Any change to the public surface fails the
/// build until the snapshot is regenerated, so the change is a reviewed diff.
/// </summary>
public class PublicApiTests
{
    [Fact]
    public void Public_api_surface_matches_the_approved_snapshot()
    {
        string actual = Normalise(typeof(AprsPacket).Assembly.GeneratePublicApi(
            new ApiGeneratorOptions { IncludeAssemblyAttributes = false }));

        string approvedPath = SnapshotPath("PublicApi.approved.txt");
        string approved = File.Exists(approvedPath)
            ? Normalise(File.ReadAllText(approvedPath))
            : string.Empty;

        if (actual != approved)
        {
            File.WriteAllText(SnapshotPath("PublicApi.received.txt"), actual);
        }

        actual.Should().Be(
            approved,
            "the public API must match PublicApi.approved.txt; if this change is intended, "
            + "copy PublicApi.received.txt over PublicApi.approved.txt");
    }

    private static string Normalise(string api) => api.ReplaceLineEndings("\n").TrimEnd() + "\n";

    private static string SnapshotPath(string fileName, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, fileName);
}
