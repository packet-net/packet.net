using System.Diagnostics;
using System.IO;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Invokes the codegen tool as a subprocess against a temporary directory.
/// Each test scenario writes its `*.sdl.yaml` and `events.yaml` fixtures into
/// the temp dir, asks the runner to invoke codegen, then asserts on the
/// exit code, stderr (for ::error:: lines), and the generated `.g.cs`
/// content.
/// </summary>
/// <remarks>
/// <para>
/// Black-box on purpose: tests exercise the actual CLI contract of the
/// tool. Refactoring <c>Program.cs</c> internals doesn't break tests; only
/// CLI behaviour does.
/// </para>
/// <para>
/// The runner uses the codegen project's build output directly via
/// <c>dotnet</c> to invoke <c>Packet.Sdl.CodeGen.dll</c>. It does NOT run
/// <c>dotnet run --project ...</c> per invocation because that path
/// re-builds every time and the test suite would crawl.
/// </para>
/// </remarks>
internal sealed class CodegenRunner : IDisposable
{
    public string RootDir { get; }
    public string SpecDir { get; }
    public string OutDir  { get; }
    public string TestsDir { get; }

    public CodegenRunner()
    {
        RootDir  = Directory.CreateTempSubdirectory("sdl-codegen-tests-").FullName;
        SpecDir  = Path.Combine(RootDir, "spec-sdl");
        OutDir   = Path.Combine(RootDir, "src", "Packet.Ax25.Sdl");
        TestsDir = Path.Combine(RootDir, "tests", "Packet.Ax25.Conformance.Tests");
        Directory.CreateDirectory(SpecDir);
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(TestsDir);
    }

    /// <summary>Drop a YAML page into the SpecDir at the given sub-path.</summary>
    public void WritePage(string relativePath, string yaml)
    {
        var full = Path.Combine(SpecDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, yaml);
    }

    /// <summary>Drop an events.yaml at the SpecDir root.</summary>
    public void WriteEventsCatalog(string yaml)
        => File.WriteAllText(Path.Combine(SpecDir, "events.yaml"), yaml);

    /// <summary>Drop an actions.yaml at the SpecDir root (optional; soft passthrough when absent).</summary>
    public void WriteActionsCatalog(string yaml)
        => File.WriteAllText(Path.Combine(SpecDir, "actions.yaml"), yaml);

    public sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>Run the codegen tool and capture exit + stdout + stderr.</summary>
    public RunResult Run()
    {
        // Locate the codegen DLL next to the test binary. The csproj sets
        // ReferenceOutputAssembly=false so the assembly isn't auto-copied;
        // we resolve it via the well-known relative path from this test
        // assembly's build output. Works in dotnet test and direct runs.
        var testAsmDir = Path.GetDirectoryName(typeof(CodegenRunner).Assembly.Location)!;
        // testAsmDir = .../tests/Packet.Sdl.CodeGen.Tests/bin/<Config>/<tfm>
        // codegen dll: .../tools/Packet.Sdl.CodeGen/bin/<Config>/<tfm>/Packet.Sdl.CodeGen.dll
        var config = new DirectoryInfo(testAsmDir).Parent!.Name; // Debug or Release
        var tfm = new DirectoryInfo(testAsmDir).Name;
        var repoRoot = FindRepoRoot(testAsmDir);
        var dll = Path.Combine(repoRoot, "tools", "Packet.Sdl.CodeGen", "bin", config, tfm, "Packet.Sdl.CodeGen.dll");

        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"codegen DLL not found at expected path: {dll}. Ensure the codegen project is built before running tests.");
        }

        // --csharp opts in to the C# backend (only) so the test
        // sandbox doesn't touch Go / TS spec directories. Per-test
        // input + output paths are passed through --in, --csharp-out,
        // --csharp-tests; the CLI's opt-in-by-presence semantics treat
        // setting either path as an explicit selection of the C#
        // backend, with all others off.
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --in \"{SpecDir}\" --csharp --csharp-out \"{OutDir}\" --csharp-tests \"{TestsDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new RunResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>Read a generated file from the output directory.</summary>
    public string ReadGenerated(string relativePath)
    {
        var full = Path.Combine(OutDir, relativePath);
        return File.ReadAllText(full);
    }

    /// <summary>Does the named file exist in the output directory?</summary>
    public bool GeneratedExists(string relativePath)
        => File.Exists(Path.Combine(OutDir, relativePath));

    public void Dispose()
    {
        try { Directory.Delete(RootDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Packet.NET.slnx")))
        {
            d = d.Parent;
        }
        return d?.FullName ?? throw new InvalidOperationException("repo root (Packet.NET.sln) not found above " + start);
    }
}
