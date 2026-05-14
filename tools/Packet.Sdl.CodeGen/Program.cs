using Packet.Sdl.CodeGen.Csharp;
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

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--in":      inDir      = args[++i]; break;
                case "--out":     outDir     = args[++i]; break;
                case "--tests":   testsDir   = args[++i]; break;
                case "--mermaid": mermaidDir = args[++i]; break;
            }
        }

        try
        {
            return Run(inDir, outDir, testsDir, mermaidDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    private static int Run(string inDir, string outDir, string testsDir, string? mermaidDir)
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

        foreach (var page in pages)
        {
            var resolved = Resolver.Resolve(page);

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

            Console.WriteLine($"  ok  {page.SourcePath}  →  {emission.ClassName}.{{g.cs,g.Tests.cs,g.mmd}}");
        }

        // Subroutine pages: one .g.cs per page (no tests / mermaid).
        foreach (var subPage in subroutinePages)
        {
            var resolved = Resolver.Resolve(subPage);
            var probeModel = CsharpSubroutinesModel.From(resolved);
            var codePath = Path.Combine(outDir, probeModel.ClassName + ".g.cs");
            var emission = CsharpEmitter.EmitSubroutinePage(resolved, codePath);
            WriteIfChanged(codePath, emission.Code);
            writtenCode.Add(Path.GetFullPath(codePath));
            Console.WriteLine($"  ok  {subPage.SourcePath}  →  {emission.ClassName}.g.cs  (subroutines)");
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

        Console.WriteLine($"generated {pages.Count} state machine page(s), {subroutinePages.Count} subroutine page(s)");
        return 0;
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
