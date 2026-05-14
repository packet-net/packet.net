using YamlDotNet.Serialization;

namespace Packet.Sdl.IR;

// ─── YAML page models ────────────────────────────────────────────────
//
// Pure-data classes loaded directly from *.sdl.yaml files by
// YamlDotNet. No logic here — just the structural shape, with attributes
// where YamlDotNet's naming convention needs steering. Loaded by
// Loader; validated by Validation; projected to language-neutral
// template models in TemplateProjection.cs.

/// <summary>One state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / etc.).</summary>
public sealed class SdlPage
{
    public string Machine { get; set; } = "";
    public string State { get; set; } = "";
    public string Coverage { get; set; } = "complete";
    public SdlSourceYaml? Source { get; set; }
    public List<string>? Variables { get; set; }
    public List<string>? Save { get; set; }
    [YamlMember(Alias = "pinned_refs", ApplyNamingConventions = false)]
    public Dictionary<string, SdlPinnedRef>? PinnedRefs { get; set; }
    public List<SdlDecision> Decisions { get; set; } = new();
    public List<SdlTransition> Transitions { get; set; } = new();
    [YamlIgnore] public string SourcePath { get; set; } = "";
}

/// <summary>One subroutine page (figc4.7).</summary>
public sealed class SubroutinePage
{
    public string Machine { get; set; } = "";
    public SdlSourceYaml? Source { get; set; }
    [YamlMember(Alias = "pinned_refs", ApplyNamingConventions = false)]
    public Dictionary<string, SdlPinnedRef>? PinnedRefs { get; set; }
    public List<SubroutineYamlEntry> Subroutines { get; set; } = new();
    [YamlIgnore] public string SourcePath { get; set; } = "";
}

/// <summary>One subroutine inside a subroutine page.</summary>
public sealed class SubroutineYamlEntry
{
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public List<string>? Variables { get; set; }
    public List<SdlDecision> Decisions { get; set; } = new();
    public List<SubroutinePathYaml> Paths { get; set; } = new();
}

/// <summary>One path through a subroutine.</summary>
public sealed class SubroutinePathYaml
{
    public string Id { get; set; } = "";
    public List<SdlPathStep> Path { get; set; } = new();
    public string? Notes { get; set; }
    public List<SdlReference>? References { get; set; }
}

/// <summary>Spec metadata for a page (which figure it's transcribing).</summary>
public sealed class SdlSourceYaml
{
    public string Spec { get; set; } = "";
    public string Figure { get; set; } = "";
    public string? Url { get; set; }
}

/// <summary>One decision diamond on a page or subroutine.</summary>
public sealed class SdlDecision
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Predicate { get; set; } = "";
}

/// <summary>One transition on a state-machine page.</summary>
public sealed class SdlTransition
{
    public string Id { get; set; } = "";
    public string On { get; set; } = "";
    public List<SdlPathStep> Path { get; set; } = new();
    public string Next { get; set; } = "";
    public string? Notes { get; set; }
    public List<SdlReference>? References { get; set; }
}

/// <summary>
/// One cross-reference citation. spec_prose entries use Cite + Quote;
/// code citations use Path + Function + Line + Note. Source must be
/// "spec_prose" or a key in the page-level pinned_refs table.
/// </summary>
public sealed class SdlReference
{
    public string Source { get; set; } = "";
    public string? Cite { get; set; }
    public string? Quote { get; set; }
    public string? Path { get; set; }
    public string? Function { get; set; }
    public int? Line { get; set; }
    public string? Note { get; set; }
}

/// <summary>One entry in the page-level pinned_refs table.</summary>
public sealed class SdlPinnedRef
{
    public string Repo { get; set; } = "";
    public string Commit { get; set; } = "";
}

/// <summary>
/// One step in a transition's or subroutine path's <c>path:</c> list.
/// Exactly one of (Decision+Branch), (Action+Kind), or (LoopWhile+Body)
/// is populated; the validator rejects malformed steps.
/// </summary>
public sealed class SdlPathStep
{
    public string? Decision { get; set; }
    public string? Branch { get; set; }
    public string? Action { get; set; }
    public string? Kind { get; set; }
    [YamlMember(Alias = "loop_while", ApplyNamingConventions = false)]
    public string? LoopWhile { get; set; }
    public List<SdlPathStep>? Body { get; set; }
}
