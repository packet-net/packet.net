using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// C# view-model for a state-machine page, surfaced to Scriban templates
/// via snake_case member access (e.g. <c>page.class_name</c>). Built
/// from a <see cref="ResolvedPage"/>; all C#-specific literal escaping
/// and enum mapping happens here.
/// </summary>
public sealed class CsharpStateModel
{
    public string Machine { get; init; } = "";
    public string State { get; init; } = "";
    public string Coverage { get; init; } = "complete";
    public string ClassName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string SourceUrlLiteral { get; init; } = "null";
    public List<CsharpTransitionModel> Transitions { get; init; } = new();

    public static CsharpStateModel From(ResolvedPage page)
    {
        var className = CsharpEmitter.Pascal(page.Machine) + "_" + page.State;
        return new CsharpStateModel
        {
            Machine          = page.Machine,
            State            = page.State,
            Coverage         = page.Coverage,
            ClassName        = className,
            SourcePath       = page.SourcePath,
            SourceSpec       = page.SourceSpec,
            SourceFigure     = page.SourceFigure,
            SourceUrl        = page.SourceUrl,
            SourceUrlLiteral = page.SourceUrl is null ? "null" : CsharpEmitter.CSharpStringLiteral(page.SourceUrl),
            Transitions      = page.Transitions.Select(CsharpTransitionModel.From).ToList(),
        };
    }
}

public sealed class CsharpTransitionModel
{
    public string Id { get; init; } = "";
    public string On { get; init; } = "";
    public string? Guard { get; init; }
    public string GuardLiteral { get; init; } = "null";
    public string Next { get; init; } = "";
    public string? Notes { get; init; }
    public string NotesLiteral { get; init; } = "null";
    public List<CsharpActionModel> Actions { get; init; } = new();
    public string ActionsCsv { get; init; } = "";
    public string ReferencesCsv { get; init; } = "";
    public string LoopsCsv { get; init; } = "";
    public string EdgeLabel { get; init; } = "";

    public static CsharpTransitionModel From(ResolvedTransition t) => new()
    {
        Id            = t.Id,
        On            = t.On,
        Guard         = t.Guard,
        GuardLiteral  = t.Guard is null ? "null" : CsharpEmitter.CSharpStringLiteral(t.Guard),
        Next          = t.Next,
        Notes         = t.Notes,
        NotesLiteral  = t.Notes is null ? "null" : CsharpEmitter.CSharpStringLiteral(t.Notes),
        Actions       = t.Actions.Select(CsharpActionModel.From).ToList(),
        ActionsCsv    = CsharpEmitter.FormatActionsCsv(t.Actions),
        ReferencesCsv = CsharpEmitter.FormatReferenceCsv(t.References),
        LoopsCsv      = CsharpEmitter.FormatLoopsCsv(t.Loops),
        EdgeLabel     = CsharpEmitter.BuildMermaidEdgeLabel(t.Id, t.On, t.Guard, t.Actions),
    };
}

public sealed class CsharpActionModel
{
    public string Verb { get; init; } = "";
    public string VerbLiteral { get; init; } = "";
    public string KindEnum { get; init; } = "";

    public static CsharpActionModel From(ResolvedAction a) => new()
    {
        Verb        = a.Verb,
        VerbLiteral = CsharpEmitter.CSharpStringLiteral(a.Verb),
        KindEnum    = CsharpEmitter.KindEnumLiteral(a.Kind),
    };
}
