using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Per-port AX.25 <b>compatibility profile</b>: which wire frames the port
/// accepts (an <see cref="Ax25ParseOptions"/> preset, optionally adjusted by
/// individual named flags) and which SDL session quirks new sessions run with
/// (an <see cref="Ax25SessionQuirks"/> selector). Null / absent =
/// <c>lenient</c> parsing + <c>default</c> quirks - exactly the node's
/// historical behaviour, so adding this setting is never a silent change.
/// </summary>
/// <remarks>
/// <para>
/// This is the node-host surface for the library's spec-vs-pragmatic flags
/// (see <c>docs/strict-vs-pragmatic-audit.md</c>): the operator matches a port
/// to its neighbour - a BPQ-facing port runs <c>preset: bpq</c>, a clean v2.2
/// link can run <c>preset: strict</c> - instead of every port being
/// kitchen-sink lenient. Presets are resolved by <see cref="Ax25CompatPresets"/>;
/// the per-flag overrides apply on top of the preset, explicit-wins, mirroring
/// how <see cref="ChannelProfiles"/> overlays timing params.
/// </para>
/// <para>
/// Like the rest of <see cref="PortConfig"/> this is a value record - the
/// reconcile planner diffs it with <c>Equals</c> to classify a compat-only
/// edit as a hot (no-restart) change.
/// </para>
/// </remarks>
public sealed record PortCompatConfig
{
    /// <summary>The <see cref="Ax25ParseOptions"/> preset name:
    /// <c>strict</c> | <c>lenient</c> | <c>bpq</c> | <c>xrouter</c> |
    /// <c>direwolf</c> (case-insensitive). Null = <c>lenient</c> (the
    /// historical default).</summary>
    public string? Preset { get; init; }

    /// <summary>Override the preset's <see cref="Ax25ParseOptions.AllowEmptyCallsignBase"/>
    /// (accept all-space callsign slots - BPQ <c>&gt;IS</c> ID beacons et al).
    /// Null = take the preset's value.</summary>
    public bool? AllowEmptyCallsignBase { get; init; }

    /// <summary>Override the preset's <see cref="Ax25ParseOptions.AllowInfoOnSupervisoryFrames"/>
    /// (capture trailing bytes on S frames instead of rejecting the frame).
    /// Null = take the preset's value.</summary>
    public bool? AllowInfoOnSupervisoryFrames { get; init; }

    /// <summary>Override the preset's <see cref="Ax25ParseOptions.AllowCommandFrameAsResponse"/>
    /// (accept a SABM/SABME/DISC whose C-bits don't mark it a command - AX.25
    /// v1.x interop, #142). Null = take the preset's value.</summary>
    public bool? AllowCommandFrameAsResponse { get; init; }

    /// <summary>The <see cref="Ax25SessionQuirks"/> selector for sessions on this
    /// port: <c>default</c> (spec-correct - all documented figure-defect
    /// corrections on) | <c>strictly-faithful</c> (run the SDL figures exactly
    /// as drawn, defects included - conformance study only, not for on-air
    /// use). Null = <c>default</c>.</summary>
    public string? Quirks { get; init; }
}

/// <summary>
/// Maps <see cref="PortCompatConfig"/>'s operator-facing names onto the
/// library's <see cref="Ax25ParseOptions"/> presets and
/// <see cref="Ax25SessionQuirks"/> selectors, and resolves a port's effective
/// values (preset first, then the per-flag overrides on top). The single
/// authority the validator and the <c>PortSupervisor</c> both use, so a name
/// the validator accepted can never fail to resolve at bring-up.
/// </summary>
public static class Ax25CompatPresets
{
    /// <summary>The recognised parse-preset names (for the validator's error
    /// message + docs).</summary>
    public static IReadOnlyList<string> PresetNames { get; } = ["strict", "lenient", "bpq", "xrouter", "direwolf"];

    /// <summary>The recognised quirks-selector names (for the validator's error
    /// message + docs).</summary>
    public static IReadOnlyList<string> QuirksNames { get; } = ["default", "strictly-faithful"];

    /// <summary>True if <paramref name="preset"/> names a known parse preset
    /// (case-insensitive). Null/empty is "no preset" (= lenient) - also valid.</summary>
    public static bool IsKnownPreset(string? preset) =>
        string.IsNullOrWhiteSpace(preset) || PresetFor(preset) is not null;

    /// <summary>True if <paramref name="quirks"/> names a known quirks selector
    /// (case- and hyphen/underscore-insensitive). Null/empty is "default" - also
    /// valid.</summary>
    public static bool IsKnownQuirks(string? quirks) =>
        string.IsNullOrWhiteSpace(quirks) || QuirksFor(quirks) is not null;

    /// <summary>
    /// Resolve a port's effective inbound <see cref="Ax25ParseOptions"/>: the
    /// named preset (null = <see cref="Ax25ParseOptions.Lenient"/>) with the
    /// individual flag overrides applied on top. An unknown preset name resolves
    /// as lenient - unreachable in practice because validation rejects it first.
    /// </summary>
    public static Ax25ParseOptions ResolveParseOptions(PortCompatConfig? compat)
    {
        var preset = PresetFor(compat?.Preset) ?? Ax25ParseOptions.Lenient;
        if (compat is null)
        {
            return preset;
        }

        return preset with
        {
            AllowEmptyCallsignBase = compat.AllowEmptyCallsignBase ?? preset.AllowEmptyCallsignBase,
            AllowInfoOnSupervisoryFrames = compat.AllowInfoOnSupervisoryFrames ?? preset.AllowInfoOnSupervisoryFrames,
            AllowCommandFrameAsResponse = compat.AllowCommandFrameAsResponse ?? preset.AllowCommandFrameAsResponse,
        };
    }

    /// <summary>Resolve a port's effective <see cref="Ax25SessionQuirks"/> (null
    /// = <see cref="Ax25SessionQuirks.Default"/>). An unknown selector resolves
    /// as default - unreachable in practice because validation rejects it first.</summary>
    public static Ax25SessionQuirks ResolveQuirks(PortCompatConfig? compat) =>
        QuirksFor(compat?.Quirks) ?? Ax25SessionQuirks.Default;

    private static Ax25ParseOptions? PresetFor(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return null;
        }

        return Normalise(preset) switch
        {
            "strict" => Ax25ParseOptions.Strict,
            "lenient" => Ax25ParseOptions.Lenient,
            "bpq" => Ax25ParseOptions.Bpq,
            "xrouter" => Ax25ParseOptions.Xrouter,
            "direwolf" => Ax25ParseOptions.Direwolf,
            _ => null,
        };
    }

    private static Ax25SessionQuirks? QuirksFor(string? quirks)
    {
        if (string.IsNullOrWhiteSpace(quirks))
        {
            return null;
        }

        return Normalise(quirks) switch
        {
            "default" => Ax25SessionQuirks.Default,
            "strictlyfaithful" => Ax25SessionQuirks.StrictlyFaithful,
            _ => null,
        };
    }

    // Same normalisation as ChannelProfiles: names are case- and
    // hyphen/underscore-insensitive ("strictly-faithful" == "StrictlyFaithful").
    private static string Normalise(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal)
           .Replace("_", "", StringComparison.Ordinal)
           .ToLowerInvariant();
}
