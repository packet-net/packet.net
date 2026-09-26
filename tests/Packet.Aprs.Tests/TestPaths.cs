namespace Packet.Aprs.Tests;

/// <summary>
/// Locates this project's snapshot files in the source tree. Walks up from the test assembly
/// rather than using <c>[CallerFilePath]</c>, because CI builds with deterministic source paths
/// (<c>ContinuousIntegrationBuild</c>), which rewrite that path to <c>/_/...</c>.
/// </summary>
internal static class TestPaths
{
    public static string InProject(params string[] parts)
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string project = Path.Combine(dir, "tests", "Packet.Aprs.Tests");
            if (File.Exists(Path.Combine(project, "Packet.Aprs.Tests.csproj")))
            {
                return Path.Combine([project, .. parts]);
            }
        }

        throw new InvalidOperationException("Could not locate tests/Packet.Aprs.Tests above the test assembly.");
    }
}
