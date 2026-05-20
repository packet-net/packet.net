using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Validates that <see cref="DefaultSubroutineRegistry.Wire(IActionDispatcher, GuardEvaluator)"/>
/// turns each generated <see cref="SubroutineSpec"/> name into a path-walking
/// invocation that goes through the supplied dispatcher and guard evaluator.
/// </summary>
/// <remarks>
/// Auto-Wire from <c>Ax25Session</c> is deliberately off (action verbs
/// like <c>"RC &lt;- 1"</c> aren't bound yet) — these tests exercise the
/// walker via explicit <c>Wire(...)</c> calls.
/// </remarks>
public class DefaultSubroutineRegistryWalkerTests
{
    [Fact]
    public void KnownSubroutines_Reflects_Generated_Specs_Plus_Legacy_Aliases()
    {
        // Packet.Ax25.Sdl v0.5.0's figc4.7 transcription emits 11
        // subroutines (was 13 — Establish_Data_Link and
        // Establish_Extended_Data_Link are skipped pending the n50
        // missing-edge graphml fix at m0lte/ax25sdl#11; supplied
        // hand-coded for now). The registry also exposes 4 legacy
        // aliases — the historic Enquiry_Response_F_0 / _F_1, plus the
        // post-rename aliases Select_T1_Value / Check_Need_For_Response
        // emitted as action verbs by older state-machine yamls.
        DefaultSubroutineRegistry.KnownSubroutines.Should().HaveCount(17);
        DefaultSubroutineRegistry.KnownSubroutines.Should().Contain(new[]
        {
            "N_r_Error_Recovery",
            "Clear_Exception_Conditions",
            "Set_Version_2_0",
            "Set_Version_2_2",
            "Establish_Data_Link",
            "Establish_Extended_Data_Link",
            "Enquiry_Response_F_0",   // legacy alias
            "Enquiry_Response_F_1",   // legacy alias
        });
    }

    [Fact]
    public void Default_Stub_Is_NoOp_When_Not_Wired()
    {
        var registry = new DefaultSubroutineRegistry();
        var tx = MakeContext();

        // Without Wire(), invoking a known subroutine silently no-ops.
        var act = () => registry.Invoke("Set_Version_2_0", tx);
        act.Should().NotThrow();
    }

    [Fact]
    public void Walker_Executes_Generated_Actions_When_Wired()
    {
        var actionsExecuted = new List<string>();
        var dispatcher = new RecordingDispatcher(actionsExecuted);
        var guards = MakeGuards(_ => true);   // every guard holds
        var registry = new DefaultSubroutineRegistry();
        registry.Wire(dispatcher, guards);

        var tx = MakeContext();
        registry.Invoke("Set_Version_2_0", tx);

        // Set_Version_2_0 has a single decision-free path with 7 processing
        // actions per APRS101 §11 — drawn as one multi-line processing box,
        // transcribed as one action per line.
        actionsExecuted.Should().Equal(new[]
        {
            "Set Half Duplex",
            "Set Implicit Reject",
            "Modulo := 8",
            "N1 := 2048",
            "k := 8",
            "T2 := 3000",
            "N2 := 10",
        });
    }

    [Fact]
    public void Register_Override_Survives_Wire()
    {
        var recorder = new List<string>();
        var dispatcher = new RecordingDispatcher(new List<string>());
        var guards = MakeGuards(_ => true);

        var registry = new DefaultSubroutineRegistry();
        registry.Register("Set_Version_2_0", _ => recorder.Add("custom"));
        registry.Wire(dispatcher, guards);   // walker added for everything else, NOT Set_Version_2_0

        registry.Invoke("Set_Version_2_0", MakeContext());
        recorder.Should().Equal(new[] { "custom" });
    }

    [Fact]
    public void Walker_Picks_First_Path_Whose_Guard_Holds()
    {
        // Establish_Data_Link has two paths: t01_mod_8_sabm (guard:
        // !mod_128) and t02_mod_128_sabme (guard: mod_128). Wire a guard
        // evaluator that says mod_128 is true → the SABME path fires.
        var actionsExecuted = new List<string>();
        var dispatcher = new RecordingDispatcher(actionsExecuted);
        var guards = MakeGuards(p => p == "mod_128");
        var registry = new DefaultSubroutineRegistry();
        registry.Wire(dispatcher, guards);

        registry.Invoke("Establish_Data_Link", MakeContext());
        actionsExecuted.Should().Contain("SABME");
        actionsExecuted.Should().NotContain("SABM");
    }

    [Theory]
    [InlineData("Enquiry_Response_F_0")]
    [InlineData("Enquiry_Response_F_1")]
    public void Legacy_Alias_Walks_Canonical_Enquiry_Response_Spec(string legacyAlias)
    {
        // figc4.4 has two distinct subroutine-call boxes — `Enquiry Response (F = 0)`
        // and `Enquiry Response (F = 1)` — that the redraw of figc4.7 collapses
        // into a single Enquiry_Response body. Per "Trust the figure" we keep
        // the figc4.4 transcription faithful (two verbs at the call site);
        // the registry resolves both to the same canonical spec.
        //
        // Pick the first-path actions the Enquiry_Response spec emits when
        // poll_supervisory_or_i is false (the simplest path:
        // t01_not_polled_rr_response → N(r) <- V(r); RR Response;
        // Clear Acknowledge Pending).
        var actionsExecuted = new List<string>();
        var dispatcher = new RecordingDispatcher(actionsExecuted);
        var guards = MakeGuards(_ => false);   // every predicate false → t01 path
        var registry = new DefaultSubroutineRegistry();
        registry.Wire(dispatcher, guards);

        registry.Invoke(legacyAlias, MakeContext());

        actionsExecuted.Should().Equal(new[]
        {
            "N(r) := V(r)",
            "RR Response",
            "Clear Acknowledge Pending",
        });
    }

    [Fact]
    public void Walker_Tolerates_Unbound_Predicate_As_NoMatch()
    {
        // GuardEvaluator throws GuardEvaluationException when a predicate
        // isn't bound; the walker should treat that as "path doesn't
        // match" and continue rather than crashing the caller.
        var actionsExecuted = new List<string>();
        var dispatcher = new RecordingDispatcher(actionsExecuted);
        var guards = MakeGuards(_ =>
            throw new GuardEvaluationException("unbound predicate (test)"));
        var registry = new DefaultSubroutineRegistry();
        registry.Wire(dispatcher, guards);

        // Establish_Data_Link has guarded paths; if all guards "throw
        // unbound", the walker should reach the end without invoking the
        // dispatcher and without crashing.
        var act = () => registry.Invoke("Establish_Data_Link", MakeContext());
        act.Should().NotThrow();
        actionsExecuted.Should().BeEmpty();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Construct a <see cref="GuardEvaluator"/> whose lookup is implemented
    /// by a single delegate (each predicate name routes through
    /// <paramref name="lookup"/>). The codegen tool emits guard expressions
    /// containing bare predicate identifiers; the evaluator wants per-name
    /// thunks. The catch-all bindings dictionary delegates each
    /// requested name to the supplied function.
    /// </summary>
    private static GuardEvaluator MakeGuards(Func<string, bool> lookup)
    {
        // The codegen-produced guards reference these specific identifiers;
        // pre-populate the dictionary with all of them. Tests can also pass
        // a lookup that throws to exercise the walker's unbound-predicate
        // tolerance path.
        var identifiers = new[]
        {
            "mod_128", "mod_8", "own_receiver_busy", "srej_enabled",
            "out_of_sequence_frames_in_receive_buffer",
            "f_eq_1_and_supervisory_or_i", "v_s_eq_x",
            "peer_receiver_busy", "n_r_eq_v_s", "t1_running", "n_r_eq_v_a",
            "command_and_p_eq_1", "response_and_f_eq_1",
            "incoming_is_command", "ui_info_field_valid",
            "rc_eq_0", "t1_expired",
        };
        var bindings = new Dictionary<string, Func<bool>>(StringComparer.Ordinal);
        foreach (var id in identifiers)
        {
            string captured = id;
            bindings[id] = () => lookup(captured);
        }
        return new GuardEvaluator(bindings);
    }

    private static TransitionContext MakeContext()
    {
        var local  = new Callsign("M0LTE", 1);
        var remote = new Callsign("WB2OSZ", 0);
        var sessionContext = new Ax25SessionContext { Local = local, Remote = remote };
        // Use any concrete event as the trigger — we don't care about its
        // shape, the walker just needs a non-null TransitionContext.
        return new TransitionContext(sessionContext, NullScheduler.Instance, new DlConnectRequest());
    }

    private sealed class RecordingDispatcher : IActionDispatcher
    {
        private readonly List<string> recorder;
        public RecordingDispatcher(List<string> recorder) => this.recorder = recorder;
        public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx)
        {
            foreach (var a in actions) recorder.Add(a.Verb);
        }
        public void Execute(IEnumerable<string> actions, TransitionContext tx)
        {
            foreach (var a in actions) recorder.Add(a);
        }
        public void Execute(string action, TransitionContext tx) => recorder.Add(action);
    }

    private sealed class NullScheduler : ITimerScheduler
    {
        public static readonly NullScheduler Instance = new();
        public void Arm(string name, TimeSpan duration, Action onExpiry) { }
        public void Cancel(string name) { }
        public bool IsRunning(string name) => false;
        public TimeSpan TimeRemaining(string name) => TimeSpan.Zero;
    }
}
