using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// C# view-model for a subroutine page, surfaced to Scriban templates
/// via snake_case member access. Built from a
/// <see cref="ResolvedSubroutinesPage"/>; class name is derived from the
/// machine + file stem so a future second subroutine page (e.g.
/// Link_Mux) doesn't collide with the data-link one.
/// </summary>
public sealed class CsharpSubroutinesModel
{
    public string Machine { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string SourceUrlLiteral { get; init; } = "null";
    public List<CsharpSubroutineModel> Subroutines { get; init; } = new();

    public static CsharpSubroutinesModel From(ResolvedSubroutinesPage page)
    {
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var className = CsharpEmitter.Pascal(page.Machine) + "_" + CsharpEmitter.Pascal(fileStem);
        return new CsharpSubroutinesModel
        {
            Machine          = page.Machine,
            ClassName        = className,
            SourcePath       = page.SourcePath,
            SourceSpec       = page.SourceSpec,
            SourceFigure     = page.SourceFigure,
            SourceUrl        = page.SourceUrl,
            SourceUrlLiteral = page.SourceUrl is null ? "null" : CsharpEmitter.CSharpStringLiteral(page.SourceUrl),
            Subroutines      = page.Subroutines.Select(CsharpSubroutineModel.From).ToList(),
        };
    }
}

public sealed class CsharpSubroutineModel
{
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public string NotesLiteral { get; init; } = "null";
    public string ReferencesCsv { get; init; } = "";
    public List<CsharpSubroutinePathModel> Paths { get; init; } = new();

    public static CsharpSubroutineModel From(ResolvedSubroutine s) => new()
    {
        Name          = s.Name,
        Notes         = s.Notes,
        NotesLiteral  = s.Notes is null ? "null" : CsharpEmitter.CSharpStringLiteral(s.Notes),
        ReferencesCsv = CsharpEmitter.FormatReferenceCsv(s.References),
        Paths         = s.Paths.Select(CsharpSubroutinePathModel.From).ToList(),
    };
}

public sealed class CsharpSubroutinePathModel
{
    public string Id { get; init; } = "";
    public string? Guard { get; init; }
    public string GuardLiteral { get; init; } = "null";
    public string? Notes { get; init; }
    public string NotesLiteral { get; init; } = "null";
    public string ActionsCsv { get; init; } = "";
    public string ReferencesCsv { get; init; } = "";
    public string LoopsCsv { get; init; } = "";

    public static CsharpSubroutinePathModel From(ResolvedSubroutinePath p) => new()
    {
        Id            = p.Id,
        Guard         = p.Guard,
        GuardLiteral  = p.Guard is null ? "null" : CsharpEmitter.CSharpStringLiteral(p.Guard),
        Notes         = p.Notes,
        NotesLiteral  = p.Notes is null ? "null" : CsharpEmitter.CSharpStringLiteral(p.Notes),
        ActionsCsv    = CsharpEmitter.FormatActionsCsv(p.Actions),
        ReferencesCsv = CsharpEmitter.FormatReferenceCsv(p.References),
        LoopsCsv      = CsharpEmitter.FormatLoopsCsv(p.Loops),
    };
}
