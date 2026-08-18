using System.Text.RegularExpressions;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// Enforced separation guardrails for the app platform: the node host and core share <b>no</b>
/// compile-time link with any application, and don't hardcode a specific app (WALL) into their
/// source. WALL being a separate-language (Python) out-of-process program already makes a code
/// link impossible; these tests forward-proof that - so a future contributor can't quietly add
/// a project reference to an in-repo app or bake an app's path into the node. The separation is
/// the proof of the seam, so it is asserted, not merely intended.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppPlatformArchitectureTests
{
    private static readonly string[] PdnProjectDirs = ["src/Packet.Node.Core", "src/Packet.Node"];
    private static readonly string[] PdnCsproj =
        ["src/Packet.Node.Core/Packet.Node.Core.csproj", "src/Packet.Node/Packet.Node.csproj"];

    [Fact]
    public void Pdn_node_projects_reference_no_application_project()
    {
        var root = RepoRoot();
        var rx = new Regex("ProjectReference\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        foreach (var rel in PdnCsproj)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            foreach (Match m in rx.Matches(text))
            {
                var inc = m.Groups[1].Value.Replace('\\', '/').ToLowerInvariant();
                (inc.Contains("/examples/") || inc.Contains("/apps/") || inc.Contains("wall"))
                    .Should().BeFalse(
                        $"{rel} must not reference an application project, but references '{m.Groups[1].Value}'. " +
                        "Apps run out-of-process; the node never links app code.");
            }
        }
    }

    [Fact]
    public void Pdn_node_source_does_not_hardcode_the_wall_app()
    {
        var root = RepoRoot();
        // Unambiguous references to the WALL app specifically (its script / its example dir).
        // Deliberately NOT a bare "wall" match - the repo's §2.7 "no wall-clock" convention is
        // pervasive and unrelated; these tokens only ever mean "the WALL app".
        var forbidden = new[] { "wall.py", "examples/wall", "examples\\wall" };
        var offenders = new List<string>();

        foreach (var relDir in PdnProjectDirs)
        {
            var dir = Path.Combine(root, relDir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGenerated(file))
                {
                    continue;   // skip bin/obj generated sources
                }
                var text = File.ReadAllText(file);
                foreach (var token in forbidden)
                {
                    if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{Path.GetFileName(file)} contains '{token}'");
                    }
                }
            }
        }

        (offenders.Count == 0).Should().BeTrue(
            "The node must not hardcode the WALL app — it's an external, separately-deployed " +
            "program discovered only via the applications: config. Offenders: " + string.Join("; ", offenders));
    }

    private static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/") || p.Contains("/bin/");
    }

    // Walk up from the test assembly's location to the repo root (the dir that contains src/).
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Packet.Node.Core")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate the repo root (no src/Packet.Node.Core above the test assembly).");
    }
}
