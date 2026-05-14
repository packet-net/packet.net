using System.Globalization;
using System.Text;
using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Ts;

/// <summary>
/// TypeScript emitter for the resolved SDL IR. Produces one
/// <c>.g.ts</c> file per page in the <c>ax25sdl</c> npm package.
/// Hand-rolled string emission (no template engine); the output is
/// designed to be strict-mode TypeScript with <c>noImplicitAny</c> +
/// <c>strictNullChecks</c> enabled.
/// </summary>
public static class TsEmitter
{
    public sealed record Emission(string FileName, string Content);

    public static Emission EmitStatePage(ResolvedPage page)
    {
        var varName = Pascal(page.Machine) + page.State;
        var stem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var fileName = stem + ".g.ts";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        // NodeNext module resolution requires the .js extension even on
        // TypeScript imports (the runtime-resolved path is .js, the type
        // resolver lifts that to .ts at typecheck time).
        sb.Append("import type { StatePage } from \"./types.js\";\n\n");
        sb.Append("/**\n");
        sb.Append(" * SDL transitions for the ").Append(page.State)
          .Append(" state of the ").Append(page.Machine).Append(" machine.\n");
        sb.Append(" * Source: ").Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append(" */\n");
        sb.Append("export const ").Append(varName).Append(": StatePage = {\n");
        sb.Append("  machine: ").Append(TsStringLiteral(page.Machine)).Append(",\n");
        sb.Append("  state: ").Append(TsStringLiteral(page.State)).Append(",\n");
        sb.Append("  source: { spec: ").Append(TsStringLiteral(page.SourceSpec))
          .Append(", figure: ").Append(TsStringLiteral(page.SourceFigure))
          .Append(", url: ").Append(TsStringLiteral(page.SourceUrl ?? "")).Append(" },\n");
        sb.Append("  transitions: [\n");
        foreach (var t in page.Transitions)
        {
            sb.Append("    {\n");
            sb.Append("      id: ").Append(TsStringLiteral(t.Id)).Append(",\n");
            sb.Append("      from: ").Append(TsStringLiteral(page.State)).Append(",\n");
            sb.Append("      on: ").Append(TsStringLiteral(t.On)).Append(",\n");
            sb.Append("      guard: ").Append(TsStringLiteral(t.Guard ?? "")).Append(",\n");
            sb.Append("      actions: ").Append(FormatActions(t.Actions, 3)).Append(",\n");
            sb.Append("      next: ").Append(TsStringLiteral(t.Next)).Append(",\n");
            sb.Append("      notes: ").Append(TsStringLiteral(t.Notes ?? "")).Append(",\n");
            sb.Append("      references: ").Append(FormatReferences(t.References, 3)).Append(",\n");
            sb.Append("      loops: ").Append(FormatLoops(t.Loops, 3)).Append(",\n");
            sb.Append("    },\n");
        }
        sb.Append("  ],\n");
        sb.Append("};\n");

        return new Emission(fileName, sb.ToString());
    }

    public static Emission EmitSubroutinePage(ResolvedSubroutinesPage page)
    {
        var fileStem = Path.GetFileNameWithoutExtension(page.SourcePath)
            .Replace(".sdl", string.Empty, StringComparison.Ordinal);
        var varName = Pascal(page.Machine) + Pascal(fileStem);
        var fileName = fileStem + ".g.ts";

        var sb = new StringBuilder();
        EmitHeader(sb, page.SourcePath);
        sb.Append("import type { SubroutinesPage } from \"./types.js\";\n\n");
        sb.Append("/**\n");
        sb.Append(" * SDL subroutines for the ").Append(page.Machine).Append(" machine.\n");
        sb.Append(" * Source: ").Append(page.SourceSpec).Append(", figure ").Append(page.SourceFigure).Append(".\n");
        sb.Append(" */\n");
        sb.Append("export const ").Append(varName).Append(": SubroutinesPage = {\n");
        sb.Append("  machine: ").Append(TsStringLiteral(page.Machine)).Append(",\n");
        sb.Append("  source: { spec: ").Append(TsStringLiteral(page.SourceSpec))
          .Append(", figure: ").Append(TsStringLiteral(page.SourceFigure))
          .Append(", url: ").Append(TsStringLiteral(page.SourceUrl ?? "")).Append(" },\n");
        sb.Append("  subroutines: [\n");
        foreach (var s in page.Subroutines)
        {
            sb.Append("    {\n");
            sb.Append("      name: ").Append(TsStringLiteral(s.Name)).Append(",\n");
            sb.Append("      paths: [\n");
            foreach (var p in s.Paths)
            {
                sb.Append("        {\n");
                sb.Append("          id: ").Append(TsStringLiteral(p.Id)).Append(",\n");
                sb.Append("          guard: ").Append(TsStringLiteral(p.Guard ?? "")).Append(",\n");
                sb.Append("          actions: ").Append(FormatActions(p.Actions, 5)).Append(",\n");
                sb.Append("          notes: ").Append(TsStringLiteral(p.Notes ?? "")).Append(",\n");
                sb.Append("          references: ").Append(FormatReferences(p.References, 5)).Append(",\n");
                sb.Append("          loops: ").Append(FormatLoops(p.Loops, 5)).Append(",\n");
                sb.Append("        },\n");
            }
            sb.Append("      ],\n");
            sb.Append("      notes: ").Append(TsStringLiteral(s.Notes ?? "")).Append(",\n");
            sb.Append("      references: ").Append(FormatReferences(s.References, 3)).Append(",\n");
            sb.Append("    },\n");
        }
        sb.Append("  ],\n");
        sb.Append("};\n");

        return new Emission(fileName, sb.ToString());
    }

    /// <summary>
    /// Build the package <c>index.ts</c> that re-exports every
    /// generated page plus the runtime types. Lets consumers do
    /// <c>import { DataLinkConnected } from "ax25sdl"</c> without
    /// caring which file it lives in.
    /// </summary>
    public static string EmitIndex(IEnumerable<ResolvedPage> pages, IEnumerable<ResolvedSubroutinesPage> subPages)
    {
        var sb = new StringBuilder();
        sb.Append("// Code generated by tools/Packet.Sdl.CodeGen. DO NOT EDIT.\n");
        sb.Append("// Re-exports every SDL page so consumers can `import { DataLinkConnected } from \"ax25sdl\"`.\n\n");
        sb.Append("export * from \"./types.js\";\n");
        foreach (var page in pages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(page.SourcePath)
                .Replace(".sdl", string.Empty, StringComparison.Ordinal);
            sb.Append("export * from \"./").Append(stem).Append(".g.js\";\n");
        }
        foreach (var page in subPages.OrderBy(p => p.SourcePath, StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(page.SourcePath)
                .Replace(".sdl", string.Empty, StringComparison.Ordinal);
            sb.Append("export * from \"./").Append(stem).Append(".g.js\";\n");
        }
        return sb.ToString();
    }

    // ─── Formatting helpers ───────────────────────────────────────────

    private static void EmitHeader(StringBuilder sb, string sourcePath)
    {
        sb.Append("// Code generated by tools/Packet.Sdl.CodeGen from ").Append(sourcePath.Replace('\\', '/')).Append(".\n");
        sb.Append("// DO NOT EDIT. Run `dotnet run --project tools/Packet.Sdl.CodeGen` to regenerate.\n\n");
    }

    /// <summary>
    /// <paramref name="parentIndent"/> is the indentation depth (in
    /// 2-space units) of the surrounding <c>field: [</c> line. Entries
    /// land at parentIndent+1; the closing bracket matches parentIndent.
    /// </summary>
    private static string FormatActions(IReadOnlyList<ResolvedAction> actions, int parentIndent)
    {
        if (actions.Count == 0) return "[]";
        var indent = new string(' ', (parentIndent + 1) * 2);
        var closer = new string(' ', parentIndent * 2);
        var sb = new StringBuilder();
        sb.Append("[\n");
        foreach (var a in actions)
        {
            sb.Append(indent).Append("{ verb: ").Append(TsStringLiteral(a.Verb))
              .Append(", kind: ").Append(TsKindLiteral(a.Kind)).Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    private static string FormatReferences(IReadOnlyList<ResolvedReference> refs, int parentIndent)
    {
        if (refs.Count == 0) return "[]";
        var indent = new string(' ', (parentIndent + 1) * 2);
        var closer = new string(' ', parentIndent * 2);
        var sb = new StringBuilder();
        sb.Append("[\n");
        foreach (var r in refs)
        {
            sb.Append(indent).Append("{ source: ").Append(TsStringLiteral(r.Source))
              .Append(", cite: ").Append(TsStringLiteral(r.Cite ?? ""))
              .Append(", quote: ").Append(TsStringLiteral(r.Quote ?? ""))
              .Append(", path: ").Append(TsStringLiteral(r.Path ?? ""))
              .Append(", function: ").Append(TsStringLiteral(r.Function ?? ""))
              .Append(", line: ").Append((r.Line ?? 0).ToString(CultureInfo.InvariantCulture))
              .Append(", note: ").Append(TsStringLiteral(r.Note ?? ""))
              .Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    private static string FormatLoops(IReadOnlyList<ResolvedLoop> loops, int parentIndent)
    {
        if (loops.Count == 0) return "[]";
        var indent = new string(' ', (parentIndent + 1) * 2);
        var closer = new string(' ', parentIndent * 2);
        var sb = new StringBuilder();
        sb.Append("[\n");
        foreach (var l in loops)
        {
            sb.Append(indent).Append("{ start: ").Append(l.Start.ToString(CultureInfo.InvariantCulture))
              .Append(", length: ").Append(l.Length.ToString(CultureInfo.InvariantCulture))
              .Append(", predicate: ").Append(TsStringLiteral(l.Predicate))
              .Append(" },\n");
        }
        sb.Append(closer).Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// TypeScript string literal — double-quoted with backslash escapes.
    /// Avoids template literals because no field on the generated side
    /// needs interpolation, and double-quoted strings round-trip more
    /// predictably through every JS toolchain.
    /// </summary>
    internal static string TsStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                default:
                    if (c < 0x20)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Emit the kind as a string-literal type member. The TS
    /// <c>ActionKind</c> is a discriminated union of string literals
    /// (matching <c>spec-sdl/actions.yaml</c> kind names), so this
    /// produces a value that satisfies <c>ActionKind</c> directly.
    /// </summary>
    internal static string TsKindLiteral(ResolvedActionKind kind) => kind switch
    {
        ResolvedActionKind.SignalUpper => "\"signal_upper\"",
        ResolvedActionKind.SignalLower => "\"signal_lower\"",
        ResolvedActionKind.Processing  => "\"processing\"",
        ResolvedActionKind.Subroutine  => "\"subroutine\"",
        ResolvedActionKind.InternalOut => "\"internal_out\"",
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };

    /// <summary>snake_case → PascalCase, preserving non-alphanumeric runs.</summary>
    internal static string Pascal(string snake)
    {
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
