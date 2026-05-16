namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Black-box tests of the codegen tool's CLI contract. Each test sets up a
/// fixture YAML, runs the codegen as a subprocess, and asserts on the
/// generated output or error messages.
/// </summary>
public class CodegenSmokeTests
{
    private const string MinimalEvents = """
        primitives_upper:
          - DL_DISCONNECT_request
        frames_received:
          - I_received
        catchalls: []
        internal: []
        timers:
          - T1_expiry
        """;

    private const string ValidMinimalPage = """
        machine: data_link
        state: Connected
        coverage: partial
        source:
          spec: test_spec
          figure: figc.test
        decisions: []
        transitions:
          - id: t01_dl_disconnect_request
            on: DL_DISCONNECT_request
            path:
              - { action: send_disc, kind: signal_lower }
            next: AwaitingRelease
        """;

    [Fact]
    public void Valid_minimal_page_generates_code_with_zero_exit()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}\nstdout: {result.Stdout}");
        r.GeneratedExists("DataLink_Connected.g.cs").Should().BeTrue();

        var gen = r.ReadGenerated("DataLink_Connected.g.cs");
        gen.Should().Contain("public static class DataLink_Connected");
        gen.Should().Contain("t01_dl_disconnect_request");
        gen.Should().Contain("send_disc");
    }

    [Fact]
    public void Unknown_event_name_fails_with_clear_error()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", ValidMinimalPage.Replace(
            "on: DL_DISCONNECT_request",
            "on: DL_DEFINITELY_NOT_AN_EVENT"));

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("DL_DEFINITELY_NOT_AN_EVENT");
        result.Stderr.Should().Contain("events.yaml");
    }

    [Fact]
    public void Decision_with_missing_no_branch_fails_completeness_lint()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: my_predicate
                question: "Is something true?"
                predicate: something_true
            transitions:
              - id: t01_yes_only
                on: I_received
                path:
                  - { decision: my_predicate, branch: "Yes" }
                  - { action: do_thing, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("my_predicate");
        result.Stderr.Should().Contain("'No'");
    }

    [Fact]
    public void Two_transitions_with_same_event_and_overlapping_guards_fail_lint()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: cond_a
                question: "A?"
                predicate: a_true
              - id: cond_b
                question: "B?"
                predicate: b_true
            transitions:
              - id: t01_a_yes
                on: I_received
                path:
                  - { decision: cond_a, branch: "Yes" }
                  - { action: alpha, kind: processing }
                next: Connected
              - id: t02_a_no
                on: I_received
                path:
                  - { decision: cond_a, branch: "No" }
                  - { action: beta, kind: processing }
                next: Connected
              - id: t03_b_yes
                on: I_received
                path:
                  - { decision: cond_b, branch: "Yes" }
                  - { action: gamma, kind: processing }
                next: Connected
              - id: t04_b_no
                on: I_received
                path:
                  - { decision: cond_b, branch: "No" }
                  - { action: delta, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        // t01 (guard=a_true) overlaps with t03 (guard=b_true) — non-disjoint.
        // t01/t02 are disjoint with each other (a_true vs not a_true).
        // t03/t04 are disjoint with each other. So expected: t01/t03,
        // t01/t04, t02/t03, t02/t04 are all flagged.
        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("non-disjoint");
        result.Stderr.Should().Contain("I_received");
    }

    [Fact]
    public void Loop_while_emits_loop_range_in_generated_code()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: has_more
                question: "More?"
                predicate: more_available
            transitions:
              - id: t01_drain
                on: I_received
                path:
                  - { decision: has_more, branch: "Yes" }
                  - loop_while: has_more
                    body:
                      - { action: retrieve_one, kind: processing }
                      - { action: deliver_one,  kind: signal_upper }
                  - { action: cleanup, kind: processing }
                next: Connected
              - id: t02_no_more
                on: I_received
                path:
                  - { decision: has_more, branch: "No" }
                  - { action: cleanup, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        var gen = r.ReadGenerated("DataLink_Connected.g.cs");

        // The loop body actions are inlined into Actions[] (one iteration).
        gen.Should().Contain("retrieve_one");
        gen.Should().Contain("deliver_one");
        gen.Should().Contain("cleanup");

        // A LoopRange entry is emitted, pointing at the body. Loop body
        // is two actions long; in this transition, indices are [0]
        // Action retrieve_one (after the implicit decision skipping the
        // gate which doesn't appear in Actions), [1] deliver_one,
        // [2] cleanup. The loop range covers indices 0..1 (length 2).
        gen.Should().Contain("new LoopRange(0, 2, \"more_available\")");
    }

    [Fact]
    public void Loop_while_with_nested_decision_in_body_is_rejected()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: has_more
                question: "More?"
                predicate: more_available
              - id: nested
                question: "Nested?"
                predicate: nested_true
            transitions:
              - id: t01_bad_loop
                on: I_received
                path:
                  - { decision: has_more, branch: "Yes" }
                  - loop_while: has_more
                    body:
                      - { decision: nested, branch: "Yes" }
                      - { action: thing, kind: processing }
                  - { action: cleanup, kind: processing }
                next: Connected
              - id: t02_no_more
                on: I_received
                path:
                  - { decision: has_more, branch: "No" }
                next: Connected
              - id: t03_nested_no
                on: I_received
                path:
                  - { decision: nested, branch: "No" }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("loop");
        result.Stderr.Should().Contain("body");
    }

    [Fact]
    public void Duplicate_transition_id_fails_validation()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions: []
            transitions:
              - id: t01_first
                on: I_received
                path:
                  - { action: a, kind: processing }
                next: Connected
              - id: t01_first
                on: I_received
                path:
                  - { action: b, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("duplicate");
        result.Stderr.Should().Contain("t01_first");
    }

    [Fact]
    public void Action_with_unknown_kind_is_rejected()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions: []
            transitions:
              - id: t01_bad_kind
                on: I_received
                path:
                  - { action: something, kind: telepathic_emission }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("telepathic_emission");
    }

    [Fact]
    public void Actions_catalog_normalises_alias_spellings_to_canonical()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog("""
            primitives_upper:
              - DL_DISCONNECT_request
            frames_received:
              - I_received
              - RR_received
              - RNR_received
            catchalls: []
            internal: []
            timers: []
            """);
        r.WriteActionsCatalog("""
            signal_lower:
              - name: "DM (F = 1)"
                aliases:
                  - "DM F=1"
                  - "DM F = 1"
            """);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions: []
            transitions:
              - id: t01_alias_a
                on: I_received
                path:
                  - { action: "DM F=1",  kind: signal_lower }
                next: Connected
              - id: t02_alias_b
                on: RR_received
                path:
                  - { action: "DM F = 1", kind: signal_lower }
                next: Connected
              - id: t03_canonical
                on: RNR_received
                path:
                  - { action: "DM (F = 1)", kind: signal_lower }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        var gen = r.ReadGenerated("DataLink_Connected.g.cs");

        // All three alias / canonical spellings normalise to the canonical
        // emitted into .g.cs. The verbatim alias spellings never appear.
        gen.Should().Contain("\"DM (F = 1)\"");
        gen.Should().NotContain("\"DM F=1\"");
        gen.Should().NotContain("\"DM F = 1\"");
    }

    [Fact]
    public void Actions_catalog_kind_mismatch_is_rejected()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WriteActionsCatalog("""
            signal_lower:
              - name: "DM (F = 1)"
                aliases:
                  - "DM F=1"
            """);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions: []
            transitions:
              - id: t01_wrong_kind
                on: I_received
                path:
                  # Catalog declares "DM F=1" as signal_lower; YAML draws it
                  # as processing — must be flagged as a transcription error.
                  - { action: "DM F=1", kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("DM F=1");
        result.Stderr.Should().Contain("signal_lower");
        result.Stderr.Should().Contain("processing");
    }

    [Fact]
    public void Reference_to_undefined_pinned_source_is_rejected()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            pinned_refs:
              linbpq:
                repo: https://example.com/linbpq
                commit: aaaaaaaa
            decisions: []
            transitions:
              - id: t01_unknown_source
                on: I_received
                path:
                  - { action: thing, kind: processing }
                next: Connected
                references:
                  - source: not_pinned_anywhere
                    path: foo.c
                    function: bar
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        result.Stderr.Should().Contain("not_pinned_anywhere");
        result.Stderr.Should().Contain("pinned_refs");
    }

    // ─── lint-targets.yaml behaviour ─────────────────────────────────────
    //
    // The runtime-specific lints (predicate / dispatcher / subroutine /
    // DL-ERROR / orphan) are driven by spec-sdl/lint-targets.yaml. These
    // tests pin three invariants:
    //   1. No lint-targets.yaml → runtime-specific lints all skip cleanly
    //      (preserves the standalone-codegen escape hatch).
    //   2. Two targets where one has a bindings gap → only that target's
    //      label appears in the error.
    //   3. Error messages carry the `[language]` prefix from the target's
    //      `language:` field, so a CI failure attributes the gap.

    [Fact]
    public void Without_lint_targets_yaml_runtime_specific_lints_skip()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        // A page that references a predicate not bound in any runtime —
        // the predicate lint would normally fire if any target was on
        // disk. With no lint-targets.yaml, the runtime-specific lints
        // all silently skip, so codegen succeeds.
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: needs_unbound_predicate
                question: "Definitely unbound?"
                predicate: some_predicate_no_runtime_binds
            transitions:
              - id: t01_yes
                on: I_received
                path:
                  - { decision: needs_unbound_predicate, branch: "Yes" }
                  - { action: do_thing, kind: processing }
                next: Connected
              - id: t02_no
                on: I_received
                path:
                  - { decision: needs_unbound_predicate, branch: "No" }
                  - { action: do_other_thing, kind: processing }
                next: Connected
            """);

        var result = r.Run();

        result.ExitCode.Should().Be(0, $"stderr: {result.Stderr}");
        result.Stderr.Should().NotContain("some_predicate_no_runtime_binds");
    }

    [Fact]
    public void Lint_targets_with_two_runtimes_fires_only_for_target_with_gap()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: foo_check
                question: "Foo?"
                predicate: foo_predicate
            transitions:
              - id: t01_yes
                on: I_received
                path:
                  - { decision: foo_check, branch: "Yes" }
                  - { action: do_thing, kind: processing }
                next: Connected
              - id: t02_no
                on: I_received
                path:
                  - { decision: foo_check, branch: "No" }
                  - { action: do_other_thing, kind: processing }
                next: Connected
            """);
        // Two fake runtime bindings files: alpha has the binding,
        // bravo doesn't.
        r.WriteFile("fake-runtime/alpha/bindings.cs",
            "// alpha binds the predicate\nvar bindings = new Dictionary<string, Func<bool>>(); bindings[\"foo_predicate\"] = () => true;\n");
        r.WriteFile("fake-runtime/bravo/bindings.cs",
            "// bravo binds something else entirely\nvar bindings = new Dictionary<string, Func<bool>>(); bindings[\"different_predicate\"] = () => true;\n");
        r.WriteLintTargets("""
            targets:
              - language: alpha
                bindings:
                  path: fake-runtime/alpha/bindings.cs
                  regex: '\["([A-Za-z_][A-Za-z0-9_]*)"\]'
              - language: bravo
                bindings:
                  path: fake-runtime/bravo/bindings.cs
                  regex: '\["([A-Za-z_][A-Za-z0-9_]*)"\]'
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0, $"stderr: {result.Stderr}");
        // Error must mention the bravo target by language label, but
        // not the alpha target — alpha has the binding.
        result.Stderr.Should().Contain("[bravo]");
        result.Stderr.Should().Contain("foo_predicate");
        result.Stderr.Should().NotContain("[alpha]");
    }

    [Fact]
    public void Lint_targets_error_messages_carry_language_label()
    {
        using var r = new CodegenRunner();
        r.WriteEventsCatalog(MinimalEvents);
        r.WritePage("data-link/connected.sdl.yaml", """
            machine: data_link
            state: Connected
            coverage: partial
            source: { spec: test, figure: f }
            decisions:
              - id: gap_check
                question: "Gap?"
                predicate: gap_predicate
            transitions:
              - id: t01_yes
                on: I_received
                path:
                  - { decision: gap_check, branch: "Yes" }
                  - { action: do_thing, kind: processing }
                next: Connected
              - id: t02_no
                on: I_received
                path:
                  - { decision: gap_check, branch: "No" }
                  - { action: do_other_thing, kind: processing }
                next: Connected
            """);
        r.WriteFile("fake-runtime/mypy/bindings.py",
            "# python-style binding declaration\nbindings = {}\n");
        r.WriteLintTargets("""
            targets:
              - language: mypy
                bindings:
                  path: fake-runtime/mypy/bindings.py
                  regex: 'bindings\["([A-Za-z_][A-Za-z0-9_]*)"\]'
            """);

        var result = r.Run();

        result.ExitCode.Should().NotBe(0);
        // The custom language label appears verbatim in the error
        // message — so future runtimes can be added with arbitrary
        // labels and the per-target attribution still works.
        result.Stderr.Should().Contain("[mypy]");
        result.Stderr.Should().Contain("gap_predicate");
    }
}
