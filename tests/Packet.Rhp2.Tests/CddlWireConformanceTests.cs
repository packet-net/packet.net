using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// CDDL wire-grammar conformance: every JSON payload the codec emits must
/// validate against <c>spec/rhp2.cddl</c> (RFC 8610). This is the
/// language-neutral drift guard; if a code change alters the wire shape
/// (renames a field, changes casing, drops a required field, adds an
/// unexpected key), this test fails independently of the C# type system.
///
/// These tests shell out to the <c>cddl</c> CLI (<c>cargo install cddl</c>), so
/// what they report depends on whether that binary is present:
/// <list type="bullet">
///   <item><description>cddl on PATH (or at <c>~/.cargo/bin/cddl</c>): every test
///     really validates its payload against the grammar.</description></item>
///   <item><description>cddl absent: each test reports <b>Skipped</b>, never a
///     silent pass. Install it to get real coverage locally.</description></item>
///   <item><description>cddl absent and <c>PDN_REQUIRE_CDDL=1</c>: each test
///     <b>fails</b>. CI sets that variable, so an unprovisioned runner is a red
///     build rather than a suite of hollow greens.</description></item>
/// </list>
/// </summary>
public class CddlWireConformanceTests
{
    private static readonly string? CddlPath = FindCddl();

    /// <summary>CI sets <c>PDN_REQUIRE_CDDL=1</c>: a missing cddl CLI must then fail, not skip.</summary>
    private static readonly bool RequireCddlTool =
        Environment.GetEnvironmentVariable("PDN_REQUIRE_CDDL") == "1";

    private static readonly string GrammarPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "spec", "rhp2.cddl"));

    private static string? FindCddl()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoBin = Path.Combine(home, ".cargo", "bin", "cddl");
        if (File.Exists(cargoBin))
        {
            return cargoBin;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "cddl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Gate for every test in this class: hard-fail when <c>PDN_REQUIRE_CDDL=1</c> demands the
    /// tool and it is missing, otherwise skip. Throws a skip exception, so it only reports
    /// correctly from a <c>[SkippableFact]</c>.
    /// </summary>
    private static void RequireCddl()
    {
        if (RequireCddlTool)
        {
            CddlPath.Should().NotBeNull(
                "PDN_REQUIRE_CDDL=1 but the cddl CLI was not found; cargo install cddl");
        }

        Skip.If(CddlPath is null, "cddl CLI not installed (cargo install cddl)");
    }

    /// <summary>
    /// Runs the payload through <c>cddl validate</c> and returns its exit code plus stderr.
    /// </summary>
    /// <remarks>
    /// The global <c>--ci</c> flag is load-bearing, not cosmetic: without it the cddl CLI
    /// (checked against 0.10.7) prints its validation errors to stderr and still exits 0, so an
    /// exit-code assertion alone would pass for literally any input, including <c>"a string"</c>
    /// or <c>{"nope":1}</c>. <c>--ci</c> is what turns a failed validation into a non-zero exit.
    /// </remarks>
    private static (int ExitCode, string Stderr) RunCddl(string json)
    {
        File.Exists(GrammarPath).Should().BeTrue($"grammar file must exist at {GrammarPath}");

        var psi = new ProcessStartInfo
        {
            FileName = CddlPath,
            ArgumentList = { "--ci", "validate", "--cddl", GrammarPath, "--stdin" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.Write(json);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue("the cddl CLI must exit within 30s");

        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// A failure against the top-level type choice reports one error per branch (25+ branches,
    /// several lines each), which buries the useful line. Keep the head of it.
    /// </summary>
    private static string Head(string stderr, int lines = 12)
    {
        var split = stderr.Split('\n');
        return split.Length <= lines
            ? stderr
            : string.Join('\n', split.Take(lines)) + $"\n... ({split.Length - lines} more lines)";
    }

    private static void ValidateAgainstGrammar(string json)
    {
        RequireCddl();

        var (exitCode, stderr) = RunCddl(json);

        exitCode.Should().Be(0,
            $"wire JSON must validate against spec/rhp2.cddl.\nJSON: {json}\nCDDL errors:\n{Head(stderr)}");
    }

    private static string ToJson(RhpMessage message) => Encoding.UTF8.GetString(RhpJson.Serialize(message));

    // ─── Requests (client → server) ──────────────────────────────────────────

    [SkippableFact]
    public void Auth_validates() =>
        ValidateAgainstGrammar(ToJson(new AuthMessage { Id = 1, User = "test", Pass = "secret" }));

    [SkippableFact]
    public void Open_active_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenMessage
        {
            Id = 2, Pfam = "ax25", Mode = "stream", Local = "M0LTE", Remote = "G9DUM", Port = "1", Flags = 0x80,
        }));

    [SkippableFact]
    public void Open_passive_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenMessage { Id = 3, Pfam = "ax25", Mode = "stream" }));

    [SkippableFact]
    public void Socket_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketMessage { Id = 4, Pfam = "ax25", Mode = "dgram" }));

    [SkippableFact]
    public void Bind_validates() =>
        ValidateAgainstGrammar(ToJson(new BindMessage { Id = 5, Handle = 1, Local = "M0LTE-7", Port = "1" }));

    [SkippableFact]
    public void Bind_null_port_validates() =>
        ValidateAgainstGrammar(ToJson(new BindMessage { Id = 6, Handle = 1, Local = "M0LTE-7" }));

    [SkippableFact]
    public void Listen_validates() =>
        ValidateAgainstGrammar(ToJson(new ListenMessage { Id = 7, Handle = 1 }));

    [SkippableFact]
    public void Connect_validates() =>
        ValidateAgainstGrammar(ToJson(new ConnectMessage { Id = 8, Handle = 1, Remote = "G9DUM" }));

    [SkippableFact]
    public void Send_validates() =>
        ValidateAgainstGrammar(ToJson(new SendMessage { Id = 9, Handle = 1, Data = "hello" }));

    [SkippableFact]
    public void Send_empty_data_validates() =>
        ValidateAgainstGrammar(ToJson(new SendMessage { Id = 10, Handle = 1, Data = "" }));

    [SkippableFact]
    public void SendTo_validates() =>
        ValidateAgainstGrammar(ToJson(new SendToMessage
        {
            Id = 11, Handle = 2, Data = "beacon", Remote = "G9DUM", Local = "M0LTE-7",
        }));

    [SkippableFact]
    public void Status_request_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusMessage { Id = 12, Handle = 1 }));

    [SkippableFact]
    public void Close_request_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseMessage { Id = 13, Handle = 1 }));

    // ─── Replies (server → client) ───────────────────────────────────────────

    [SkippableFact]
    public void AuthReply_validates() =>
        ValidateAgainstGrammar(ToJson(new AuthReplyMessage { Id = 1, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void OpenReply_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenReplyMessage { Id = 2, Handle = 100, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void OpenReply_error_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenReplyMessage { Id = 2, ErrCode = 15, ErrText = "No Route" }));

    [SkippableFact]
    public void SocketReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketReplyMessage { Id = 4, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void BindReply_validates() =>
        ValidateAgainstGrammar(ToJson(new BindReplyMessage { Id = 5, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void ListenReply_validates() =>
        ValidateAgainstGrammar(ToJson(new ListenReplyMessage { Id = 7, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void ConnectReply_validates() =>
        ValidateAgainstGrammar(ToJson(new ConnectReplyMessage { Id = 8, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void SendReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SendReplyMessage { Id = 9, Handle = 100, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void SendReply_with_status_validates() =>
        ValidateAgainstGrammar(ToJson(new SendReplyMessage { Id = 9, Handle = 100, ErrCode = 0, ErrText = "Ok", Status = 2 }));

    [SkippableFact]
    public void SendToReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SendToReplyMessage { Id = 11, Handle = 2, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void StatusReply_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusReplyMessage { Id = 12, Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    [SkippableFact]
    public void CloseReply_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseReplyMessage { Id = 13, Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    // ─── Async pushes (server → client, carry seqno) ─────────────────────────

    [SkippableFact]
    public void Recv_stream_validates() =>
        ValidateAgainstGrammar(ToJson(new RecvMessage { Seqno = 0, Handle = 100, Data = "hello world" }));

    [SkippableFact]
    public void Recv_dgram_validates() =>
        ValidateAgainstGrammar(ToJson(new RecvMessage
        {
            Seqno = 1, Handle = 200, Data = "UI frame", Port = "1", Local = "M0LTE-7", Remote = "G9DUM",
        }));

    [SkippableFact]
    public void Accept_validates() =>
        ValidateAgainstGrammar(ToJson(new AcceptMessage
        {
            Seqno = 0, Handle = 103, Child = 104, Remote = "G9DUM", Local = "M0LTE-7", Port = "1",
        }));

    [SkippableFact]
    public void Status_push_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusMessage { Seqno = 1, Handle = 104, Flags = 2 }));

    [SkippableFact]
    public void Close_push_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseMessage { Seqno = 2, Handle = 104 }));

    // ─── Id-less request (row 10: server still replies) ──────────────────────

    [SkippableFact]
    public void Socket_without_id_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketMessage { Pfam = "ax25", Mode = "stream" }));

    [SkippableFact]
    public void SocketReply_to_idless_request_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketReplyMessage { Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    // ─── Vectors corpus (spec/vectors/rhp2-messages.json) ────────────────────

    [SkippableFact]
    public void All_vectors_in_corpus_validate_against_grammar()
    {
        RequireCddl();

        var vectorsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "spec", "vectors", "rhp2-messages.json"));
        File.Exists(vectorsPath).Should().BeTrue($"vectors corpus must exist at {vectorsPath}");

        var corpus = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(
            File.ReadAllText(vectorsPath));
        corpus.Should().NotBeNull();

        foreach (var (element, index) in corpus!.Select((e, i) => (e, i)))
        {
            ValidateAgainstGrammar(element.GetRawText());
        }
    }

    // ─── Negative control (the harness must be able to fail) ─────────────────

    /// <summary>
    /// Guards the guard: a payload the grammar cannot accept (an auth message missing
    /// <c>user</c>/<c>pass</c> and carrying an unexpected key) must be rejected. Without this,
    /// a harness regression, a dropped <c>--ci</c> flag, or a grammar that degenerates into
    /// "anything goes" would leave every test above passing while checking nothing.
    /// </summary>
    [SkippableFact]
    public void Grammar_rejects_a_payload_that_does_not_match()
    {
        RequireCddl();

        var (exitCode, stderr) = RunCddl("""{"type":"auth","id":1,"bogus":true}""");

        exitCode.Should().NotBe(0,
            $"the conformance harness must actually reject malformed wire JSON.\nCDDL output:\n{stderr}");
    }
}
