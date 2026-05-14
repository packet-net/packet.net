using Packet.Sdl.CodeGen.Csharp;
using Packet.Sdl.CodeGen.Go;
using Packet.Sdl.CodeGen.Ts;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen;

/// <summary>
/// Thin driver for the SDL codegen pipeline. Loads YAML pages from
/// <c>spec-sdl/</c>, validates them, resolves into the language-neutral
/// IR (<see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/>),
/// hands them to <see cref="CsharpEmitter"/>, and writes the emitted
/// <c>.g.cs</c> / <c>.g.Tests.cs</c> / <c>.g.mmd</c> files. A second
/// backend (Go) plugs in alongside the C# one without touching this
/// orchestration.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        string inDir = "spec-sdl";
        string outDir = "src/Packet.Ax25.Sdl";
        string testsDir = "tests/Packet.Ax25.Conformance.Tests";
        string? mermaidDir = null;  // null = "alongside the source YAML"
        string? goDir = null;       // null = don't emit Go
        string? tsDir = null;       // null = don't emit TS

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--in":      inDir      = args[++i]; break;
                case "--out":     outDir     = args[++i]; break;
                case "--tests":   testsDir   = args[++i]; break;
                case "--mermaid": mermaidDir = args[++i]; break;
                case "--go":      goDir      = args[++i]; break;
                case "--ts":      tsDir      = args[++i]; break;
            }
        }

        // Default: emit Go / TS when the conventional package directory
        // exists, so a normal `dotnet run --project tools/Packet.Sdl.CodeGen`
        // keeps every backend in sync without an explicit flag.
        if (goDir is null && Directory.Exists("go-spec/ax25sdl")) goDir = "go-spec/ax25sdl";
        if (tsDir is null && Directory.Exists("ts-spec/src/ax25sdl")) tsDir = "ts-spec/src/ax25sdl";

        try
        {
            return Run(inDir, outDir, testsDir, mermaidDir, goDir, tsDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    private static int Run(string inDir, string outDir, string testsDir, string? mermaidDir, string? goDir, string? tsDir)
    {
        if (!Directory.Exists(inDir))
        {
            Console.Error.WriteLine($"::warning::SDL input directory '{inDir}' does not exist; nothing to do.");
            return 0;
        }

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(testsDir);

        var events  = EventCatalog.Load(Path.Combine(inDir, "events.yaml"));
        var actions = ActionCatalog.Load(Path.Combine(inDir, "actions.yaml"));

        // Split YAML files into state-machine pages (sdl-machine schema)
        // and subroutine pages (sdl-subroutines schema). Both share the
        // *.sdl.yaml extension; Loader.IsSubroutinePage routes by content.
        var yamlFiles = Directory.EnumerateFiles(inDir, "*.sdl.yaml", SearchOption.AllDirectories)
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

        var writtenCode    = new HashSet<string>(StringComparer.Ordinal);
        var writtenTests   = new HashSet<string>(StringComparer.Ordinal);
        var writtenMermaid = new HashSet<string>(StringComparer.Ordinal);
        var writtenGo      = new HashSet<string>(StringComparer.Ordinal);
        var writtenTs      = new HashSet<string>(StringComparer.Ordinal);

        // Collect resolved IR so EmitIndex (TS) sees the full set in
        // deterministic order at the end of the run.
        var resolvedPages = new List<ResolvedPage>(pages.Count);
        var resolvedSubPages = new List<ResolvedSubroutinesPage>(subroutinePages.Count);

        if (goDir is not null) Directory.CreateDirectory(goDir);
        if (tsDir is not null) Directory.CreateDirectory(tsDir);

        foreach (var page in pages)
        {
            var resolved = Resolver.Resolve(page);
            resolvedPages.Add(resolved);

            // Class name comes back from the emitter so we don't need to
            // duplicate the snake-to-Pascal naming convention here.
            var probeModel = CsharpStateModel.From(resolved);
            var codePath    = Path.Combine(outDir,   probeModel.ClassName + ".g.cs");
            var testsPath   = Path.Combine(testsDir, probeModel.ClassName + ".g.Tests.cs");
            var mermaidPath = mermaidDir is null
                ? Path.Combine(
                    Path.GetDirectoryName(page.SourcePath)!,
                    Path.GetFileNameWithoutExtension(page.SourcePath).Replace(".sdl", string.Empty, StringComparison.Ordinal) + ".g.mmd")
                : Path.Combine(mermaidDir, probeModel.ClassName + ".g.mmd");

            var emission = CsharpEmitter.EmitStatePage(resolved, codePath, testsPath);

            WriteIfChanged(codePath,    emission.Code);
            WriteIfChanged(testsPath,   emission.Tests);
            WriteIfChanged(mermaidPath, emission.Mermaid);

            writtenCode.Add(Path.GetFullPath(codePath));
            writtenTests.Add(Path.GetFullPath(testsPath));
            writtenMermaid.Add(Path.GetFullPath(mermaidPath));

            if (goDir is not null)
            {
                var go = GoEmitter.EmitStatePage(resolved);
                var goPath = Path.Combine(goDir, go.FileName);
                WriteIfChanged(goPath, go.Content);
                writtenGo.Add(Path.GetFullPath(goPath));
            }

            if (tsDir is not null)
            {
                var ts = TsEmitter.EmitStatePage(resolved);
                var tsPath = Path.Combine(tsDir, ts.FileName);
                WriteIfChanged(tsPath, ts.Content);
                writtenTs.Add(Path.GetFullPath(tsPath));
            }

            Console.WriteLine($"  ok  {page.SourcePath}  →  {emission.ClassName}.{{g.cs,g.Tests.cs,g.mmd}}{(goDir is not null ? " + .g.go" : "")}{(tsDir is not null ? " + .g.ts" : "")}");
        }

        // Subroutine pages: one .g.cs per page (no tests / mermaid).
        foreach (var subPage in subroutinePages)
        {
            var resolved = Resolver.Resolve(subPage);
            resolvedSubPages.Add(resolved);
            var probeModel = CsharpSubroutinesModel.From(resolved);
            var codePath = Path.Combine(outDir, probeModel.ClassName + ".g.cs");
            var emission = CsharpEmitter.EmitSubroutinePage(resolved, codePath);
            WriteIfChanged(codePath, emission.Code);
            writtenCode.Add(Path.GetFullPath(codePath));

            if (goDir is not null)
            {
                var go = GoEmitter.EmitSubroutinePage(resolved);
                var goPath = Path.Combine(goDir, go.FileName);
                WriteIfChanged(goPath, go.Content);
                writtenGo.Add(Path.GetFullPath(goPath));
            }

            if (tsDir is not null)
            {
                var ts = TsEmitter.EmitSubroutinePage(resolved);
                var tsPath = Path.Combine(tsDir, ts.FileName);
                WriteIfChanged(tsPath, ts.Content);
                writtenTs.Add(Path.GetFullPath(tsPath));
            }

            Console.WriteLine($"  ok  {subPage.SourcePath}  →  {emission.ClassName}.g.cs  (subroutines){(goDir is not null ? " + .g.go" : "")}{(tsDir is not null ? " + .g.ts" : "")}");
        }

        // TS package needs an index.ts that re-exports every page so
        // consumers can `import { DataLinkConnected } from "ax25sdl"`.
        // Go doesn't need this — every var declared in a package is
        // visible from the package namespace already.
        if (tsDir is not null)
        {
            var indexPath = Path.Combine(tsDir, "index.ts");
            WriteIfChanged(indexPath, TsEmitter.EmitIndex(resolvedPages, resolvedSubPages));
            writtenTs.Add(Path.GetFullPath(indexPath));
        }

        // Tidy stale generated files (someone deleted a *.sdl.yaml).
        CleanStaleFiles(outDir,   "*.g.cs",       writtenCode);
        CleanStaleFiles(testsDir, "*.g.Tests.cs", writtenTests);
        if (mermaidDir is not null)
        {
            CleanStaleFiles(mermaidDir, "*.g.mmd", writtenMermaid);
        }
        else
        {
            foreach (var dir in Directory.EnumerateDirectories(inDir, "*", SearchOption.AllDirectories))
            {
                CleanStaleFiles(dir, "*.g.mmd", writtenMermaid);
            }
        }
        if (goDir is not null)
        {
            CleanStaleFiles(goDir, "*.g.go", writtenGo);
            RunGofmt(goDir);
        }
        if (tsDir is not null)
        {
            // index.ts is generated too; the CleanStaleFiles pass scopes
            // by *.g.ts so it leaves index.ts (and types.ts) alone.
            CleanStaleFiles(tsDir, "*.g.ts", writtenTs);
        }

        var extras = (goDir is not null ? " (+ Go)" : "") + (tsDir is not null ? " (+ TS)" : "");
        Console.WriteLine($"generated {pages.Count} state machine page(s), {subroutinePages.Count} subroutine page(s){extras}");
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
