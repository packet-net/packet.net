using System.Globalization;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// C# emitter for the resolved SDL IR. Renders state-machine pages to
/// <c>.g.cs</c> / <c>.g.Tests.cs</c> / <c>.g.mmd</c> via Scriban
/// templates, and subroutine pages to <c>.g.cs</c>. Every emission is
/// validated with <see cref="CsharpValidator"/> before being returned.
/// </summary>
public static class CsharpEmitter
{
    private static readonly Scriban.Template CodeTemplate        = TemplateLoader.Load("code.scriban-cs");
    private static readonly Scriban.Template TestsTemplate       = TemplateLoader.Load("tests.scriban-cs");
    private static readonly Scriban.Template MermaidTemplate     = TemplateLoader.Load("mermaid.scriban-mmd");
    private static readonly Scriban.Template SubroutinesTemplate = TemplateLoader.Load("subroutines.scriban-cs");

    public sealed record StatePageEmission(string ClassName, string Code, string Tests, string Mermaid);
    public sealed record SubroutinePageEmission(string ClassName, string Code);

    /// <summary>
    /// Emit C# for one state-machine page. <paramref name="codePath"/> and
    /// <paramref name="testsPath"/> are passed through to the Roslyn
    /// parse-back diagnostics so error messages point at the eventual
    /// output paths rather than virtual names.
    /// </summary>
    public static StatePageEmission EmitStatePage(ResolvedPage page, string codePath, string testsPath)
    {
        var model = CsharpStateModel.From(page);
        var code    = TemplateLoader.Render(CodeTemplate, model);
        var tests   = TemplateLoader.Render(TestsTemplate, model);
        var mermaid = TemplateLoader.Render(MermaidTemplate, model);
        CsharpValidator.Validate(codePath,  code);
        CsharpValidator.Validate(testsPath, tests);
        return new StatePageEmission(model.ClassName, code, tests, mermaid);
    }

    /// <summary>Emit C# for one subroutine page.</summary>
    public static SubroutinePageEmission EmitSubroutinePage(ResolvedSubroutinesPage page, string codePath)
    {
        var model = CsharpSubroutinesModel.From(page);
        var code = TemplateLoader.Render(SubroutinesTemplate, model);
        CsharpValidator.Validate(codePath, code);
        return new SubroutinePageEmission(model.ClassName, code);
    }

    // ─── Literal helpers ──────────────────────────────────────────────

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

    internal static string NullOrLiteral(string? s) => s is null ? "null" : CSharpStringLiteral(s);

    internal static string KindEnumLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "ActionKind.SignalUpper",
        ResolvedActionKind.SignalLower => "ActionKind.SignalLower",
        ResolvedActionKind.Processing  => "ActionKind.Processing",
        ResolvedActionKind.Subroutine  => "ActionKind.Subroutine",
        ResolvedActionKind.InternalOut => "ActionKind.InternalOut",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    internal static string KindIndicator(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "↑",
        ResolvedActionKind.SignalLower => "↓",
        ResolvedActionKind.Processing  => "·",
        ResolvedActionKind.Subroutine  => "→()",
        ResolvedActionKind.InternalOut => "⇢",
        _ => "?",
    };

    internal static string Pascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    internal static string FormatReferenceCsv(IEnumerable<ResolvedReference> refs)
        => string.Join(", ", refs.Select(r =>
            "new ImplementationReference(" +
            $"Source: {CSharpStringLiteral(r.Source)}, " +
            $"Cite: {NullOrLiteral(r.Cite)}, " +
            $"Quote: {NullOrLiteral(r.Quote)}, " +
            $"Path: {NullOrLiteral(r.Path)}, " +
            $"Function: {NullOrLiteral(r.Function)}, " +
            $"Line: {(r.Line is null ? "null" : r.Line.Value.ToString(CultureInfo.InvariantCulture))}, " +
            $"Note: {NullOrLiteral(r.Note)})"));

    internal static string FormatActionsCsv(IEnumerable<ResolvedAction> actions)
        => string.Join(", ", actions.Select(a =>
            $"new ActionStep({CSharpStringLiteral(a.Verb)}, {KindEnumLiteral(a.Kind)})"));

    internal static string FormatLoopsCsv(IEnumerable<ResolvedLoop> loops)
        => string.Join(", ", loops.Select(l =>
            $"new LoopRange({l.Start.ToString(CultureInfo.InvariantCulture)}, " +
            $"{l.Length.ToString(CultureInfo.InvariantCulture)}, " +
            $"{CSharpStringLiteral(l.Predicate)})"));

    internal static string EscapeMermaid(string s) => s
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("\n", " ",      StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);

    internal static string BuildMermaidEdgeLabel(string id, string on, string? guard, IReadOnlyList<ResolvedAction> actions)
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
}
