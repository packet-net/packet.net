using CommandLine;
using Packet.Sdl.CodeGen.C;
using Packet.Sdl.CodeGen.Csharp;
using Packet.Sdl.CodeGen.Go;
using Packet.Sdl.CodeGen.Json;
using Packet.Sdl.CodeGen.Python;
using Packet.Sdl.CodeGen.Rust;
using Packet.Sdl.CodeGen.Ts;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen;

/// <summary>
/// Driver for the SDL codegen pipeline. Loads YAML pages from
/// <c>spec-sdl/</c>, validates them, resolves into the language-neutral
/// IR (<see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/>),
/// and hands them to one or more language backends
/// (C# / Go / TS / JSON / Rust / C / Python).
/// </summary>
/// <remarks>
/// <para>
/// Backend selection is opt-in by presence: pass no language flags and
/// every backend runs with its default output path; pass any language
/// flag and only the explicitly-enabled backends run. A backend is
/// "enabled" when its bare flag (<c>--csharp</c>, <c>--go</c>,
/// <c>--ts</c>, <c>--json</c>, <c>--rust</c>, <c>--emit-c</c>,
/// <c>--python</c>) is set OR when one of its path options
/// (<c>--csharp-out</c>, <c>--csharp-tests</c>, <c>--go-out</c>,
/// <c>--ts-out</c>, <c>--json-out</c>, <c>--rust-out</c>,
/// <c>--c-out</c>, <c>--python-out</c>) is set.
/// </para>
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        var parsed = Parser.Default.ParseArguments<CodegenOptions>(args);
        if (parsed.Errors.Any())
        {
            // CommandLineParser already wrote the help text to stderr.
            return 1;
        }

        try
        {
            return Run(CodegenPlan.From(parsed.Value));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    private static int Run(CodegenPlan plan)
    {
        if (!Directory.Exists(plan.InDir))
        {
            Console.Error.WriteLine($"::warning::SDL input directory '{plan.InDir}' does not exist; nothing to do.");
            return 0;
        }

        if (plan.EmitCsharp)
        {
            Directory.CreateDirectory(plan.CsharpOut);
            Directory.CreateDirectory(plan.CsharpTests);
        }
        if (plan.EmitGo)
        {
            Directory.CreateDirectory(plan.GoOut);
        }
        if (plan.EmitTs)
        {
            Directory.CreateDirectory(plan.TsOut);
        }
        if (plan.EmitJson)
        {
            Directory.CreateDirectory(plan.JsonOut);
        }
        if (plan.EmitRust)
        {
            Directory.CreateDirectory(plan.RustOut);
        }
        if (plan.EmitC)
        {
            Directory.CreateDirectory(plan.COut);
            // Test files land in a sibling `test/` directory of the C
            // source output. CMake's CTest scans that directory by glob.
            Directory.CreateDirectory(CTestDir(plan.COut));
        }
        if (plan.EmitPython)
        {
            Directory.CreateDirectory(plan.PythonOut);
        }

        var events  = EventCatalog.Load(Path.Combine(plan.InDir, "events.yaml"));
        var actions = ActionCatalog.Load(Path.Combine(plan.InDir, "actions.yaml"));

        // Split YAML files into state-machine pages (sdl-machine schema)
        // and subroutine pages (sdl-subroutines schema). Both share the
        // *.sdl.yaml extension; Loader.IsSubroutinePage routes by content.
        var yamlFiles = Directory.EnumerateFiles(plan.InDir, "*.sdl.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var pages = new List<SdlPage>(yamlFiles.Count);
        var subroutinePages = new List<SubroutinePage>();
        foreach (var path in yamlFiles)
        {
            if (Loader.IsSubroutinePage(path))
            {
                subroutinePages.Add(Loader.LoadSubroutinePage(path));
                continue;
            }
            pages.Add(Loader.LoadPage(path));
        }

        var errors = new List<string>();
        foreach (var page in pages)
        {
            Validation.NormaliseActionVerbs(page, actions, errors);
            Validation.ValidatePage(page, events, errors);
        }
        foreach (var subPage in subroutinePages)
        {
            Validation.NormaliseSubroutineActionVerbs(subPage, actions, errors);
            Validation.ValidateSubroutinePage(subPage, errors);
        }

        // Unused-alias lint: every alias declared in actions.yaml should
        // be referenced by at least one *.sdl.yaml verb. Otherwise the
        // alias is dead weight — file an error so the catalog stays tidy.
        foreach (var alias in actions.DeclaredAliases)
        {
            if (!actions.SeenAliases.Contains(alias))
            {
                errors.Add(
                    $"spec-sdl/actions.yaml: alias `{alias}` (→ `{actions.CanonicalLookup[alias]}`) " +
                    "is declared but never referenced by any *.sdl.yaml verb. Remove the alias or update " +
                    "a YAML page to use it.");
            }
        }

        // Predicate-completeness lint: every predicate the YAML's
        // decisions reference must have a binding in
        // Ax25SessionBindings.CreateDefault. Without this, an unbound
        // predicate makes GuardEvaluator.Evaluate throw at runtime
        // — and worse, the throw can be silently swallowed by a
        // background pump task, manifesting as a state machine that
        // mysteriously ignores certain frames. The lint catches it at
        // codegen time so transcription gaps surface BEFORE the live
        // RF test exposes them.
        LintPredicateBindings(pages, subroutinePages, errors);

        // Action-verb-completeness lint: every action verb the YAML
        // emits — after actions.yaml alias resolution — must have a
        // case arm in ActionDispatcher.Execute. Without this, an
        // unhandled verb makes the dispatcher throw at runtime with
        // "unknown SDL action". Same risk profile as the predicate
        // lint (silent swallow inside a background pump = mysterious
        // dropped frame). Catches both kinds of gap:
        //   1. SDL uses a figure-verbatim spelling, no alias in
        //      actions.yaml, no case in the dispatcher → lint fires
        //      with the YAML-verbatim name so we know to either add
        //      an alias or add a case.
        //   2. SDL uses the canonical spelling directly but the
        //      dispatcher's case is missing → lint fires with the
        //      canonical name so we know to add the case.
        LintActionDispatcherCoverage(pages, subroutinePages, errors);

        if (errors.Count > 0)
        {
            foreach (var e in errors) Console.Error.WriteLine($"::error::{e}");
            return 1;
        }

        var writtenCsharpCode    = new HashSet<string>(StringComparer.Ordinal);
        var writtenCsharpTests   = new HashSet<string>(StringComparer.Ordinal);
        var writtenMermaid       = new HashSet<string>(StringComparer.Ordinal);
        var writtenGo            = new HashSet<string>(StringComparer.Ordinal);
        var writtenTs            = new HashSet<string>(StringComparer.Ordinal);
        var writtenJson          = new HashSet<string>(StringComparer.Ordinal);
        var writtenRust          = new HashSet<string>(StringComparer.Ordinal);
        var writtenCSrc          = new HashSet<string>(StringComparer.Ordinal);
        var writtenCTest         = new HashSet<string>(StringComparer.Ordinal);
        var writtenPython        = new HashSet<string>(StringComparer.Ordinal);

        // Emit the JSON Schema once up front. State + subroutine pages
        // reference it via "$schema": "./schema.json" and validate against
        // it before they're written, so we need the schema text in hand
        // before the per-page loop starts.
        string? jsonSchemaText = null;
        if (plan.EmitJson)
        {
            jsonSchemaText = JsonEmitter.EmitSchema();
            var schemaPath = Path.Combine(plan.JsonOut, "schema.json");
            WriteIfChanged(schemaPath, jsonSchemaText);
            writtenJson.Add(Path.GetFullPath(schemaPath));
        }

        // Collect resolved IR so TS index emission sees the full set in
        // deterministic order at the end of the run.
        var resolvedPages = new List<ResolvedPage>(pages.Count);
        var resolvedSubPages = new List<ResolvedSubroutinesPage>(subroutinePages.Count);

        foreach (var page in pages)
        {
            var resolved = Resolver.Resolve(page);
            resolvedPages.Add(resolved);

            var label = "";
            if (plan.EmitCsharp)
            {
                var model = CsharpStateModel.From(resolved);
                var codePath  = Path.Combine(plan.CsharpOut,   model.ClassName + ".g.cs");
                var testsPath = Path.Combine(plan.CsharpTests, model.ClassName + ".g.Tests.cs");
                var mermaidPath = plan.MermaidOut is null
                    ? Path.Combine(
                        Path.GetDirectoryName(page.SourcePath)!,
                        Path.GetFileNameWithoutExtension(page.SourcePath).Replace(".sdl", string.Empty, StringComparison.Ordinal) + ".g.mmd")
                    : Path.Combine(plan.MermaidOut, model.ClassName + ".g.mmd");

                var emission = CsharpEmitter.EmitStatePage(resolved, codePath, testsPath);
                WriteIfChanged(codePath,    emission.Code);
                WriteIfChanged(testsPath,   emission.Tests);
                WriteIfChanged(mermaidPath, emission.Mermaid);
                writtenCsharpCode.Add(Path.GetFullPath(codePath));
                writtenCsharpTests.Add(Path.GetFullPath(testsPath));
                writtenMermaid.Add(Path.GetFullPath(mermaidPath));
                label = $"  →  {model.ClassName}.{{g.cs,g.Tests.cs,g.mmd}}";
            }

            if (plan.EmitGo)
            {
                var go = GoEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, go.FileName), go.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, go.FileName)));

                // Per-transition tests file alongside the data file —
                // matches the C# .g.Tests.cs scope (state-machine pages
                // only, not subroutine pages).
                var goTests = GoEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, goTests.FileName), goTests.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, goTests.FileName)));
                label += " + .g{,_test}.go";
            }

            if (plan.EmitTs)
            {
                var ts = TsEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, ts.FileName), ts.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, ts.FileName)));

                var tsTests = TsEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, tsTests.FileName), tsTests.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, tsTests.FileName)));
                label += " + .g{,.test}.ts";
            }

            if (plan.EmitJson)
            {
                var json = JsonEmitter.EmitStatePage(resolved);
                var outPath = Path.Combine(plan.JsonOut, json.FileName);
                // Validate before writing — a drift between the IR types
                // and the hand-written schema must fail codegen, not
                // produce broken consumer files.
                JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, json.Content, outPath);
                WriteIfChanged(outPath, json.Content);
                writtenJson.Add(Path.GetFullPath(outPath));
                label += " + .g.json";
            }

            if (plan.EmitRust)
            {
                // Rust co-locates data + per-transition tests in the
                // same .g.rs file (idiomatic #[cfg(test)] mod tests).
                var rs = RustEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.RustOut, rs.FileName), rs.Content);
                writtenRust.Add(Path.GetFullPath(Path.Combine(plan.RustOut, rs.FileName)));
                label += " + .g.rs";
            }

            if (plan.EmitC)
            {
                var c = CEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.COut, c.FileName), c.Content);
                writtenCSrc.Add(Path.GetFullPath(Path.Combine(plan.COut, c.FileName)));

                // Per-page test executable in the sibling test/ dir;
                // CMake's CTest picks them up via a glob.
                var cTestDir = CTestDir(plan.COut);
                var cTests = CEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(cTestDir, cTests.FileName), cTests.Content);
                writtenCTest.Add(Path.GetFullPath(Path.Combine(cTestDir, cTests.FileName)));
                label += " + .g{,.test}.c";
            }

            if (plan.EmitPython)
            {
                var py = PythonEmitter.EmitStatePage(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, py.FileName), py.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, py.FileName)));

                var pyTests = PythonEmitter.EmitStatePageTests(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, pyTests.FileName), pyTests.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, pyTests.FileName)));
                label += " + .g.py + _g_test.py";
            }

            Console.WriteLine($"  ok  {page.SourcePath}{label}");
        }

        // Subroutine pages: one .g.cs / .g.go / .g.ts per page (no
        // generated tests on this side — matches the C# emitter's
        // historical scope).
        foreach (var subPage in subroutinePages)
        {
            var resolved = Resolver.Resolve(subPage);
            resolvedSubPages.Add(resolved);

            string className = "";
            if (plan.EmitCsharp)
            {
                var model = CsharpSubroutinesModel.From(resolved);
                className = model.ClassName;
                var codePath = Path.Combine(plan.CsharpOut, model.ClassName + ".g.cs");
                var emission = CsharpEmitter.EmitSubroutinePage(resolved, codePath);
                WriteIfChanged(codePath, emission.Code);
                writtenCsharpCode.Add(Path.GetFullPath(codePath));
            }

            if (plan.EmitGo)
            {
                var go = GoEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.GoOut, go.FileName), go.Content);
                writtenGo.Add(Path.GetFullPath(Path.Combine(plan.GoOut, go.FileName)));
            }

            if (plan.EmitTs)
            {
                var ts = TsEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.TsOut, ts.FileName), ts.Content);
                writtenTs.Add(Path.GetFullPath(Path.Combine(plan.TsOut, ts.FileName)));
            }

            if (plan.EmitJson)
            {
                var json = JsonEmitter.EmitSubroutinePage(resolved);
                var outPath = Path.Combine(plan.JsonOut, json.FileName);
                JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, json.Content, outPath);
                WriteIfChanged(outPath, json.Content);
                writtenJson.Add(Path.GetFullPath(outPath));
            }

            if (plan.EmitRust)
            {
                var rs = RustEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.RustOut, rs.FileName), rs.Content);
                writtenRust.Add(Path.GetFullPath(Path.Combine(plan.RustOut, rs.FileName)));
            }

            if (plan.EmitC)
            {
                var c = CEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.COut, c.FileName), c.Content);
                writtenCSrc.Add(Path.GetFullPath(Path.Combine(plan.COut, c.FileName)));
            }

            if (plan.EmitPython)
            {
                var py = PythonEmitter.EmitSubroutinePage(resolved);
                WriteIfChanged(Path.Combine(plan.PythonOut, py.FileName), py.Content);
                writtenPython.Add(Path.GetFullPath(Path.Combine(plan.PythonOut, py.FileName)));
            }

            Console.WriteLine($"  ok  {subPage.SourcePath}  (subroutines){(className.Length > 0 ? "  →  " + className + ".g.cs" : "")}");
        }

        // TS package needs an index.ts that re-exports every page so
        // consumers can `import { DataLinkConnected } from "ax25sdl"`.
        // Go doesn't need this — every var declared in a package is
        // visible from the package namespace already.
        if (plan.EmitTs)
        {
            var indexPath = Path.Combine(plan.TsOut, "index.ts");
            WriteIfChanged(indexPath, TsEmitter.EmitIndex(resolvedPages, resolvedSubPages));
            writtenTs.Add(Path.GetFullPath(indexPath));
        }

        // JSON consumers get an index.json manifest mapping every emitted
        // .g.json to its page identity (machine / state / figure). Lets a
        // jq pipeline or Python script enumerate the spec without reading
        // every file's contents to discover what's there.
        if (plan.EmitJson)
        {
            var indexPath = Path.Combine(plan.JsonOut, "index.json");
            var indexText = JsonEmitter.EmitIndex(resolvedPages, resolvedSubPages);
            JsonEmitter.ValidateAgainstSchema(jsonSchemaText!, indexText, indexPath);
            WriteIfChanged(indexPath, indexText);
            writtenJson.Add(Path.GetFullPath(indexPath));
        }

        // Rust crate needs a lib.rs that declares each generated module
        // and re-exports them — without it `cargo build` doesn't see the
        // .g.rs files (they're not auto-discovered by file name).
        if (plan.EmitRust)
        {
            var libPath = Path.Combine(plan.RustOut, "lib.rs");
            WriteIfChanged(libPath, RustEmitter.EmitLib(resolvedPages, resolvedSubPages));
            writtenRust.Add(Path.GetFullPath(libPath));
        }

        // C library needs a master generated header so test sources
        // (and any other consumer) can `#include "ax25sdl.g.h"` and pick
        // up every `extern const StatePage ...` declaration in one go.
        if (plan.EmitC)
        {
            var headerPath = Path.Combine(plan.COut, "ax25sdl.g.h");
            WriteIfChanged(headerPath, CEmitter.EmitHeader(resolvedPages, resolvedSubPages));
            writtenCSrc.Add(Path.GetFullPath(headerPath));
        }

        // Python package needs an __init__.py that re-exports every page
        // constant. The .g.py filenames embed a literal dot which makes
        // `from .<stem>.g import …` invalid; __init__.py uses importlib
        // at module-init time to surface the constants at package scope.
        if (plan.EmitPython)
        {
            var initPath = Path.Combine(plan.PythonOut, "__init__.py");
            WriteIfChanged(initPath, PythonEmitter.EmitInit(resolvedPages, resolvedSubPages));
            writtenPython.Add(Path.GetFullPath(initPath));
        }

        // Tidy stale generated files (someone deleted a *.sdl.yaml).
        // Cleanups are scoped to "the files this run produced" so we
        // never sweep across runs that omit a backend — e.g. running
        // with --csharp doesn't delete go-spec/ax25sdl/*.g.go.
        if (plan.EmitCsharp)
        {
            CleanStaleFiles(plan.CsharpOut,   "*.g.cs",       writtenCsharpCode);
            CleanStaleFiles(plan.CsharpTests, "*.g.Tests.cs", writtenCsharpTests);
            if (plan.MermaidOut is not null)
            {
                CleanStaleFiles(plan.MermaidOut, "*.g.mmd", writtenMermaid);
            }
            else
            {
                foreach (var dir in Directory.EnumerateDirectories(plan.InDir, "*", SearchOption.AllDirectories))
                {
                    CleanStaleFiles(dir, "*.g.mmd", writtenMermaid);
                }
            }
        }
        if (plan.EmitGo)
        {
            CleanStaleFiles(plan.GoOut, "*.g.go",      writtenGo);
            CleanStaleFiles(plan.GoOut, "*.g_test.go", writtenGo);
            RunGofmt(plan.GoOut);
        }
        if (plan.EmitTs)
        {
            // index.ts and types.ts are intentionally outside both
            // patterns (the cleanup is scoped) so they survive across
            // codegen runs.
            CleanStaleFiles(plan.TsOut, "*.g.ts",      writtenTs);
            CleanStaleFiles(plan.TsOut, "*.g.test.ts", writtenTs);
        }
        if (plan.EmitJson)
        {
            // schema.json + index.json are outside the *.g.json glob and
            // were added to writtenJson at emission time, so they survive
            // the cleanup naturally.
            CleanStaleFiles(plan.JsonOut, "*.g.json", writtenJson);
        }
        if (plan.EmitRust)
        {
            // lib.rs is added to writtenRust at emission time, so it
            // survives the *.g.rs cleanup. types.rs is hand-written and
            // outside the glob. rustfmt canonicalises the per-page files.
            CleanStaleFiles(plan.RustOut, "*.g.rs", writtenRust);
            RunRustfmt(plan.RustOut);
        }
        if (plan.EmitC)
        {
            // Hand-written sources (ax25sdl.h, smoke.test.c, CMakeLists,
            // README) live outside the *.g.{c,h} globs and survive
            // cleanup. clang-format runs over src/ + test/ to wrap
            // long struct-initialiser strings into readable multi-line
            // form. The CI clang-format check enforces this canonical
            // shape; if clang-format isn't installed, the emitter's
            // raw single-line output gets written instead and CI's
            // dry-run check fails loudly — by design, since clang-format
            // is part of the documented runner toolchain.
            CleanStaleFiles(plan.COut,           "*.g.c",      writtenCSrc);
            CleanStaleFiles(plan.COut,           "*.g.h",      writtenCSrc);
            CleanStaleFiles(CTestDir(plan.COut), "*.g.test.c", writtenCTest);
            RunClangFormat(plan.COut);
            RunClangFormat(CTestDir(plan.COut));
        }
        if (plan.EmitPython)
        {
            // types.py and __init__.py both live outside the *.g.py /
            // *_g_test.py patterns. types.py is hand-written; __init__.py
            // is in writtenPython explicitly so the cleanup spares it.
            CleanStaleFiles(plan.PythonOut, "*.g.py",      writtenPython);
            CleanStaleFiles(plan.PythonOut, "*_g_test.py", writtenPython);
        }

        var which = string.Join(" + ", new[] {
            plan.EmitCsharp ? "C#"     : null,
            plan.EmitGo     ? "Go"     : null,
            plan.EmitTs     ? "TS"     : null,
            plan.EmitJson   ? "JSON"   : null,
            plan.EmitRust   ? "Rust"   : null,
            plan.EmitC      ? "C"      : null,
            plan.EmitPython ? "Python" : null,
        }.Where(s => s is not null));
        Console.WriteLine($"generated {pages.Count} state machine page(s), {subroutinePages.Count} subroutine page(s) [{which}]");
        return 0;
    }

    /// <summary>
    /// Shell out to <c>gofmt -w</c> on the Go output directory. gofmt's
    /// struct-field alignment rules are subtle (different alignment
    /// "runs" form around multi-line literal fields) and it's much
    /// simpler to delegate canonicalisation to gofmt itself than to
    /// re-implement it in the emitter. If gofmt isn't on PATH we emit
    /// a warning rather than fail — the codegen-idempotent CI check
    /// runs gofmt separately, so a missing gofmt locally is visible
    /// but non-blocking.
    /// </summary>
    private static void RunGofmt(string goDir)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gofmt",
                ArgumentList = { "-w", goDir },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null)
            {
                Console.Error.WriteLine("::warning::gofmt not found on PATH; emitted Go files may not be canonically formatted.");
                return;
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::gofmt exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("::warning::gofmt not found on PATH; emitted Go files may not be canonically formatted.");
        }
    }

    /// <summary>
    /// Shell out to <c>rustfmt</c> on every <c>*.rs</c> file in the Rust
    /// output directory. Mirrors <see cref="RunGofmt"/>: the emitter
    /// aims for output that's close to canonical and rustfmt handles
    /// the last-mile alignment. Missing rustfmt produces a warning,
    /// not a failure — the CI discipline job runs <c>cargo fmt --check</c>
    /// separately and will catch any drift.
    /// </summary>
    private static void RunRustfmt(string rustDir)
    {
        if (!Directory.Exists(rustDir)) return;
        var rsFiles = Directory.EnumerateFiles(rustDir, "*.rs", SearchOption.TopDirectoryOnly).ToList();
        if (rsFiles.Count == 0) return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rustfmt",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--edition");
            psi.ArgumentList.Add("2021");
            foreach (var f in rsFiles) psi.ArgumentList.Add(f);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                Console.Error.WriteLine("::warning::rustfmt not found on PATH; emitted Rust files may not be canonically formatted.");
                return;
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::rustfmt exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("::warning::rustfmt not found on PATH; emitted Rust files may not be canonically formatted.");
        }
    }

    /// <summary>
    /// Sibling <c>test/</c> directory derived from a C source-output
    /// directory. <c>c-spec/src</c> → <c>c-spec/test</c>. CMake's CTest
    /// pulls in <c>test/*.c</c> via a glob, so we keep this convention
    /// rather than expose a separate flag.
    /// </summary>
    private static string CTestDir(string cOut)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(cOut));
        return parent is null ? "test" : Path.Combine(parent, "test");
    }

    /// <summary>Run <c>clang-format -i</c> over all generated C files in a directory. Same warning-on-missing semantics as <see cref="RunGofmt"/> / <see cref="RunRustfmt"/>.</summary>
    private static void RunClangFormat(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.g.c")
            .Concat(Directory.GetFiles(dir, "*.g.h"))
            .Concat(Directory.GetFiles(dir, "*.g.test.c"))
            .ToArray();
        if (files.Length == 0) return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clang-format",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-i");
            foreach (var f in files) psi.ArgumentList.Add(f);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                Console.Error.WriteLine("::warning::clang-format not found on PATH; emitted C files may not be canonically formatted.");
                return;
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"::warning::clang-format exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("::warning::clang-format not found on PATH; emitted C files may not be canonically formatted.");
        }
    }

    private const string BindingsPath = "src/Packet.Ax25/Session/Ax25SessionBindings.cs";

    private static readonly HashSet<string> GuardOperators =
        new(new[] { "and", "or", "not" }, StringComparer.Ordinal);

    private static readonly char[] PredicateTokenSeparators = { ' ', '\t' };

    /// <summary>
    /// Cross-reference every predicate identifier the YAML's decisions
    /// reference against the bindings <see cref="BindingsPath"/> declares.
    /// Missing bindings become codegen errors with the precise predicate
    /// name and the YAML location of its first use.
    /// </summary>
    /// <remarks>
    /// Bindings are extracted by regex-scanning <see cref="BindingsPath"/>
    /// for indexer literals (<c>["name"] =</c>) — the canonical syntax for
    /// declaring bindings today. If someone adds a binding via a different
    /// mechanism (e.g. dynamic <c>bindings.Add(...)</c>), this lint won't
    /// see it; the regex is the contract. The lint is silently skipped
    /// when the bindings file isn't present, keeping the codegen tool
    /// useful as a standalone library generator without
    /// <c>Packet.Ax25</c> on disk.
    /// </remarks>
    private static void LintPredicateBindings(
        List<SdlPage> pages,
        List<SubroutinePage> subroutinePages,
        List<string> errors)
    {
        if (!File.Exists(BindingsPath))
        {
            // Standalone codegen invocation without the runtime library
            // on disk — skip rather than fail.
            return;
        }

        var bound = ExtractBoundPredicateNames(File.ReadAllText(BindingsPath));

        // Walk every decision in every page, tokenize its predicate,
        // and remember the first YAML location each identifier appears
        // at so the error message can point at it.
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            foreach (var d in page.Decisions)
            {
                CollectIdents(d.Predicate, $"{page.SourcePath}: decision `{d.Id}`", firstSeen);
            }
        }
        foreach (var page in subroutinePages)
        {
            foreach (var sub in page.Subroutines)
            {
                foreach (var d in sub.Decisions)
                {
                    CollectIdents(d.Predicate, $"{page.SourcePath}: subroutine `{sub.Name}` decision `{d.Id}`", firstSeen);
                }
            }
        }

        foreach (var (ident, loc) in firstSeen.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!bound.Contains(ident))
            {
                errors.Add(
                    $"{loc}: predicate `{ident}` has no binding in {BindingsPath}. " +
                    "Add an entry to Ax25SessionBindings.CreateDefault — unbound predicates throw " +
                    "GuardEvaluationException at runtime, which can manifest as silently-dropped frames " +
                    "when a background pump swallows the exception.");
            }
        }
    }

    private static void CollectIdents(string predicate, string location, Dictionary<string, string> firstSeen)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return;
        var tokens = predicate.Split(PredicateTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            if (GuardOperators.Contains(tok)) continue;
            firstSeen.TryAdd(tok, location);
        }
    }

    private static HashSet<string> ExtractBoundPredicateNames(string bindingsSource)
    {
        // Match `["name"]` indexer literals — both the dictionary-
        // initializer form (`["foo"] = ...`) and any other use of
        // the same indexer syntax. The regex's identifier class
        // matches C# identifier rules (letter/underscore start,
        // alnum/underscore body).
        var rx = new System.Text.RegularExpressions.Regex(@"\[""([A-Za-z_][A-Za-z0-9_]*)""\]");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(bindingsSource))
        {
            names.Add(m.Groups[1].Value);
        }
        return names;
    }

    private const string DispatcherPath = "src/Packet.Ax25/Session/ActionDispatcher.cs";

    /// <summary>
    /// Cross-reference every action verb the resolved IR emits against
    /// the case arms <see cref="DispatcherPath"/>'s Execute switch
    /// declares. The IR is resolved here (not before) so the lint sees
    /// the canonical verb names that the dispatcher actually receives at
    /// runtime — i.e. post-aliasing via <c>spec-sdl/actions.yaml</c>.
    /// Missing cases become codegen errors with the precise verb name
    /// and the YAML location of its first use.
    /// </summary>
    /// <remarks>
    /// Cases are extracted by regex-scanning <see cref="DispatcherPath"/>
    /// for <c>case "..."</c> string-literal labels. The dispatcher uses a
    /// single switch on the action string today; if a second switch is
    /// added in the same file the regex will pick up its cases too. That
    /// would over-accept (verbs in unrelated switches counted as handled)
    /// rather than under-accept, so the lint stays conservative. The
    /// lint is silently skipped when the dispatcher file isn't present
    /// (codegen as a standalone tool without the runtime library).
    /// </remarks>
    private static void LintActionDispatcherCoverage(
        List<SdlPage> pages,
        List<SubroutinePage> subroutinePages,
        List<string> errors)
    {
        if (!File.Exists(DispatcherPath))
        {
            return;
        }

        var handled = ExtractDispatcherCaseLabels(File.ReadAllText(DispatcherPath));

        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var resolved = Resolver.Resolve(page);
            foreach (var t in resolved.Transitions)
            {
                foreach (var a in t.Actions)
                {
                    var loc = $"{page.SourcePath}: transition `{t.Id}` action `{a.Verb}`";
                    firstSeen.TryAdd(a.Verb, loc);
                }
            }
        }

        foreach (var subPage in subroutinePages)
        {
            var resolved = Resolver.Resolve(subPage);
            foreach (var sub in resolved.Subroutines)
            {
                foreach (var path in sub.Paths)
                {
                    foreach (var a in path.Actions)
                    {
                        var loc = $"{subPage.SourcePath}: subroutine `{sub.Name}` path `{path.Id}` action `{a.Verb}`";
                        firstSeen.TryAdd(a.Verb, loc);
                    }
                }
            }
        }

        foreach (var (verb, loc) in firstSeen.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!handled.Contains(verb))
            {
                errors.Add(
                    $"{loc}: action `{verb}` has no case in {DispatcherPath}'s Execute switch. " +
                    "Add a case arm there, OR if this is a figure-verbatim spelling of an existing " +
                    "canonical verb, add an alias entry to spec-sdl/actions.yaml. Unhandled verbs " +
                    "throw 'unknown SDL action' at runtime, which can be silently swallowed by a " +
                    "background pump and manifest as dropped frames.");
            }
        }
    }

    private static HashSet<string> ExtractDispatcherCaseLabels(string dispatcherSource)
    {
        // Match `case "literal":` arms. Body can contain any non-quote
        // character (the dispatcher's verb spellings include spaces,
        // parens, equals, colons, arrows — but no embedded quotes or
        // escape sequences in practice).
        var rx = new System.Text.RegularExpressions.Regex(@"case\s+""([^""]+)""\s*:");
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(dispatcherSource))
        {
            labels.Add(m.Groups[1].Value);
        }
        return labels;
    }

    private static void WriteIfChanged(string path, string contents)
    {
        if (File.Exists(path) && File.ReadAllText(path) == contents) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void CleanStaleFiles(string dir, string pattern, HashSet<string> keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, pattern))
        {
            if (!keep.Contains(Path.GetFullPath(f)))
            {
                File.Delete(f);
                Console.WriteLine($"  removed stale {f}");
            }
        }
    }
}

/// <summary>
/// CLI surface for <see cref="Program"/>. Parsed by CommandLineParser.
/// </summary>
/// <remarks>
/// Empty-string sentinel means "not specified". CommandLineParser's
/// nullable-reference-type support pre-dates NRTs cleanly, so we use
/// <c>Default = ""</c> + length check rather than <c>string?</c>.
/// </remarks>
internal sealed class CodegenOptions
{
    [Option("in", Default = "spec-sdl", HelpText = "Directory containing *.sdl.yaml inputs.")]
    public string InDir { get; set; } = "spec-sdl";

    // ─── C# ────────────────────────────────────────────────────────────
    [Option("csharp", Default = false, HelpText = "Emit C# backend (defaults to src/Packet.Ax25.Sdl + tests/Packet.Ax25.Conformance.Tests).")]
    public bool Csharp { get; set; }

    [Option("csharp-out", Default = "", HelpText = "C# source output directory. Implies --csharp. Defaults to src/Packet.Ax25.Sdl.")]
    public string CsharpOut { get; set; } = "";

    [Option("csharp-tests", Default = "", HelpText = "C# generated-tests output directory. Implies --csharp. Defaults to tests/Packet.Ax25.Conformance.Tests.")]
    public string CsharpTests { get; set; } = "";

    [Option("mermaid-out", Default = "", HelpText = "Mermaid output directory (gated on --csharp). When unset, emits .g.mmd alongside each *.sdl.yaml.")]
    public string MermaidOut { get; set; } = "";

    // ─── Go ────────────────────────────────────────────────────────────
    [Option("go", Default = false, HelpText = "Emit Go backend (defaults to go-spec/ax25sdl).")]
    public bool Go { get; set; }

    [Option("go-out", Default = "", HelpText = "Go output directory. Implies --go. Defaults to go-spec/ax25sdl.")]
    public string GoOut { get; set; } = "";

    // ─── TypeScript ────────────────────────────────────────────────────
    [Option("ts", Default = false, HelpText = "Emit TypeScript backend (defaults to ts-spec/src/ax25sdl).")]
    public bool Ts { get; set; }

    [Option("ts-out", Default = "", HelpText = "TypeScript output directory. Implies --ts. Defaults to ts-spec/src/ax25sdl.")]
    public string TsOut { get; set; } = "";

    // ─── JSON ──────────────────────────────────────────────────────────
    [Option("json", Default = false, HelpText = "Emit JSON backend (defaults to json-spec/).")]
    public bool Json { get; set; }

    [Option("json-out", Default = "", HelpText = "JSON output directory. Implies --json. Defaults to json-spec.")]
    public string JsonOut { get; set; } = "";

    // ─── Rust ──────────────────────────────────────────────────────────
    [Option("rust", Default = false, HelpText = "Emit Rust backend (defaults to rust-spec/src).")]
    public bool Rust { get; set; }

    [Option("rust-out", Default = "", HelpText = "Rust source output directory. Implies --rust. Defaults to rust-spec/src.")]
    public string RustOut { get; set; } = "";

    // ─── C ─────────────────────────────────────────────────────────────
    //
    // CommandLineParser rejects single-character long names ("--c" would
    // throw at startup), so we expose the C backend as `--emit-c` long
    // form / `-c` short form. The path option uses `--c-out` to stay
    // consistent with the other backends' `<lang>-out` pattern.
    [Option('c', "emit-c", Default = false, HelpText = "Emit C backend (defaults to c-spec/src + sibling c-spec/test).")]
    public bool C { get; set; }

    [Option("c-out", Default = "", HelpText = "C source output directory. Implies --emit-c. Defaults to c-spec/src. The test/ sibling of this directory receives the .g.test.c files.")]
    public string COut { get; set; } = "";

    // ─── Python ────────────────────────────────────────────────────────
    [Option("python", Default = false, HelpText = "Emit Python backend (defaults to python-spec/ax25sdl).")]
    public bool Python { get; set; }

    [Option("python-out", Default = "", HelpText = "Python output directory. Implies --python. Defaults to python-spec/ax25sdl.")]
    public string PythonOut { get; set; } = "";
}

/// <summary>
/// Resolved plan derived from <see cref="CodegenOptions"/>. Encapsulates
/// "which backends to emit, with which paths". Built by
/// <see cref="From"/>, which applies the default-all-when-nothing-specified
/// rule and resolves blank-sentinel paths to their conventional defaults.
/// </summary>
internal sealed class CodegenPlan
{
    public required string InDir { get; init; }
    public required bool EmitCsharp { get; init; }
    public required bool EmitGo { get; init; }
    public required bool EmitTs { get; init; }
    public required bool EmitJson { get; init; }
    public required bool EmitRust { get; init; }
    public required bool EmitC { get; init; }
    public required bool EmitPython { get; init; }
    public required string CsharpOut { get; init; }
    public required string CsharpTests { get; init; }
    public required string GoOut { get; init; }
    public required string TsOut { get; init; }
    public required string JsonOut { get; init; }
    public required string RustOut { get; init; }
    public required string COut { get; init; }
    public required string PythonOut { get; init; }
    public required string? MermaidOut { get; init; }

    private const string DefaultCsharpOut   = "src/Packet.Ax25.Sdl";
    private const string DefaultCsharpTests = "tests/Packet.Ax25.Conformance.Tests";
    private const string DefaultGoOut       = "go-spec/ax25sdl";
    private const string DefaultTsOut       = "ts-spec/src/ax25sdl";
    private const string DefaultJsonOut     = "json-spec";
    private const string DefaultRustOut     = "rust-spec/src";
    private const string DefaultCOut        = "c-spec/src";
    private const string DefaultPythonOut   = "python-spec/ax25sdl";

    public static CodegenPlan From(CodegenOptions opt)
    {
        // A backend is "explicitly enabled" when either its bare flag or
        // any of its path options is set. Per-backend path options
        // therefore both *select* the backend and *configure* its output
        // — there's no way to express "emit Go but I don't care where".
        bool csharpExplicit = opt.Csharp || opt.CsharpOut.Length > 0 || opt.CsharpTests.Length > 0 || opt.MermaidOut.Length > 0;
        bool goExplicit     = opt.Go     || opt.GoOut.Length > 0;
        bool tsExplicit     = opt.Ts     || opt.TsOut.Length > 0;
        bool jsonExplicit   = opt.Json   || opt.JsonOut.Length > 0;
        bool rustExplicit   = opt.Rust   || opt.RustOut.Length > 0;
        bool cExplicit      = opt.C      || opt.COut.Length > 0;
        bool pythonExplicit = opt.Python || opt.PythonOut.Length > 0;
        bool anyExplicit    = csharpExplicit || goExplicit || tsExplicit || jsonExplicit
                              || rustExplicit || cExplicit || pythonExplicit;

        // Default rule: no language flags at all → emit every backend.
        bool emitCsharp = anyExplicit ? csharpExplicit : true;
        bool emitGo     = anyExplicit ? goExplicit     : true;
        bool emitTs     = anyExplicit ? tsExplicit     : true;
        bool emitJson   = anyExplicit ? jsonExplicit   : true;
        bool emitRust   = anyExplicit ? rustExplicit   : true;
        bool emitC      = anyExplicit ? cExplicit      : true;
        bool emitPython = anyExplicit ? pythonExplicit : true;

        return new CodegenPlan
        {
            InDir       = opt.InDir,
            EmitCsharp  = emitCsharp,
            EmitGo      = emitGo,
            EmitTs      = emitTs,
            EmitJson    = emitJson,
            EmitRust    = emitRust,
            EmitC       = emitC,
            EmitPython  = emitPython,
            CsharpOut   = opt.CsharpOut.Length > 0 ? opt.CsharpOut : DefaultCsharpOut,
            CsharpTests = opt.CsharpTests.Length > 0 ? opt.CsharpTests : DefaultCsharpTests,
            GoOut       = opt.GoOut.Length > 0 ? opt.GoOut : DefaultGoOut,
            TsOut       = opt.TsOut.Length > 0 ? opt.TsOut : DefaultTsOut,
            JsonOut     = opt.JsonOut.Length > 0 ? opt.JsonOut : DefaultJsonOut,
            RustOut     = opt.RustOut.Length > 0 ? opt.RustOut : DefaultRustOut,
            COut        = opt.COut.Length > 0 ? opt.COut : DefaultCOut,
            PythonOut   = opt.PythonOut.Length > 0 ? opt.PythonOut : DefaultPythonOut,
            MermaidOut  = opt.MermaidOut.Length > 0 ? opt.MermaidOut : null,
        };
    }
}
