using CommandLine;
using Packet.Sdl.CodeGen.Csharp;
using Packet.Sdl.CodeGen.Go;
using Packet.Sdl.CodeGen.Ts;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen;

/// <summary>
/// Driver for the SDL codegen pipeline. Loads YAML pages from
/// <c>spec-sdl/</c>, validates them, resolves into the language-neutral
/// IR (<see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/>),
/// and hands them to one or more language backends (C# / Go / TS).
/// </summary>
/// <remarks>
/// <para>
/// Backend selection is opt-in by presence: pass no language flags and
/// all three backends run with their default output paths; pass any
/// language flag and only the explicitly-enabled backends run. A
/// backend is "enabled" when its flag (<c>--csharp</c>, <c>--go</c>,
/// <c>--ts</c>) is set OR when one of its path options
/// (<c>--csharp-out</c>, <c>--csharp-tests</c>, <c>--go-out</c>,
/// <c>--ts-out</c>) is set.
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

        var which = string.Join(" + ", new[] {
            plan.EmitCsharp ? "C#" : null,
            plan.EmitGo     ? "Go" : null,
            plan.EmitTs     ? "TS" : null,
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
    public required string CsharpOut { get; init; }
    public required string CsharpTests { get; init; }
    public required string GoOut { get; init; }
    public required string TsOut { get; init; }
    public required string? MermaidOut { get; init; }

    private const string DefaultCsharpOut   = "src/Packet.Ax25.Sdl";
    private const string DefaultCsharpTests = "tests/Packet.Ax25.Conformance.Tests";
    private const string DefaultGoOut       = "go-spec/ax25sdl";
    private const string DefaultTsOut       = "ts-spec/src/ax25sdl";

    public static CodegenPlan From(CodegenOptions opt)
    {
        // A backend is "explicitly enabled" when either its bare flag or
        // any of its path options is set. Per-backend path options
        // therefore both *select* the backend and *configure* its output
        // — there's no way to express "emit Go but I don't care where".
        bool csharpExplicit = opt.Csharp || opt.CsharpOut.Length > 0 || opt.CsharpTests.Length > 0 || opt.MermaidOut.Length > 0;
        bool goExplicit     = opt.Go     || opt.GoOut.Length > 0;
        bool tsExplicit     = opt.Ts     || opt.TsOut.Length > 0;
        bool anyExplicit    = csharpExplicit || goExplicit || tsExplicit;

        // Default rule: no language flags at all → emit every backend.
        bool emitCsharp = anyExplicit ? csharpExplicit : true;
        bool emitGo     = anyExplicit ? goExplicit     : true;
        bool emitTs     = anyExplicit ? tsExplicit     : true;

        return new CodegenPlan
        {
            InDir       = opt.InDir,
            EmitCsharp  = emitCsharp,
            EmitGo      = emitGo,
            EmitTs      = emitTs,
            CsharpOut   = opt.CsharpOut.Length > 0 ? opt.CsharpOut : DefaultCsharpOut,
            CsharpTests = opt.CsharpTests.Length > 0 ? opt.CsharpTests : DefaultCsharpTests,
            GoOut       = opt.GoOut.Length > 0 ? opt.GoOut : DefaultGoOut,
            TsOut       = opt.TsOut.Length > 0 ? opt.TsOut : DefaultTsOut,
            MermaidOut  = opt.MermaidOut.Length > 0 ? opt.MermaidOut : null,
        };
    }
}
