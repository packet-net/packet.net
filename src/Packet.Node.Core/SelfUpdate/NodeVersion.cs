using System.Globalization;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// A node version for the self-update available-version check — a dotted numeric release
/// (<c>MAJOR.MINOR.PATCH</c>, extra trailing components tolerated) plus a flag for whether the
/// running build carries SemVer <em>build metadata</em> (the <c>+…</c> suffix, e.g.
/// <c>0.1.0+dev20260614…</c> or <c>0.1.0+&lt;sha&gt;</c>).
/// </summary>
/// <remarks>
/// <para>The one rule that matters for "is an update available?": a <b>dev/local build sorts
/// <em>above</em> the release of the same base version</b>. A node running <c>0.1.0+dev…</c> is
/// (by construction) built from a tree at or ahead of the <c>0.1.0</c> release, so offering it
/// the <c>0.1.0</c> release would be a <em>downgrade</em>, not an update. So an update is offered
/// only when the candidate's numeric base is <b>strictly greater</b> than the running base; an
/// equal base is "up to date" regardless of build metadata (per docs/node-self-update-design.md
/// § Channel = github).</para>
/// <para>Build metadata is otherwise opaque (SemVer §10: it does not participate in ordering).
/// We do not parse pre-release tags (<c>-rc.1</c>) — node releases are plain <c>node-vX.Y.Z</c>;
/// any <c>-suffix</c> on the numeric part is simply ignored for comparison, conservatively.</para>
/// </remarks>
public readonly record struct NodeVersion : IComparable<NodeVersion>
{
    private readonly int[] components;

    /// <summary>The numeric release components (e.g. <c>[0, 1, 0]</c>). Never null.</summary>
    public IReadOnlyList<int> Components => components;

    /// <summary>Whether the source string carried SemVer build metadata (a <c>+…</c> suffix) —
    /// the marker of a dev/local/CI build that sorts above the matching release.</summary>
    public bool HasBuildMetadata { get; }

    private NodeVersion(int[] components, bool hasBuildMetadata)
    {
        this.components = components;
        HasBuildMetadata = hasBuildMetadata;
    }

    /// <summary>
    /// Parse a version string. Accepts a leading <c>v</c> / <c>node-v</c> (release-tag spellings),
    /// a SemVer build-metadata suffix (<c>+…</c>, recorded in <see cref="HasBuildMetadata"/>), and
    /// a SemVer pre-release suffix (<c>-…</c>, ignored). Returns <c>false</c> rather than throwing
    /// on anything it can't read — the available-version check must never throw.
    /// </summary>
    public static bool TryParse(string? text, out NodeVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        // Tolerate the release-tag spellings: `node-v0.1.0`, `v0.1.0`.
        if (s.StartsWith("node-v", StringComparison.OrdinalIgnoreCase))
        {
            s = s["node-v".Length..];
        }
        else if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // SemVer build metadata (`+…`) — the dev/local marker. Strip + record it.
        bool hasBuild = false;
        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            hasBuild = true;
            s = s[..plus];
        }

        // SemVer pre-release (`-…`) — ignored for ordering (node releases are plain X.Y.Z).
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            s = s[..dash];
        }

        if (s.Length == 0)
        {
            return false;
        }

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            nums[i] = n;
        }

        version = new NodeVersion(nums, hasBuild);
        return true;
    }

    /// <summary>Compare the numeric base versions only (component-wise, missing components treated
    /// as 0). Build metadata is deliberately NOT part of this ordering — see
    /// <see cref="IsUpdateOver"/> for the dev-above-release rule that consumes it.</summary>
    public int CompareTo(NodeVersion other)
    {
        var a = components ?? [];
        var b = other.components ?? [];
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int ai = i < a.Length ? a[i] : 0;
            int bi = i < b.Length ? b[i] : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }
        return 0;
    }

    /// <summary>
    /// Would <see langword="this"/> be an update to apply over a node currently running
    /// <paramref name="running"/>? <c>true</c> only when this version's numeric base is
    /// <b>strictly greater</b> than the running base. An equal base is "up to date" — and that is
    /// exactly the dev-above-release case: a <c>0.1.0+dev…</c> node compared against the <c>0.1.0</c>
    /// release has an equal base, so this returns <c>false</c> (no downgrade offered).
    /// </summary>
    public bool IsUpdateOver(NodeVersion running) => CompareTo(running) > 0;

    /// <summary>Render the numeric base (no build metadata) — what <c>latestVersion</c> reports.</summary>
    public override string ToString() => string.Join('.', components ?? []);

    // Comparison operators over the numeric base (CA1036). Note these compare the base only; the
    // dev-above-release semantics live in IsUpdateOver, which is what the availability check uses.
    public static bool operator <(NodeVersion left, NodeVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(NodeVersion left, NodeVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(NodeVersion left, NodeVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(NodeVersion left, NodeVersion right) => left.CompareTo(right) >= 0;
}
