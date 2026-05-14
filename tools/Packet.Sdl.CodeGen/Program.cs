using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Scriban;
using Scriban.Runtime;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.CodeGen;

internal static class Program
{
    private static readonly Template CodeTemplate        = LoadTemplate("code.scriban-cs");
    private static readonly Template TestsTemplate       = LoadTemplate("tests.scriban-cs");
    private static readonly Template MermaidTemplate     = LoadTemplate("mermaid.scriban-mmd");
    private static readonly Template SubroutinesTemplate = LoadTemplate("subroutines.scriban-cs");

    private static readonly HashSet<string> ValidActionKinds = new(
        new[] { "signal_upper", "signal_lower", "processing", "subroutine", "internal_out" },
        StringComparer.Ordinal);

    private static readonly char[] GuardTokenSeparators = { ' ', '\t' };

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

        var events = LoadEventCatalog(Path.Combine(inDir, "events.yaml"));
        var actions = LoadActionCatalog(Path.Combine(inDir, "actions.yaml"));

        // Split YAML files into state-machine pages (sdl-machine schema)
        // and subroutine pages (sdl-subroutines schema). Both schemas use
        // the *.sdl.yaml extension; IsSubroutinePage detects by content.
        var yamlFiles = Directory.EnumerateFiles(inDir, "*.sdl.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var pages = new List<SdlPage>(yamlFiles.Count);
        var subroutinePages = new List<SubroutinePage>();
        foreach (var path in yamlFiles)
        {
            if (IsSubroutinePage(path))
            {
                subroutinePages.Add(LoadSubroutinePage(path));
                continue;
            }
            pages.Add(LoadPage(path));
        }

        var errors = new List<string>();
        foreach (var page in pages)
        {
            NormaliseActionVerbs(page, actions, errors);
            ValidatePage(page, events, errors);
        }
        foreach (var subPage in subroutinePages)
        {
            NormaliseSubroutineActionVerbs(subPage, actions, errors);
            ValidateSubroutinePage(subPage, errors);
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
            var model = TemplateModel.From(page);

            var codePath  = Path.Combine(outDir,   model.ClassName + ".g.cs");
            var testsPath = Path.Combine(testsDir, model.ClassName + ".g.Tests.cs");
            var mermaidPath = mermaidDir is null
                ? Path.Combine(
                    Path.GetDirectoryName(page.SourcePath)!,
                    Path.GetFileNameWithoutExtension(page.SourcePath).Replace(".sdl", string.Empty, StringComparison.Ordinal) + ".g.mmd")
                : Path.Combine(mermaidDir, model.ClassName + ".g.mmd");

            var codeText    = Render(CodeTemplate, model);
            var testsText   = Render(TestsTemplate, model);
            var mermaidText = Render(MermaidTemplate, model);

            // Parse-back validation: run the emitted C# through the
            // Roslyn parser. Anything malformed throws here, with line/col
            // pointing at the offending output — much friendlier than the
            // downstream `dotnet build` failure that the codegen would
            // otherwise produce.
            ValidateCSharp(codePath,  codeText);
            ValidateCSharp(testsPath, testsText);

            WriteIfChanged(codePath,    codeText);
            WriteIfChanged(testsPath,   testsText);
            WriteIfChanged(mermaidPath, mermaidText);

            writtenCode.Add(Path.GetFullPath(codePath));
            writtenTests.Add(Path.GetFullPath(testsPath));
            writtenMermaid.Add(Path.GetFullPath(mermaidPath));

            Console.WriteLine($"  ok  {page.SourcePath}  →  {model.ClassName}.{{g.cs,g.Tests.cs,g.mmd}}");
        }

        // Subroutine pages: emit one .g.cs per page (no tests / mermaid).
        foreach (var subPage in subroutinePages)
        {
            var subModel = SubroutinesTemplateModel.From(subPage);
            var codePath = Path.Combine(outDir, subModel.ClassName + ".g.cs");
            var codeText = Render(SubroutinesTemplate, subModel);
            ValidateCSharp(codePath, codeText);
            WriteIfChanged(codePath, codeText);
            writtenCode.Add(Path.GetFullPath(codePath));
            Console.WriteLine($"  ok  {subPage.SourcePath}  →  {subModel.ClassName}.g.cs  (subroutines)");
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

    // ─── Template loading + rendering ──────────────────────────────────

    private static Template LoadTemplate(string name)
    {
        var asm = typeof(Program).Assembly;
        var resourceName = $"Packet.Sdl.CodeGen.Templates.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"missing embedded template '{resourceName}'. Resources present: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var template = Template.Parse(text, sourceFilePath: name);
        if (template.HasErrors)
        {
            var msgs = string.Join("; ", template.Messages.Select(m => m.ToString()));
            throw new InvalidOperationException($"template '{name}' parse errors: {msgs}");
        }
        return template;
    }

    private static string Render<T>(Template template, T model)
    {
        // Pass the model through Scriban's POCO support with the snake_case
        // renamer so templates can use page.class_name, t.action_literals,
        // etc. instead of the C# PascalCase property names. Generic so the
        // same Render works for state-machine pages (TemplateModel) and
        // subroutine pages (SubroutinesTemplateModel).
        return template.Render(new { page = model }, member => StandardMemberRenamer.Default(member));
    }

    // ─── C# parse-back validation ──────────────────────────────────────

    private static void ValidateCSharp(string virtualPath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: virtualPath);
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (diagnostics.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append("generated C# for ").Append(virtualPath).Append(" failed to parse:\n");
        foreach (var d in diagnostics)
        {
            var line = d.Location.GetLineSpan().StartLinePosition;
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "  line {0} col {1}: {2}\n",
                line.Line + 1, line.Character + 1, d.GetMessage(CultureInfo.InvariantCulture));
        }
        // Show the offending output around the first error so the user can
        // spot the template glitch without grovelling through the file.
        var firstLine = diagnostics[0].Location.GetLineSpan().StartLinePosition.Line;
        var lines = source.Split('\n');
        int from = Math.Max(0, firstLine - 3);
        int to   = Math.Min(lines.Length - 1, firstLine + 3);
        sb.Append("context:\n");
        for (int i = from; i <= to; i++)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "  {0}{1,4}: {2}\n",
                i == firstLine ? "→ " : "  ",
                i + 1, lines[i]);
        }
        throw new InvalidOperationException(sb.ToString());
    }

    // ─── YAML loading / page validation ────────────────────────────────

    private static HashSet<string> LoadEventCatalog(string path)
    {
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var catalog = deserializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, items) in catalog)
        {
            foreach (var item in items)
            {
                set.Add(item);
            }
        }
        return set;
    }

    /// <summary>
    /// Load the canonical action-verb catalog from <c>spec-sdl/actions.yaml</c>.
    /// Soft mode: an absent file produces an empty catalog and codegen passes
    /// every verb through verbatim. When the file exists, every alias of every
    /// declared canonical name maps to the canonical, and the kind is recorded
    /// so the validator can flag transcriptions that draw the same verb under
    /// a different shape class.
    /// </summary>
    private static ActionCatalog LoadActionCatalog(string path)
    {
        var catalog = new ActionCatalog();
        if (!File.Exists(path)) return catalog;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<ActionCatalogEntry>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<ActionCatalogEntry>>(StringComparer.Ordinal);

        foreach (var (kind, entries) in raw)
        {
            if (!ValidActionKinds.Contains(kind))
                throw new InvalidDataException($"{path}: unknown action kind group `{kind}`. Valid: {string.Join(", ", ValidActionKinds)}.");

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"{path}: entry under `{kind}:` is missing `name:`");

                if (!catalog.CanonicalKind.TryAdd(entry.Name, kind))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` declared twice");

                // Canonical name is its own alias (identity passthrough).
                if (!catalog.CanonicalLookup.TryAdd(entry.Name, entry.Name))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` collides with an alias declared earlier");

                foreach (var alias in entry.Aliases ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        throw new InvalidDataException($"{path}: empty alias under canonical name `{entry.Name}`");
                    if (!catalog.CanonicalLookup.TryAdd(alias, entry.Name))
                        throw new InvalidDataException($"{path}: alias `{alias}` is claimed by two canonical names");
                    catalog.DeclaredAliases.Add(alias);
                }
            }
        }

        return catalog;
    }

    /// <summary>
    /// Walk a page's transitions and substitute every alias-spelling action
    /// verb with its canonical name. If the catalog is empty (no actions.yaml
    /// present), this is a no-op. Records validation errors when a canonical
    /// verb is drawn with a different <c>kind:</c> than the catalog declared.
    /// </summary>
    private static void NormaliseActionVerbs(SdlPage page, ActionCatalog catalog, List<string> errors)
    {
        if (catalog.CanonicalLookup.Count == 0) return;

        foreach (var t in page.Transitions)
        {
            if (t.Path is null) continue;
            NormalisePathSteps(page.SourcePath, t.Id, "path", t.Path, catalog, errors);
        }
    }

    private static void NormalisePathSteps(
        string loc, string transitionId, string contextLabel,
        List<SdlPathStep> path,
        ActionCatalog catalog,
        List<string> errors)
    {
        for (int i = 0; i < path.Count; i++)
        {
            var step = path[i];
            if (!string.IsNullOrWhiteSpace(step.Action) && catalog.CanonicalLookup.TryGetValue(step.Action!, out var canonical))
            {
                if (catalog.CanonicalKind.TryGetValue(canonical, out var expectedKind)
                    && !string.IsNullOrWhiteSpace(step.Kind)
                    && !string.Equals(step.Kind, expectedKind, StringComparison.Ordinal))
                {
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` (canonical `{canonical}`) " +
                               $"is drawn with kind `{step.Kind}` but spec-sdl/actions.yaml declares it as `{expectedKind}`. " +
                               "Either the YAML's kind is wrong, or the catalog is wrong.");
                }
                // Track alias usage so we can warn about dead aliases at the
                // end of the run. An alias is "used" if at least one YAML
                // verb literally matched it (i.e. step.Action wasn't already
                // the canonical name).
                if (!string.Equals(step.Action, canonical, StringComparison.Ordinal))
                {
                    catalog.SeenAliases.Add(step.Action!);
                }
                step.Action = canonical;
            }
            if (step.Body is not null)
            {
                NormalisePathSteps(loc, transitionId, contextLabel + $"[{i}].body", step.Body, catalog, errors);
            }
        }
    }

    private static SdlPage LoadPage(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var page = deserializer.Deserialize<SdlPage>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"could not parse {path}");
        page.SourcePath = path;
        page.Transitions ??= new();
        return page;
    }

    /// <summary>
    /// True if <paramref name="path"/> is a subroutine page (figc4.7 style)
    /// rather than a state-machine page. Subroutine pages use a top-level
    /// `subroutines:` key instead of `state:` + `transitions:`. Detected by
    /// a simple line-prefix scan rather than full YAML deserialisation so
    /// we don't pay parse cost twice.
    /// </summary>
    private static bool IsSubroutinePage(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("subroutines:", StringComparison.Ordinal)) return true;
            if (line.StartsWith("state:",       StringComparison.Ordinal)) return false;
            if (line.StartsWith("transitions:", StringComparison.Ordinal)) return false;
        }
        return false;
    }

    private static SubroutinePage LoadSubroutinePage(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var page = deserializer.Deserialize<SubroutinePage>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"could not parse {path}");
        page.SourcePath = path;
        return page;
    }

    /// <summary>
    /// Normalise action-verb spellings on a subroutine page. Same canonicalisation
    /// the state-machine page version applies; lifted into a separate method
    /// because the page types are different.
    /// </summary>
    private static void NormaliseSubroutineActionVerbs(SubroutinePage page, ActionCatalog catalog, List<string> errors)
    {
        if (catalog.CanonicalLookup.Count == 0) return;
        foreach (var sub in page.Subroutines)
        {
            foreach (var p in sub.Paths)
            {
                NormalisePathSteps(page.SourcePath, p.Id, "path", p.Path, catalog, errors);
            }
        }
    }

    /// <summary>
    /// Structural validation for subroutine pages. Enforces the same
    /// decision-branch / path-ID rules as state-machine page validation,
    /// adapted for subroutines (no `on:` event, no `next:` state).
    /// </summary>
    private static void ValidateSubroutinePage(SubroutinePage page, List<string> errors)
    {
        var loc = page.SourcePath;
        if (string.IsNullOrWhiteSpace(page.Machine)) errors.Add($"{loc}: missing `machine`");
        if (page.Source is null)                     errors.Add($"{loc}: missing `source`");
        if (page.Subroutines.Count == 0)             errors.Add($"{loc}: at least one subroutine required");

        var subNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in page.Subroutines)
        {
            if (string.IsNullOrWhiteSpace(sub.Name)) { errors.Add($"{loc}: subroutine missing `name`"); continue; }
            if (!subNames.Add(sub.Name)) errors.Add($"{loc}: duplicate subroutine name `{sub.Name}`");

            var decisionsById = new Dictionary<string, SdlDecision>(StringComparer.Ordinal);
            foreach (var d in sub.Decisions)
            {
                if (string.IsNullOrWhiteSpace(d.Id)) { errors.Add($"{loc}: {sub.Name}: decision missing `id`"); continue; }
                if (!decisionsById.TryAdd(d.Id, d))  errors.Add($"{loc}: {sub.Name}: duplicate decision id `{d.Id}`");
                if (string.IsNullOrWhiteSpace(d.Question))  errors.Add($"{loc}: {sub.Name}: decision `{d.Id}` missing `question`");
                if (string.IsNullOrWhiteSpace(d.Predicate)) errors.Add($"{loc}: {sub.Name}: decision `{d.Id}` missing `predicate`");
            }

            var pathIds = new HashSet<string>(StringComparer.Ordinal);
            if (sub.Paths.Count == 0) errors.Add($"{loc}: {sub.Name}: at least one path required");
            foreach (var p in sub.Paths)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) { errors.Add($"{loc}: {sub.Name}: path missing `id`"); continue; }
                if (!pathIds.Add(p.Id)) errors.Add($"{loc}: {sub.Name}: duplicate path id `{p.Id}`");

                if (p.Path is null || p.Path.Count == 0)
                {
                    // A decision-free, action-free path is fine (silent return).
                    continue;
                }

                ValidatePathSteps(loc, p.Id, "path", p.Path, decisionsById, errors);
            }
        }
    }

    private static void ValidatePage(SdlPage page, HashSet<string> events, List<string> errors)
    {
        var loc = page.SourcePath;
        if (string.IsNullOrWhiteSpace(page.Machine))   errors.Add($"{loc}: missing `machine`");
        if (string.IsNullOrWhiteSpace(page.State))     errors.Add($"{loc}: missing `state`");
        if (page.Source is null)                       errors.Add($"{loc}: missing `source`");
        if (page.Transitions.Count == 0)               errors.Add($"{loc}: at least one transition required");

        var decisionsById = new Dictionary<string, SdlDecision>(StringComparer.Ordinal);
        foreach (var d in page.Decisions)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) { errors.Add($"{loc}: decision missing `id`"); continue; }
            if (!decisionsById.TryAdd(d.Id, d))  errors.Add($"{loc}: duplicate decision id `{d.Id}`");
            if (string.IsNullOrWhiteSpace(d.Question))  errors.Add($"{loc}: decision `{d.Id}` missing `question`");
            if (string.IsNullOrWhiteSpace(d.Predicate)) errors.Add($"{loc}: decision `{d.Id}` missing `predicate`");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in page.Transitions)
        {
            if (string.IsNullOrWhiteSpace(t.Id))   errors.Add($"{loc}: transition missing `id`");
            else if (!ids.Add(t.Id))               errors.Add($"{loc}: duplicate transition id `{t.Id}`");

            if (string.IsNullOrWhiteSpace(t.On))   errors.Add($"{loc}: transition `{t.Id}` missing `on`");
            else if (events.Count > 0 && !events.Contains(t.On))
                errors.Add($"{loc}: transition `{t.Id}` references unknown event `{t.On}`. Add it to /spec-sdl/events.yaml.");

            if (string.IsNullOrWhiteSpace(t.Next)) errors.Add($"{loc}: transition `{t.Id}` missing `next`");

            if (t.Path is null)
            {
                errors.Add($"{loc}: transition `{t.Id}` missing `path`. Use `path: []` for a no-op column (input → state with no intermediate boxes).");
                continue;
            }

            // Empty path is allowed: figc4.1's "All Other Primitives" (from
            // lower layer) is drawn as input → state with no intermediate
            // boxes, and that's a valid SDL pattern.

            ValidatePathSteps(loc, t.Id, "path", t.Path, decisionsById, errors);
        }

        LintDecisionBranchCompleteness(page, decisionsById, errors);
        LintGuardOverlap(page, decisionsById, errors);
        LintReferences(page, errors);
    }

    /// <summary>
    /// Validates a path or loop body. Each step must be exactly one of:
    /// (decision + branch), (action + kind), or (loop_while + body).
    /// </summary>
    private static void ValidatePathSteps(
        string loc, string transitionId, string contextLabel,
        List<SdlPathStep> path,
        Dictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        for (int i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var hasDecision = !string.IsNullOrWhiteSpace(step.Decision);
            var hasAction   = !string.IsNullOrWhiteSpace(step.Action);
            var hasLoop     = !string.IsNullOrWhiteSpace(step.LoopWhile);
            var kindsSet = (hasDecision ? 1 : 0) + (hasAction ? 1 : 0) + (hasLoop ? 1 : 0);
            if (kindsSet != 1)
            {
                errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}]: must specify exactly one of `decision:`, `action:`, or `loop_while:`");
                continue;
            }

            if (hasDecision)
            {
                if (!decisionsById.ContainsKey(step.Decision!))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] references undefined decision `{step.Decision}`. Add it to the page-level decisions[].");
                if (step.Branch is not "Yes" and not "No")
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] branch must be 'Yes' or 'No' (was `{step.Branch}`)");
            }
            else if (hasAction)
            {
                if (string.IsNullOrWhiteSpace(step.Kind))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` missing `kind:` (one of signal_upper, signal_lower, processing, subroutine, internal_out)");
                else if (!ValidActionKinds.Contains(step.Kind))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` has unknown kind `{step.Kind}`. Valid: {string.Join(", ", ValidActionKinds)}.");
            }
            else // hasLoop
            {
                if (!decisionsById.ContainsKey(step.LoopWhile!))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] loop_while references undefined decision `{step.LoopWhile}`. Add it to the page-level decisions[].");
                if (step.Body is null || step.Body.Count == 0)
                {
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] loop_while missing or empty `body:`. Loop bodies must have at least one step.");
                }
                else
                {
                    // Restrict body to action steps only — nested decisions
                    // and nested loops require richer runtime semantics that
                    // we'd rather face when a real figure needs them.
                    for (int j = 0; j < step.Body.Count; j++)
                    {
                        var b = step.Body[j];
                        if (string.IsNullOrWhiteSpace(b.Action))
                        {
                            errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}].body[{j}] must be an action step. Nested decisions and loops inside a loop body are not supported by the current codegen; refactor as a subroutine if needed.");
                        }
                        else if (string.IsNullOrWhiteSpace(b.Kind) || !ValidActionKinds.Contains(b.Kind))
                        {
                            errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}].body[{j}] action `{b.Action}` has invalid or missing `kind:`.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates per-transition references against the page's pinned_refs
    /// table. Every code citation must have a matching source pinned at
    /// the page level (with a repo URL and commit hash) so line numbers
    /// have a stable reference point. spec_prose references are validated
    /// for shape: cite is required; quote is optional.
    /// </summary>
    private static void LintReferences(SdlPage page, List<string> errors)
    {
        var pinnedSources = page.PinnedRefs?.Keys.ToHashSet(StringComparer.Ordinal)
                            ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var pin in page.PinnedRefs ?? new())
        {
            if (string.IsNullOrWhiteSpace(pin.Value.Repo))
                errors.Add($"{page.SourcePath}: pinned_refs[{pin.Key}] missing `repo`.");
            if (string.IsNullOrWhiteSpace(pin.Value.Commit))
                errors.Add($"{page.SourcePath}: pinned_refs[{pin.Key}] missing `commit`. Pin to a specific commit hash so line numbers stay valid.");
        }

        foreach (var t in page.Transitions)
        {
            foreach (var (r, i) in (t.References ?? new()).Select((r, i) => (r, i)))
            {
                if (string.IsNullOrWhiteSpace(r.Source))
                {
                    errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] missing `source`.");
                    continue;
                }

                if (r.Source == "spec_prose")
                {
                    if (string.IsNullOrWhiteSpace(r.Cite))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (spec_prose) missing `cite` (e.g. '§6.3.5 ¶1').");
                    // path/function/line should not appear on spec_prose
                    if (!string.IsNullOrWhiteSpace(r.Path) || !string.IsNullOrWhiteSpace(r.Function) || r.Line is not null)
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (spec_prose) should not have path/function/line — those are for code citations.");
                }
                else
                {
                    if (!pinnedSources.Contains(r.Source))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] source `{r.Source}` is not declared in pinned_refs. Add an entry to the page-level pinned_refs table with the repo URL and a pinned commit hash.");
                    if (string.IsNullOrWhiteSpace(r.Path))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) missing `path`.");
                    if (string.IsNullOrWhiteSpace(r.Function))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) missing `function` (primary anchor; line numbers drift, function names survive refactors).");
                    if (!string.IsNullOrWhiteSpace(r.Cite) || !string.IsNullOrWhiteSpace(r.Quote))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) has cite/quote — those are for spec_prose references.");
                }
            }
        }
    }

    // ─── Lints ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every decision defined on a page must appear with both "Yes" and "No"
    /// branches across some transition pair. A decision used with only one
    /// branch means the figure's other branch is missing from the
    /// transcription — almost always a transcription slip rather than an
    /// intentional one-armed diamond.
    /// </summary>
    private static void LintDecisionBranchCompleteness(
        SdlPage page,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        var branchUses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in page.Transitions)
        {
            foreach (var step in t.Path ?? new())
            {
                if (string.IsNullOrWhiteSpace(step.Decision)) continue;
                if (!branchUses.TryGetValue(step.Decision!, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.Ordinal);
                    branchUses[step.Decision!] = seen;
                }
                if (!string.IsNullOrWhiteSpace(step.Branch))
                {
                    seen.Add(step.Branch!);
                }
            }
        }

        foreach (var id in decisionsById.Keys)
        {
            if (!branchUses.TryGetValue(id, out var seen))
            {
                errors.Add($"{page.SourcePath}: decision `{id}` is declared but never referenced in any transition path.");
                continue;
            }
            if (!seen.Contains("Yes"))
                errors.Add($"{page.SourcePath}: decision `{id}` missing 'Yes' branch in any transition (seen: {string.Join(", ", seen.OrderBy(s => s, StringComparer.Ordinal))}). Add the missing column or mark `coverage: partial` with a verification_pending note.");
            if (!seen.Contains("No"))
                errors.Add($"{page.SourcePath}: decision `{id}` missing 'No' branch in any transition (seen: {string.Join(", ", seen.OrderBy(s => s, StringComparer.Ordinal))}). Add the missing column or mark `coverage: partial` with a verification_pending note.");
        }
    }

    /// <summary>
    /// Two transitions sharing the same <c>on:</c> event for one state must
    /// have provably-disjoint guards — otherwise the orchestrator silently
    /// picks the first match and the second is dead code. Disjointness here
    /// is decided by literal contradiction: if guard A contains <c>X</c>
    /// positively and guard B contains <c>X</c> negatively (or vice versa),
    /// they're disjoint. Guards with <c>or</c> are skipped (out of scope for
    /// this lint).
    /// </summary>
    private static void LintGuardOverlap(
        SdlPage page,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        // Compile each transition's path into a guard-literal set for fast
        // disjointness comparison. Skip transitions where the path-compile
        // produced an `or` (we don't reason about disjunctive guards).
        var compiled = page.Transitions
            .Where(t => t.Path is not null)
            .Select(t => (t, lits: CompileGuardLiterals(t, decisionsById)))
            .ToList();

        var byEvent = compiled.GroupBy(x => x.t.On, StringComparer.Ordinal);
        foreach (var grp in byEvent)
        {
            var list = grp.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].lits is null || list[j].lits is null) continue;
                    if (!AreDisjoint(list[i].lits!, list[j].lits!))
                    {
                        errors.Add($"{page.SourcePath}: transitions `{list[i].t.Id}` and `{list[j].t.Id}` both fire on event `{grp.Key}` with non-disjoint guards. The orchestrator will silently pick the first match. Add a contradicting decision branch on one path, or merge the transitions.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the set of literals making up the conjunctive guard for
    /// <paramref name="t"/>, or <c>null</c> if the path includes a decision
    /// whose canonical predicate contains <c>or</c> (disjunctive guards are
    /// out of scope for the overlap check).
    /// </summary>
    private static HashSet<(string Ident, bool Positive)>? CompileGuardLiterals(
        SdlTransition t,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var lits = new HashSet<(string, bool)>();
        foreach (var step in t.Path ?? new())
        {
            if (string.IsNullOrWhiteSpace(step.Decision)) continue;
            if (!decisionsById.TryGetValue(step.Decision!, out var decision)) continue;
            var predicate = decision.Predicate ?? string.Empty;
            if (predicate.Contains(" or ", StringComparison.Ordinal))
            {
                return null;
            }
            // Predicate is a conjunction of (possibly negated) identifiers.
            // Tokenize on whitespace; walk through "and"-separated literals.
            var tokens = predicate.Split(GuardTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            while (idx < tokens.Length)
            {
                if (tokens[idx] == "and") { idx++; continue; }
                bool positive = true;
                if (tokens[idx] == "not") { positive = false; idx++; }
                if (idx >= tokens.Length) break;
                var ident = tokens[idx++];
                if (step.Branch == "No") positive = !positive;
                lits.Add((ident, positive));
            }
        }
        return lits;
    }

    private static bool AreDisjoint(
        HashSet<(string Ident, bool Positive)> a,
        HashSet<(string Ident, bool Positive)> b)
    {
        // Two conjunctive literal sets are disjoint iff one literal in a has
        // its negation in b (or vice versa). Empty sets (unguarded) are not
        // disjoint with anything — they always fire.
        if (a.Count == 0 || b.Count == 0) return false;
        foreach (var lit in a)
        {
            if (b.Contains((lit.Ident, !lit.Positive))) return true;
        }
        return false;
    }

    // ─── File IO ───────────────────────────────────────────────────────

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

// ─── YAML model ────────────────────────────────────────────────────────

internal sealed class SdlPage
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

internal sealed class SdlSourceYaml
{
    public string Spec { get; set; } = "";
    public string Figure { get; set; } = "";
    public string? Url { get; set; }
}

internal sealed class SdlDecision
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Predicate { get; set; } = "";
}

internal sealed class SdlTransition
{
    public string Id { get; set; } = "";
    public string On { get; set; } = "";
    public List<SdlPathStep> Path { get; set; } = new();
    public string Next { get; set; } = "";
    public string? Notes { get; set; }
    public List<SdlReference>? References { get; set; }
}

/// <summary>
/// One cross-reference citation. spec_prose entries use Cite + Quote; code
/// citations use Path, Function, Line, Note. Source must be 'spec_prose' or
/// a key in the page-level pinned_refs table.
/// </summary>
internal sealed class SdlReference
{
    public string Source { get; set; } = "";
    public string? Cite { get; set; }
    public string? Quote { get; set; }
    public string? Path { get; set; }
    public string? Function { get; set; }
    public int? Line { get; set; }
    public string? Note { get; set; }
}

internal sealed class SdlPinnedRef
{
    public string Repo { get; set; } = "";
    public string Commit { get; set; } = "";
}

/// <summary>
/// One entry under a kind group in <c>spec-sdl/actions.yaml</c>. The
/// <see cref="Name"/> is the canonical spelling emitted into <c>.g.cs</c>;
/// <see cref="Aliases"/> are alternate figure-verbatim spellings normalised to
/// the canonical at codegen time.
/// </summary>
internal sealed class ActionCatalogEntry
{
    public string Name { get; set; } = "";
    public List<string>? Aliases { get; set; }
}

/// <summary>
/// Resolved action-verb catalog. Built from <c>spec-sdl/actions.yaml</c>;
/// empty when the file is absent (soft passthrough mode).
/// </summary>
internal sealed class ActionCatalog
{
    /// <summary>Map from any known spelling (canonical or alias) to canonical name.</summary>
    public Dictionary<string, string> CanonicalLookup { get; } = new(StringComparer.Ordinal);

    /// <summary>Map from canonical name to its declared SDL kind (signal_upper, signal_lower, etc.).</summary>
    public Dictionary<string, string> CanonicalKind { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every declared alias (non-canonical spelling). Populated at load time.
    /// After all YAMLs have been normalised, anything in this set that
    /// wasn't <see cref="SeenAliases"/>-touched is a dead alias and is
    /// reported as a build error.
    /// </summary>
    public HashSet<string> DeclaredAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Aliases that <see cref="NormalisePathSteps"/> actually substituted on at least one verb.</summary>
    public HashSet<string> SeenAliases { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One step in a transition's path. Exactly one of (Decision+Branch),
/// (Action+Kind), or (LoopWhile+Body) is populated; the validator rejects
/// malformed steps.
/// </summary>
internal sealed class SdlPathStep
{
    public string? Decision { get; set; }
    public string? Branch { get; set; }
    public string? Action { get; set; }
    public string? Kind { get; set; }
    [YamlMember(Alias = "loop_while", ApplyNamingConventions = false)]
    public string? LoopWhile { get; set; }
    public List<SdlPathStep>? Body { get; set; }
}

// ─── Template-facing projection ────────────────────────────────────────

internal sealed class TemplateModel
{
    public string Machine { get; set; } = "";
    public string State { get; set; } = "";
    public string Coverage { get; set; } = "complete";
    public string ClassName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceSpec { get; set; } = "";
    public string SourceFigure { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string SourceUrlLiteral { get; set; } = "null";
    public List<TransitionModel> Transitions { get; set; } = new();

    public static TemplateModel From(SdlPage page)
    {
        var classBase = Pascal(page.Machine) + "_" + page.State;
        var decisionsById = page.Decisions.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new TemplateModel
        {
            Machine          = page.Machine,
            State            = page.State,
            Coverage         = page.Coverage,
            ClassName        = classBase,
            SourcePath       = page.SourcePath,
            SourceSpec       = page.Source!.Spec,
            SourceFigure     = page.Source.Figure,
            SourceUrl        = page.Source.Url,
            SourceUrlLiteral = page.Source.Url is null ? "null" : CSharpStringLiteral(page.Source.Url),
            Transitions      = page.Transitions.Select(t => TransitionModel.From(t, decisionsById)).ToList(),
        };
    }

    private static string Pascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    internal static string CSharpStringLiteral(string s)
    {
        var escaped = s
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r",  StringComparison.Ordinal)
            .Replace("\n", "\\n",  StringComparison.Ordinal)
            .Replace("\t", "\\t",  StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    internal static string KindEnumLiteral(string kind) => kind switch
    {
        "signal_upper" => "ActionKind.SignalUpper",
        "signal_lower" => "ActionKind.SignalLower",
        "processing"   => "ActionKind.Processing",
        "subroutine"   => "ActionKind.Subroutine",
        "internal_out" => "ActionKind.InternalOut",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };
}

internal sealed class TransitionModel
{
    public string Id { get; set; } = "";
    public string On { get; set; } = "";
    public string? Guard { get; set; }
    public string GuardLiteral { get; set; } = "null";
    public string Next { get; set; } = "";
    public string? Notes { get; set; }
    public string NotesLiteral { get; set; } = "null";
    public List<ActionModel> Actions { get; set; } = new();
    public string ActionsCsv { get; set; } = "";
    public string ReferencesCsv { get; set; } = "";
    public string LoopsCsv { get; set; } = "";
    public string EdgeLabel { get; set; } = "";

    public static TransitionModel From(SdlTransition t, IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        // Compile path[] into the runtime's flat (guard, actions[], loops[]) triple.
        // - Each {decision, branch} step contributes a predicate to the guard:
        //   "Yes" → bare predicate; "No" → "not " + predicate.
        // - Each {action, kind} step contributes to the action list.
        // - Each {loop_while, body} step expands its body inline into the
        //   action list (one iteration), AND records the range as a LoopRange
        //   so loop-aware consumers can iterate while the predicate is true.
        //   Body actions and any nested decisions are walked recursively.
        var predicates = new List<string>();
        var actions = new List<ActionModel>();
        var loops = new List<LoopModel>();
        WalkPath(t.Path, decisionsById, predicates, actions, loops);

        var guard = predicates.Count == 0 ? null : string.Join(" and ", predicates);
        var refs = (t.References ?? new()).Select(r =>
            "new ImplementationReference(" +
            $"Source: {TemplateModel.CSharpStringLiteral(r.Source)}, " +
            $"Cite: {NullOrLiteral(r.Cite)}, " +
            $"Quote: {NullOrLiteral(r.Quote)}, " +
            $"Path: {NullOrLiteral(r.Path)}, " +
            $"Function: {NullOrLiteral(r.Function)}, " +
            $"Line: {(r.Line is null ? "null" : r.Line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}, " +
            $"Note: {NullOrLiteral(r.Note)})").ToList();

        return new TransitionModel
        {
            Id            = t.Id,
            On            = t.On,
            Guard         = guard,
            GuardLiteral  = guard is null ? "null" : TemplateModel.CSharpStringLiteral(guard),
            Next          = t.Next,
            Notes         = t.Notes,
            NotesLiteral  = t.Notes is null ? "null" : TemplateModel.CSharpStringLiteral(t.Notes),
            Actions       = actions,
            ActionsCsv    = string.Join(", ", actions.Select(a =>
                $"new ActionStep({TemplateModel.CSharpStringLiteral(a.Verb)}, {TemplateModel.KindEnumLiteral(a.Kind)})")),
            ReferencesCsv = string.Join(", ", refs),
            LoopsCsv      = string.Join(", ", loops.Select(l =>
                $"new LoopRange({l.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{l.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{TemplateModel.CSharpStringLiteral(l.Predicate)})")),
            EdgeLabel     = BuildMermaidEdgeLabel(t.Id, t.On, guard, actions),
        };
    }

    /// <summary>Recursively walk the path, accumulating guard predicates, actions, and loop ranges.</summary>
    private static void WalkPath(
        List<SdlPathStep> path,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> predicates,
        List<ActionModel> actions,
        List<LoopModel> loops)
    {
        foreach (var step in path)
        {
            if (!string.IsNullOrWhiteSpace(step.Decision))
            {
                var decision = decisionsById[step.Decision!];
                predicates.Add(step.Branch == "Yes" ? decision.Predicate : "not " + decision.Predicate);
            }
            else if (!string.IsNullOrWhiteSpace(step.LoopWhile))
            {
                var loopGuard = decisionsById[step.LoopWhile!];
                var startIndex = actions.Count;
                // Body is action-only (validator enforces). The body's actions
                // are inlined into the flat list as one iteration; the loop
                // predicate gates re-execution at runtime and is NOT added to
                // the transition's overall guard (the loop is entered with
                // zero iterations if the predicate starts false).
                foreach (var bodyStep in step.Body!)
                {
                    if (!string.IsNullOrWhiteSpace(bodyStep.Action))
                    {
                        actions.Add(new ActionModel(bodyStep.Action!, bodyStep.Kind!));
                    }
                    // Decision/nested-loop steps in a body are rejected by the
                    // validator; ignored here defensively.
                }
                var length = actions.Count - startIndex;
                loops.Add(new LoopModel(startIndex, length, loopGuard.Predicate));
            }
            else
            {
                actions.Add(new ActionModel(step.Action!, step.Kind!));
            }
        }
    }

    private static string NullOrLiteral(string? s) =>
        s is null ? "null" : TemplateModel.CSharpStringLiteral(s);

    private static string BuildMermaidEdgeLabel(string id, string on, string? guard, List<ActionModel> actions)
    {
        var parts = new List<string> { id, "on: " + EscapeMermaid(on) };
        if (!string.IsNullOrEmpty(guard))
        {
            parts.Add("[" + EscapeMermaid(guard) + "]");
        }
        foreach (var a in actions)
        {
            parts.Add("/ " + KindIndicator(a.Kind) + " " + EscapeMermaid(a.Verb));
        }
        return string.Join("<br/>", parts);
    }

    private static string KindIndicator(string kind) => kind switch
    {
        "signal_upper" => "↑",
        "signal_lower" => "↓",
        "processing"   => "·",
        "subroutine"   => "→()",
        "internal_out" => "⇢",
        _ => "?",
    };

    private static string EscapeMermaid(string s) => s
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("\n", " ",      StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);
}

internal sealed record ActionModel(string Verb, string Kind)
{
    public string VerbLiteral => TemplateModel.CSharpStringLiteral(Verb);
    public string KindEnum    => TemplateModel.KindEnumLiteral(Kind);
}

internal sealed record LoopModel(int Start, int Length, string Predicate);

// ─── Subroutine page YAML model ────────────────────────────────────────

internal sealed class SubroutinePage
{
    public string Machine { get; set; } = "";
    public SdlSourceYaml? Source { get; set; }
    [YamlMember(Alias = "pinned_refs", ApplyNamingConventions = false)]
    public Dictionary<string, SdlPinnedRef>? PinnedRefs { get; set; }
    public List<SubroutineYamlEntry> Subroutines { get; set; } = new();
    [YamlIgnore] public string SourcePath { get; set; } = "";
}

internal sealed class SubroutineYamlEntry
{
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public List<string>? Variables { get; set; }
    public List<SdlDecision> Decisions { get; set; } = new();
    public List<SubroutinePathYaml> Paths { get; set; } = new();
}

internal sealed class SubroutinePathYaml
{
    public string Id { get; set; } = "";
    public List<SdlPathStep> Path { get; set; } = new();
    public string? Notes { get; set; }
    public List<SdlReference>? References { get; set; }
}

// ─── Subroutine page template projection ──────────────────────────────

internal sealed class SubroutinesTemplateModel
{
    public string Machine { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceSpec { get; set; } = "";
    public string SourceFigure { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string SourceUrlLiteral { get; set; } = "null";
    public List<SubroutineModel> Subroutines { get; set; } = new();

    public static SubroutinesTemplateModel From(SubroutinePage page)
    {
        // ClassName: derive from the file name so a future second page
        // (e.g. Link_Mux subroutines) doesn't collide. Use the parent
        // file stem capitalised.
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var className = TitleSnakeToPascal(page.Machine) + "_" + TitleSnakeToPascal(fileStem);

        return new SubroutinesTemplateModel
        {
            Machine          = page.Machine,
            ClassName        = className,
            SourcePath       = page.SourcePath,
            SourceSpec       = page.Source!.Spec,
            SourceFigure     = page.Source.Figure,
            SourceUrl        = page.Source.Url,
            SourceUrlLiteral = page.Source.Url is null ? "null" : TemplateModel.CSharpStringLiteral(page.Source.Url),
            Subroutines      = page.Subroutines.Select(SubroutineModel.From).ToList(),
        };
    }

    private static string TitleSnakeToPascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

internal sealed class SubroutineModel
{
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string NotesLiteral { get; set; } = "null";
    public string ReferencesCsv { get; set; } = "";
    public List<SubroutinePathModel> Paths { get; set; } = new();

    public static SubroutineModel From(SubroutineYamlEntry s)
    {
        var decisionsById = s.Decisions.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new SubroutineModel
        {
            Name          = s.Name,
            Notes         = s.Notes,
            NotesLiteral  = s.Notes is null ? "null" : TemplateModel.CSharpStringLiteral(s.Notes),
            ReferencesCsv = "",   // subroutine-level references are rare; emit empty array for now.
            Paths         = s.Paths.Select(p => SubroutinePathModel.From(p, decisionsById)).ToList(),
        };
    }
}

internal sealed class SubroutinePathModel
{
    public string Id { get; set; } = "";
    public string? Guard { get; set; }
    public string GuardLiteral { get; set; } = "null";
    public string? Notes { get; set; }
    public string NotesLiteral { get; set; } = "null";
    public string ActionsCsv { get; set; } = "";
    public string ReferencesCsv { get; set; } = "";
    public string LoopsCsv { get; set; } = "";

    public static SubroutinePathModel From(SubroutinePathYaml p, IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        // Same path-walking logic as TransitionModel — guard is the AND of
        // decision-branch outcomes; actions are accumulated; loops are
        // recorded as ranges into the flat action list.
        var predicates = new List<string>();
        var actions = new List<ActionModel>();
        var loops = new List<LoopModel>();
        WalkSubroutinePath(p.Path, decisionsById, predicates, actions, loops);

        var guard = predicates.Count == 0 ? null : string.Join(" and ", predicates);
        var refs = (p.References ?? new()).Select(r =>
            "new ImplementationReference(" +
            $"Source: {TemplateModel.CSharpStringLiteral(r.Source)}, " +
            $"Cite: {NullOrLiteralStatic(r.Cite)}, " +
            $"Quote: {NullOrLiteralStatic(r.Quote)}, " +
            $"Path: {NullOrLiteralStatic(r.Path)}, " +
            $"Function: {NullOrLiteralStatic(r.Function)}, " +
            $"Line: {(r.Line is null ? "null" : r.Line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}, " +
            $"Note: {NullOrLiteralStatic(r.Note)})").ToList();

        return new SubroutinePathModel
        {
            Id            = p.Id,
            Guard         = guard,
            GuardLiteral  = guard is null ? "null" : TemplateModel.CSharpStringLiteral(guard),
            Notes         = p.Notes,
            NotesLiteral  = p.Notes is null ? "null" : TemplateModel.CSharpStringLiteral(p.Notes),
            ActionsCsv    = string.Join(", ", actions.Select(a =>
                $"new ActionStep({TemplateModel.CSharpStringLiteral(a.Verb)}, {TemplateModel.KindEnumLiteral(a.Kind)})")),
            ReferencesCsv = string.Join(", ", refs),
            LoopsCsv      = string.Join(", ", loops.Select(l =>
                $"new LoopRange({l.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{l.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{TemplateModel.CSharpStringLiteral(l.Predicate)})")),
        };
    }

    private static string NullOrLiteralStatic(string? s) =>
        s is null ? "null" : TemplateModel.CSharpStringLiteral(s);

    private static void WalkSubroutinePath(
        List<SdlPathStep> path,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> predicates,
        List<ActionModel> actions,
        List<LoopModel> loops)
    {
        foreach (var step in path)
        {
            if (!string.IsNullOrWhiteSpace(step.Decision))
            {
                var d = decisionsById[step.Decision!];
                predicates.Add(step.Branch == "Yes" ? d.Predicate : "not " + d.Predicate);
            }
            else if (!string.IsNullOrWhiteSpace(step.LoopWhile))
            {
                var loopGuard = decisionsById[step.LoopWhile!];
                var startIndex = actions.Count;
                foreach (var bodyStep in step.Body!)
                {
                    if (!string.IsNullOrWhiteSpace(bodyStep.Action))
                    {
                        actions.Add(new ActionModel(bodyStep.Action!, bodyStep.Kind!));
                    }
                }
                var length = actions.Count - startIndex;
                loops.Add(new LoopModel(startIndex, length, loopGuard.Predicate));
            }
            else
            {
                actions.Add(new ActionModel(step.Action!, step.Kind!));
            }
        }
    }
}
