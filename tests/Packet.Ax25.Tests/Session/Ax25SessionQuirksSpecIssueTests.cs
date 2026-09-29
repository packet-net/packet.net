using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Every deviation from the figures is on record against the spec, in the code: a quirk
/// named after a <c>packethacking/ax25spec</c> issue carries <see cref="Ax25SpecIssueAttribute"/>
/// with the same number, and nothing else carries one. Names, attributes and the audit doc
/// then cannot drift apart without this failing.
/// </summary>
public sealed class Ax25SessionQuirksSpecIssueTests
{
    private static readonly Regex Named = new(@"^Ax25Spec(\d+)[A-Z]", RegexOptions.Compiled);

    [Fact]
    public void Every_quirk_named_after_a_spec_issue_carries_the_matching_attribute()
    {
        foreach (var p in typeof(Ax25SessionQuirks).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            var m = Named.Match(p.Name);
            var attr = (Ax25SpecIssueAttribute?)Attribute.GetCustomAttribute(p, typeof(Ax25SpecIssueAttribute));
            if (m.Success)
            {
                attr.Should().NotBeNull($"{p.Name} is named after an ax25spec issue and must reference it in code");
                attr!.Issue.Should().Be(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), $"{p.Name}'s attribute must name the issue in its name");
            }
            else
            {
                attr.Should().BeNull($"{p.Name} carries a spec-issue reference but is not named after one");
            }
        }
    }

    [Fact]
    public void The_spec_issue_map_lists_every_named_quirk()
    {
        var named = typeof(Ax25SessionQuirks).GetProperties().Where(p => Named.IsMatch(p.Name)).Select(p => p.Name).ToList();
        Ax25SessionQuirks.SpecIssues.Keys.Should().BeEquivalentTo(named);
        Ax25SessionQuirks.SpecIssues["Ax25Spec114UnexpectedUaIgnored"].Url.Should().Be("https://github.com/packethacking/ax25spec/issues/114");
        Ax25SessionQuirks.SpecIssues["Ax25Spec114UnexpectedUaIgnored"].RemovalTrackedIn.Should().Be(880);
        Ax25SessionQuirks.SpecIssues["Ax25Spec50RepeatedConnectSabmReacknowledged"].RemovalTrackedIn.Should().Be(881);
    }

    [Fact]
    public void The_audit_doc_cites_the_issue_the_code_references_for_every_quirk_it_documents()
    {
        // The audit rows are the human record; the attribute is the code's. Where the audit
        // doc documents a quirk, it must cite the same issue the attribute does. (The
        // figure-defect quirks are documented in their XML docs and plan entries rather than
        // audit rows, so a quirk absent from the audit doc is not a failure.)
        var root = FindRepoRoot();
        var audit = File.ReadAllText(Path.Combine(root, "docs", "strict-vs-pragmatic-audit.md"));
        foreach (var (name, attr) in Ax25SessionQuirks.SpecIssues.Where(kv => audit.Contains(kv.Key, StringComparison.Ordinal)))
        {
            var cited = audit.Contains($"ax25spec#{attr.Issue}", StringComparison.Ordinal)
                || audit.Contains($"ax25spec/issues/{attr.Issue}", StringComparison.Ordinal);
            cited.Should().BeTrue($"the audit doc documents {name} and must cite ax25spec#{attr.Issue}, the issue its attribute names");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packet.NET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
