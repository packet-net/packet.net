using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// CDDL wire-grammar conformance: every JSON payload the codec emits must
/// validate against <c>spec/rhp2.cddl</c> (RFC 8610). This is the
/// language-neutral drift guard — if a code change alters the wire shape
/// (renames a field, changes casing, drops a required field, adds an
/// unexpected key), this test fails independently of the C# type system.
///
/// Requires the <c>cddl</c> CLI (cargo install cddl). Skips gracefully when
/// the binary is absent (local dev without cargo); CI has it installed.
/// </summary>
public class CddlWireConformanceTests
{
    private static readonly string? CddlPath = FindCddl();

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

    private static void ValidateAgainstGrammar(string json)
    {
        if (CddlPath is null)
        {
            return; // skip: cddl not installed
        }

        File.Exists(GrammarPath).Should().BeTrue($"grammar file must exist at {GrammarPath}");

        var psi = new ProcessStartInfo
        {
            FileName = CddlPath,
            ArgumentList = { "validate", "--cddl", GrammarPath, "--stdin" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.StandardInput.Write(json);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        process.ExitCode.Should().Be(0,
            $"wire JSON must validate against spec/rhp2.cddl.\nJSON: {json}\nCDDL errors:\n{stderr}");
    }

    private static string ToJson(RhpMessage message) => Encoding.UTF8.GetString(RhpJson.Serialize(message));

    // ─── Requests (client → server) ──────────────────────────────────────────

    [Fact]
    public void Auth_validates() =>
        ValidateAgainstGrammar(ToJson(new AuthMessage { Id = 1, User = "test", Pass = "secret" }));

    [Fact]
    public void Open_active_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenMessage
        {
            Id = 2, Pfam = "ax25", Mode = "stream", Local = "M0LTE", Remote = "G9DUM", Port = "1", Flags = 0x80,
        }));

    [Fact]
    public void Open_passive_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenMessage { Id = 3, Pfam = "ax25", Mode = "stream" }));

    [Fact]
    public void Socket_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketMessage { Id = 4, Pfam = "ax25", Mode = "dgram" }));

    [Fact]
    public void Bind_validates() =>
        ValidateAgainstGrammar(ToJson(new BindMessage { Id = 5, Handle = 1, Local = "M0LTE-7", Port = "1" }));

    [Fact]
    public void Bind_null_port_validates() =>
        ValidateAgainstGrammar(ToJson(new BindMessage { Id = 6, Handle = 1, Local = "M0LTE-7" }));

    [Fact]
    public void Listen_validates() =>
        ValidateAgainstGrammar(ToJson(new ListenMessage { Id = 7, Handle = 1 }));

    [Fact]
    public void Connect_validates() =>
        ValidateAgainstGrammar(ToJson(new ConnectMessage { Id = 8, Handle = 1, Remote = "G9DUM" }));

    [Fact]
    public void Send_validates() =>
        ValidateAgainstGrammar(ToJson(new SendMessage { Id = 9, Handle = 1, Data = "hello" }));

    [Fact]
    public void Send_empty_data_validates() =>
        ValidateAgainstGrammar(ToJson(new SendMessage { Id = 10, Handle = 1, Data = "" }));

    [Fact]
    public void SendTo_validates() =>
        ValidateAgainstGrammar(ToJson(new SendToMessage
        {
            Id = 11, Handle = 2, Data = "beacon", Remote = "G9DUM", Local = "M0LTE-7",
        }));

    [Fact]
    public void Status_request_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusMessage { Id = 12, Handle = 1 }));

    [Fact]
    public void Close_request_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseMessage { Id = 13, Handle = 1 }));

    // ─── Replies (server → client) ───────────────────────────────────────────

    [Fact]
    public void AuthReply_validates() =>
        ValidateAgainstGrammar(ToJson(new AuthReplyMessage { Id = 1, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void OpenReply_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenReplyMessage { Id = 2, Handle = 100, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void OpenReply_error_validates() =>
        ValidateAgainstGrammar(ToJson(new OpenReplyMessage { Id = 2, ErrCode = 15, ErrText = "No Route" }));

    [Fact]
    public void SocketReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketReplyMessage { Id = 4, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void BindReply_validates() =>
        ValidateAgainstGrammar(ToJson(new BindReplyMessage { Id = 5, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void ListenReply_validates() =>
        ValidateAgainstGrammar(ToJson(new ListenReplyMessage { Id = 7, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void ConnectReply_validates() =>
        ValidateAgainstGrammar(ToJson(new ConnectReplyMessage { Id = 8, Handle = 101, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void SendReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SendReplyMessage { Id = 9, Handle = 100, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void SendReply_with_status_validates() =>
        ValidateAgainstGrammar(ToJson(new SendReplyMessage { Id = 9, Handle = 100, ErrCode = 0, ErrText = "Ok", Status = 2 }));

    [Fact]
    public void SendToReply_validates() =>
        ValidateAgainstGrammar(ToJson(new SendToReplyMessage { Id = 11, Handle = 2, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void StatusReply_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusReplyMessage { Id = 12, Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    [Fact]
    public void CloseReply_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseReplyMessage { Id = 13, Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    // ─── Async pushes (server → client, carry seqno) ─────────────────────────

    [Fact]
    public void Recv_stream_validates() =>
        ValidateAgainstGrammar(ToJson(new RecvMessage { Seqno = 0, Handle = 100, Data = "hello world" }));

    [Fact]
    public void Recv_dgram_validates() =>
        ValidateAgainstGrammar(ToJson(new RecvMessage
        {
            Seqno = 1, Handle = 200, Data = "UI frame", Port = "1", Local = "M0LTE-7", Remote = "G9DUM",
        }));

    [Fact]
    public void Accept_validates() =>
        ValidateAgainstGrammar(ToJson(new AcceptMessage
        {
            Seqno = 0, Handle = 103, Child = 104, Remote = "G9DUM", Local = "M0LTE-7", Port = "1",
        }));

    [Fact]
    public void Status_push_validates() =>
        ValidateAgainstGrammar(ToJson(new StatusMessage { Seqno = 1, Handle = 104, Flags = 2 }));

    [Fact]
    public void Close_push_validates() =>
        ValidateAgainstGrammar(ToJson(new CloseMessage { Seqno = 2, Handle = 104 }));

    // ─── Id-less request (row 10: server still replies) ──────────────────────

    [Fact]
    public void Socket_without_id_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketMessage { Pfam = "ax25", Mode = "stream" }));

    [Fact]
    public void SocketReply_to_idless_request_validates() =>
        ValidateAgainstGrammar(ToJson(new SocketReplyMessage { Handle = 1, ErrCode = 0, ErrText = "Ok" }));

    // ─── Vectors corpus (spec/vectors/rhp2-messages.json) ────────────────────

    [Fact]
    public void All_vectors_in_corpus_validate_against_grammar()
    {
        if (CddlPath is null)
        {
            return;
        }

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
}
