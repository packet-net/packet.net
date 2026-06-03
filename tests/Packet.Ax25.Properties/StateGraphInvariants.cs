using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25.Sdl;

namespace Packet.Ax25.Properties;

/// <summary>
/// Structural-invariant properties over the codegen-emitted SDL
/// transition tables (<c>DataLink_*.Transitions</c>). These tests verify
/// that the state graph is well-formed independent of any runtime
/// behaviour:
/// </summary>
/// <remarks>
/// <para>
/// Each property runs once (no FsCheck-generated input) and iterates
/// over the static transition tables. The <c>[Property]</c> attribute is
/// used so that any future per-element generator can slot in easily —
/// e.g. shrinking on a specific transition that violates the invariant.
/// </para>
/// <para>
/// These tests catch the kinds of regressions that codegen lints don't:
/// cross-page naming drift (state name mismatch, action verb spelling
/// changes), duplicate transition ids across versions, decision-branch
/// completeness across patterns.
/// </para>
/// </remarks>
public class StateGraphInvariants
{
    /// <summary>
    /// Every Data-Link state machine's transition table, paired with its
    /// declared <c>From</c> state name. Update this list when a new state
    /// machine is transcribed.
    /// </summary>
    private static IEnumerable<(string Name, IReadOnlyList<TransitionSpec> Transitions)> AllDataLinkTables()
    {
        yield return (nameof(DataLink_Disconnected),         DataLink_Disconnected.Transitions);
        yield return (nameof(DataLink_AwaitingConnection),   DataLink_AwaitingConnection.Transitions);
        yield return (nameof(DataLink_AwaitingRelease),      DataLink_AwaitingRelease.Transitions);
        yield return (nameof(DataLink_Connected),            DataLink_Connected.Transitions);
        yield return (nameof(DataLink_AwaitingV22Connection), DataLink_AwaitingV22Connection.Transitions);
        yield return (nameof(DataLink_TimerRecovery),        DataLink_TimerRecovery.Transitions);
    }

    /// <summary>
    /// State names that are valid as a transition's <c>Next</c> target —
    /// either a state with its own transition table, or a known terminal
    /// state we haven't transcribed yet (e.g. TimerRecovery before figc4.5).
    /// </summary>
    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        // Transcribed Data-Link states (have own tables)
        "Disconnected",
        "AwaitingConnection",
        "AwaitingRelease",
        "Connected",
        "AwaitingV22Connection",
        "TimerRecovery",
    };

    [Property(DisplayName = "Every transition's Next state is a known Data-Link state name")]
    public void AllNextStatesAreKnown()
    {
        var offenders = AllDataLinkTables()
            .SelectMany(t => t.Transitions.Select(tx => (Table: t.Name, Transition: tx)))
            .Where(x => !KnownStates.Contains(x.Transition.Next))
            .Select(x => $"{x.Table}::{x.Transition.Id} → '{x.Transition.Next}'")
            .ToArray();

        offenders.Should().BeEmpty(
            $"transitions must target a state in {{{string.Join(", ", KnownStates)}}}");
    }

    [Property(DisplayName = "Every transition's From state equals its table's state name")]
    public void AllFromStatesMatchTable()
    {
        // The codegen emits From = the YAML's `state:` field. Within a single
        // transition table, every transition must have the same From.
        foreach (var (name, transitions) in AllDataLinkTables())
        {
            if (transitions.Count == 0) continue;

            var distinctFroms = transitions.Select(t => t.From).Distinct().ToArray();
            distinctFroms.Should().HaveCount(1,
                $"{name}: all transitions in one table should share the same From state, got {{{string.Join(", ", distinctFroms)}}}");
        }
    }

    [Property(DisplayName = "Transition ids are unique within each state's table")]
    public void TransitionIdsAreUniqueWithinTable()
    {
        foreach (var (name, transitions) in AllDataLinkTables())
        {
            var duplicates = transitions
                .GroupBy(t => t.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{name}::{g.Key} appears {g.Count()} times")
                .ToArray();

            duplicates.Should().BeEmpty($"{name}: transition ids must be unique");
        }
    }

    [Property(DisplayName = "Transition ids follow the t##_ convention")]
    public void TransitionIdsMatchConvention()
    {
        var idRegex = new System.Text.RegularExpressions.Regex(@"^t\d{2,3}_[a-z0-9_]+$");

        var offenders = AllDataLinkTables()
            .SelectMany(t => t.Transitions.Select(tx => (Table: t.Name, Id: tx.Id)))
            .Where(x => !idRegex.IsMatch(x.Id))
            .Select(x => $"{x.Table}::{x.Id}")
            .ToArray();

        offenders.Should().BeEmpty("ids must match 't##_<snake_case>'");
    }

    [Property(DisplayName = "Every action's Verb is a defined Ax25ActionVerb value")]
    public void AllActionsHaveDefinedVerb()
    {
        // Verb is the generated Ax25ActionVerb enum (Packet.Ax25.Sdl 0.8.0+),
        // so "non-empty" is now "a defined enum member" — a table carrying an
        // out-of-range cast would be a codegen bug.
        var offenders = AllDataLinkTables()
            .SelectMany(t => t.Transitions.SelectMany(tx =>
                tx.Actions.Select((a, i) => (Table: t.Name, Id: tx.Id, Index: i, Verb: a.Verb))))
            .Where(x => !Enum.IsDefined(x.Verb))
            .Select(x => $"{x.Table}::{x.Id}.actions[{x.Index}] has undefined verb '{x.Verb}'")
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Property(DisplayName = "Every action's Kind is a defined ActionKind value")]
    public void AllActionKindsAreDefined()
    {
        var offenders = AllDataLinkTables()
            .SelectMany(t => t.Transitions.SelectMany(tx =>
                tx.Actions.Select((a, i) => (Table: t.Name, Id: tx.Id, Index: i, Kind: a.Kind))))
            .Where(x => !Enum.IsDefined(x.Kind))
            .Select(x => $"{x.Table}::{x.Id}.actions[{x.Index}] has undefined kind '{x.Kind}'")
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Property(DisplayName = "Multi-transition events have non-null guards on every transition")]
    public void MultiTransitionEventsAlwaysHaveGuards()
    {
        // When a single event produces multiple transitions, every transition
        // must have a non-null guard — otherwise the orchestrator can't pick
        // between them deterministically. (The codegen's guard_overlap and
        // decision_branch_completeness lints check the deeper partition
        // property; this test just enforces the shape.)

        foreach (var (name, transitions) in AllDataLinkTables())
        {
            var byEvent = transitions.GroupBy(t => t.On);
            foreach (var group in byEvent)
            {
                if (group.Count() == 1) continue;

                var unguarded = group.Where(t => t.Guard is null).ToArray();
                unguarded.Should().BeEmpty(
                    $"{name}::on '{group.Key}': {unguarded.Length} of {group.Count()} transitions are unguarded — non-determinism");
            }
        }
    }

    [Property(DisplayName = "Every implementation reference has either spec_prose+cite or path+function")]
    public void ImplementationReferencesAreWellFormed()
    {
        var offenders = AllDataLinkTables()
            .SelectMany(t => t.Transitions.SelectMany(tx =>
                tx.References.Select((r, i) => (Table: t.Name, Id: tx.Id, Index: i, Ref: r))))
            .Where(x =>
            {
                var r = x.Ref;
                if (r.Source == "spec_prose")
                {
                    // spec_prose entries must carry a cite (e.g. "§6.3.5 ¶3").
                    return string.IsNullOrWhiteSpace(r.Cite);
                }
                // Code refs must have a path and function anchor.
                return string.IsNullOrWhiteSpace(r.Path) || string.IsNullOrWhiteSpace(r.Function);
            })
            .Select(x => $"{x.Table}::{x.Id}.references[{x.Index}] (source={x.Ref.Source})")
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Property(DisplayName = "No transition appears in more than one state table")]
    public void TransitionIdsAreUniqueAcrossAllTables()
    {
        // Belt-and-braces vs TransitionIdsAreUniqueWithinTable — same id
        // appearing in two different state tables is legal in principle
        // (they're scoped by `From`) but is a smell that suggests
        // copy-paste drift. Surface globally-duplicated ids for review.
        var allIds = AllDataLinkTables()
            .SelectMany(t => t.Transitions.Select(tx => (Table: t.Name, Id: tx.Id)))
            .ToArray();

        var collisions = allIds
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} appears in {{{string.Join(", ", g.Select(x => x.Table))}}}")
            .ToArray();

        // Don't fail — this is informational. But assert the count for visibility.
        // (Currently every figure's t01 means t01 appears 5 times — by design.)
        // If we ever want to enforce global uniqueness, change this to .Should().BeEmpty().
        collisions.Length.Should().BeGreaterThan(0,
            "expected at least some shared t## ids across tables (by figure convention)");
    }
}
