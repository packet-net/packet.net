using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// Roslyn parse-back validation for emitted C#. Throws with line/col +
/// surrounding context on parse errors, which is much friendlier than
/// the downstream <c>dotnet build</c> failure the codegen would
/// otherwise produce.
/// </summary>
public static class CsharpValidator
{
    public static void Validate(string virtualPath, string source)
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
        // Show the offending output around the first error so the user
        // can spot the template glitch without grovelling through the file.
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
}
