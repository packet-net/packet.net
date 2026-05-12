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
    private static readonly Template CodeTemplate    = LoadTemplate("code.scriban-cs");
    private static readonly Template TestsTemplate   = LoadTemplate("tests.scriban-cs");
    private static readonly Template MermaidTemplate = LoadTemplate("mermaid.scriban-mmd");

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

        var pages = Directory.EnumerateFiles(inDir, "*.sdl.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(LoadPage)
            .ToList();

        var errors = new List<string>();
        foreach (var page in pages)
        {
            ValidatePage(page, events, errors);
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

        Console.WriteLine($"generated {pages.Count} state machine page(s)");
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

    private static string Render(Template template, TemplateModel model)
    {
        // Pass the model through Scriban's POCO support with the snake_case
        // renamer so templates can use page.class_name, t.action_literals,
        // etc. instead of the C# PascalCase property names.
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

    private static void ValidatePage(SdlPage page, HashSet<string> events, List<string> errors)
    {
        var loc = page.SourcePath;
        if (string.IsNullOrWhiteSpace(page.Machine))   errors.Add($"{loc}: missing `machine`");
        if (string.IsNullOrWhiteSpace(page.State))     errors.Add($"{loc}: missing `state`");
        if (page.Source is null)                       errors.Add($"{loc}: missing `source`");
        if (page.Transitions.Count == 0)               errors.Add($"{loc}: at least one transition required");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in page.Transitions)
        {
            if (string.IsNullOrWhiteSpace(t.Id))   errors.Add($"{loc}: transition missing `id`");
            else if (!ids.Add(t.Id))               errors.Add($"{loc}: duplicate transition id `{t.Id}`");

            if (string.IsNullOrWhiteSpace(t.On))   errors.Add($"{loc}: transition `{t.Id}` missing `on`");
            else if (events.Count > 0 && !events.Contains(t.On))
                errors.Add($"{loc}: transition `{t.Id}` references unknown event `{t.On}`. Add it to /spec-sdl/events.yaml.");

            if (string.IsNullOrWhiteSpace(t.Next)) errors.Add($"{loc}: transition `{t.Id}` missing `next`");
        }
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
    public List<SdlTransition> Transitions { get; set; } = new();
    [YamlIgnore] public string SourcePath { get; set; } = "";
}

internal sealed class SdlSourceYaml
{
    public string Spec { get; set; } = "";
    public string Figure { get; set; } = "";
    public string? Url { get; set; }
}

internal sealed class SdlTransition
{
    public string Id { get; set; } = "";
    public string On { get; set; } = "";
    public string? Guard { get; set; }
    public List<string>? Actions { get; set; }
    public string Next { get; set; } = "";
    public string? Notes { get; set; }
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
            Transitions      = page.Transitions.Select(TransitionModel.From).ToList(),
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
    public List<string> Actions { get; set; } = new();
    public List<string> ActionLiterals { get; set; } = new();
    public string ActionsCsv { get; set; } = "";
    public string EdgeLabel { get; set; } = "";

    public static TransitionModel From(SdlTransition t)
    {
        var actionLiterals = (t.Actions ?? new()).Select(TemplateModel.CSharpStringLiteral).ToList();
        return new TransitionModel
        {
            Id             = t.Id,
            On             = t.On,
            Guard          = t.Guard,
            GuardLiteral   = t.Guard is null ? "null" : TemplateModel.CSharpStringLiteral(t.Guard),
            Next           = t.Next,
            Notes          = t.Notes,
            NotesLiteral   = t.Notes is null ? "null" : TemplateModel.CSharpStringLiteral(t.Notes),
            Actions        = t.Actions ?? new(),
            ActionLiterals = actionLiterals,
            ActionsCsv     = string.Join(", ", actionLiterals),
            EdgeLabel      = BuildMermaidEdgeLabel(t),
        };
    }

    private static string BuildMermaidEdgeLabel(SdlTransition t)
    {
        var parts = new List<string> { t.Id, "on: " + EscapeMermaid(t.On) };
        if (!string.IsNullOrEmpty(t.Guard))
        {
            parts.Add("[" + EscapeMermaid(t.Guard) + "]");
        }
        foreach (var a in t.Actions ?? new())
        {
            parts.Add("/ " + EscapeMermaid(a));
        }
        return string.Join("<br/>", parts);
    }

    private static string EscapeMermaid(string s) => s
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("\n", " ",      StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);
}
