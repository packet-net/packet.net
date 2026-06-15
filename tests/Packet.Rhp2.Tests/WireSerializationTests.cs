using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// Conformance core for the RHPv2 JSON codec. The string fixtures here are
/// golden: they replicate wire shapes pinned against real XRouter (and the
/// PWP-0222 spec's divergent examples), so a change that breaks one of
/// these breaks interop, not just a unit test.
/// </summary>
public class WireSerializationTests
{
    private static RhpMessage Parse(string wire) => RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));

    private static string ToJson(RhpMessage message) => Encoding.UTF8.GetString(RhpJson.Serialize(message));

    // -----------------------------------------------------------------
    //  errCode / errText casing — the spec-vs-XRouter divergence
    // -----------------------------------------------------------------

    [Fact]
    public void OpenReply_parses_from_spec_lowercase_form()
    {
        // PWP-0222's examples write lowercase errcode/errtext.
        var msg = (OpenReplyMessage)Parse("""{"type":"openReply","id":7,"handle":1234,"errcode":0,"errtext":"Ok"}""");

        msg.Id.Should().Be(7);
        msg.Handle.Should().Be(1234);
        msg.ErrCode.Should().Be(0);
        msg.ErrText.Should().Be("Ok");
    }

    [Fact]
    public void OpenReply_parses_from_real_xrouter_capitalised_form()
    {
        // Real XRouter emits errCode/errText with capital C/T on every
        // reply — not just authReply as the spec implies.
        var msg = (OpenReplyMessage)Parse("""{"type":"openReply","id":7,"handle":1234,"errCode":0,"errText":"Ok"}""");

        msg.ErrCode.Should().Be(0);
        msg.ErrText.Should().Be("Ok");
    }

    [Fact]
    public void AuthReply_parses_capitalised_errCode()
    {
        var msg = (AuthReplyMessage)Parse("""{"type":"authReply","id":1,"errCode":14,"errText":"Unauthorised"}""");

        msg.ErrCode.Should().Be(14);
        msg.ErrText.Should().Be("Unauthorised");
    }

    public static TheoryData<string, RhpMessage> AllReplyTypes() => new()
    {
        { "authReply", new AuthReplyMessage { ErrCode = 14, ErrText = "x" } },
        { "openReply", new OpenReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "socketReply", new SocketReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "bindReply", new BindReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "listenReply", new ListenReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "connectReply", new ConnectReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "sendReply", new SendReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "sendtoReply", new SendToReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "statusReply", new StatusReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
        { "closeReply", new CloseReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" } },
    };

    [Theory]
    [MemberData(nameof(AllReplyTypes))]
    public void Every_reply_type_serializes_errCode_errText_with_capital_C_T(string expectedType, RhpMessage reply)
    {
        // Byte-compatibility pin: our emitted replies must match what real
        // XRouter puts on the wire, so a server built on this codec is
        // indistinguishable from XRouter to existing clients.
        var json = ToJson(reply);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("type").GetString().Should().Be(expectedType);
        doc.RootElement.TryGetProperty("errCode", out _).Should().BeTrue($"{expectedType} must carry errCode: {json}");
        doc.RootElement.TryGetProperty("errText", out _).Should().BeTrue($"{expectedType} must carry errText: {json}");
        doc.RootElement.TryGetProperty("errcode", out _).Should().BeFalse($"{expectedType} must not carry lowercase errcode: {json}");
        doc.RootElement.TryGetProperty("errtext", out _).Should().BeFalse($"{expectedType} must not carry lowercase errtext: {json}");
    }

    // -----------------------------------------------------------------
    //  The port string-or-number normalisation
    // -----------------------------------------------------------------

    [Fact]
    public void Accept_parses_numeric_port_from_the_spec_example()
    {
        // PWP-0222's accept example shows port as an unquoted number.
        var msg = (AcceptMessage)Parse("""{"type":"accept","seqno":3,"handle":1,"child":2,"remote":"M0XYZ","local":"G8PZT","port":2}""");

        msg.Handle.Should().Be(1);
        msg.Child.Should().Be(2);
        msg.Remote.Should().Be("M0XYZ");
        msg.Local.Should().Be("G8PZT");
        msg.Port.Should().Be("2");
    }

    [Fact]
    public void Accept_parses_string_port_as_real_xrouter_sends_it()
    {
        var msg = (AcceptMessage)Parse("""{"type":"accept","seqno":3,"handle":1,"child":2,"remote":"M0XYZ","local":"G8PZT","port":"2"}""");

        msg.Port.Should().Be("2");
    }

    [Fact]
    public void Recv_in_trace_mode_parses_numeric_port_and_undocumented_fields()
    {
        // Golden TRACE capture: port is a JSON number here (string in
        // DGRAM!), and tseq/ilen/pid/ptcl aren't in the published spec.
        var msg = (RecvMessage)Parse(
            """{"type":"recv","seqno":1,"handle":5,"action":"sent","port":1,"srce":"G9DUM","dest":"G9DUM-1","ctrl":0,"frametype":"I","rseq":0,"tseq":0,"cr":"C","ilen":2,"pid":240,"ptcl":"DATA","data":"i\r"}""");

        msg.Port.Should().Be("1");
        msg.Tseq.Should().Be(0);
        msg.Ilen.Should().Be(2);
        msg.Pid.Should().Be(240);
        msg.Ptcl.Should().Be("DATA");
        msg.FrameType.Should().Be("I");
        msg.Action.Should().Be("sent");
        msg.Srce.Should().Be("G9DUM");
        msg.Dest.Should().Be("G9DUM-1");
        msg.Cr.Should().Be("C");
    }

    [Fact]
    public void Recv_in_dgram_mode_parses_string_port_and_addressing()
    {
        var msg = (RecvMessage)Parse(
            """{"type":"recv","handle":7,"action":"rcvd","port":"2","remote":"G8PZT-3","local":"G9DUM-4","data":"hello UI\r"}""");

        msg.Port.Should().Be("2");
        msg.Remote.Should().Be("G8PZT-3");
        msg.Local.Should().Be("G9DUM-4");
        msg.Data.Should().Be("hello UI\r");
    }

    [Fact]
    public void Recv_parses_supervisory_trace_fields()
    {
        var msg = (RecvMessage)Parse(
            """{"type":"recv","seqno":11,"handle":50,"data":"hi","action":"rcvd","srce":"M0XYZ","dest":"G8PZT","ctrl":3,"frametype":"RR","rseq":4,"cr":"R","pf":"F"}""");

        msg.Seqno.Should().Be(11);
        msg.Handle.Should().Be(50);
        msg.FrameType.Should().Be("RR");
        msg.Action.Should().Be("rcvd");
        msg.Rseq.Should().Be(4);
        msg.Pf.Should().Be("F");
    }

    [Fact]
    public void Recv_serializes_frametype_all_lowercase()
    {
        // Casing trap: the wire field is "frametype", not "frameType".
        var json = ToJson(new RecvMessage { Handle = 5, Data = "x", FrameType = "I" });
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("frametype", out _).Should().BeTrue(json);
        doc.RootElement.TryGetProperty("frameType", out _).Should().BeFalse(json);
    }

    // -----------------------------------------------------------------
    //  Discriminator handling
    // -----------------------------------------------------------------

    [Fact]
    public void ConnectReply_in_the_specs_PascalCase_typo_form_still_parses()
    {
        var msg = Parse("""{"type":"ConnectReply","id":1,"handle":50,"errcode":0,"errtext":"Ok"}""");

        var typed = msg.Should().BeOfType<ConnectReplyMessage>().Subject;
        typed.Handle.Should().Be(50);
        typed.Id.Should().Be(1);
    }

    [Fact]
    public void ConnectReply_always_serializes_camelCase()
    {
        // We tolerate the spec's typo on read but never reproduce it.
        var json = ToJson(new ConnectReplyMessage { Handle = 50, ErrCode = 0, ErrText = "Ok" });
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be("connectReply");
    }

    [Fact]
    public void Unknown_type_yields_UnknownMessage_with_raw_json()
    {
        var msg = Parse("""{"type":"newFutureMessage","id":99,"foo":"bar"}""");

        var unknown = msg.Should().BeOfType<UnknownMessage>().Subject;
        unknown.Type.Should().Be("newFutureMessage");
        unknown.Id.Should().Be(99);
        unknown.Raw["foo"]!.GetValue<string>().Should().Be("bar");
    }

    [Fact]
    public void Unknown_type_lifts_seqno_when_present()
    {
        var msg = (UnknownMessage)Parse("""{"type":"futureNotify","seqno":12}""");
        msg.Seqno.Should().Be(12);
    }

    [Fact]
    public void Missing_type_throws_RhpProtocolException()
    {
        var act = () => Parse("""{"id":1,"errcode":0}""");
        act.Should().Throw<RhpProtocolException>();
    }

    [Fact]
    public void Non_string_type_throws_RhpProtocolException()
    {
        var act = () => Parse("""{"type":42,"id":1}""");
        act.Should().Throw<RhpProtocolException>();
    }

    [Fact]
    public void Non_object_payload_throws_RhpProtocolException()
    {
        var act = () => Parse("""[1,2,3]""");
        act.Should().Throw<RhpProtocolException>();
    }

    // -----------------------------------------------------------------
    //  Request serialization shape
    // -----------------------------------------------------------------

    [Fact]
    public void Auth_serializes_type_user_pass_and_id()
    {
        var json = ToJson(new AuthMessage { User = "g8pzt", Pass = "secret", Id = 1 });
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("type").GetString().Should().Be("auth");
        doc.RootElement.GetProperty("user").GetString().Should().Be("g8pzt");
        doc.RootElement.GetProperty("pass").GetString().Should().Be("secret");
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Open_omits_null_port_remote_and_id()
    {
        // XRouter never writes JSON nulls; absent fields are simply absent.
        var json = ToJson(new OpenMessage
        {
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Local = "G8PZT",
            Flags = (int)OpenFlags.Passive,
        });
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("local", out _).Should().BeTrue(json);
        doc.RootElement.TryGetProperty("port", out _).Should().BeFalse(json);
        doc.RootElement.TryGetProperty("remote", out _).Should().BeFalse(json);
        doc.RootElement.TryGetProperty("id", out _).Should().BeFalse(json);
    }

    // -----------------------------------------------------------------
    //  Field ABSENCE survives deserialization (absent ≠ empty — the wire
    //  answers errCode 12 for a missing handle/data, 3 only for unknown
    //  handles, and "data":"" is a legal zero-byte send)
    // -----------------------------------------------------------------

    [Fact]
    public void Send_with_data_absent_deserializes_to_null_not_empty()
    {
        var msg = (SendMessage)Parse("""{"type":"send","id":1,"handle":5}""");

        msg.Handle.Should().Be(5);
        msg.Data.Should().BeNull("an absent data field must be distinguishable from \"\"");
    }

    [Fact]
    public void Send_with_empty_data_deserializes_to_empty_not_null()
    {
        var msg = (SendMessage)Parse("""{"type":"send","id":1,"handle":5,"data":""}""");

        msg.Data.Should().Be("");
    }

    [Theory]
    [InlineData("""{"type":"close","id":1}""", typeof(CloseMessage))]
    [InlineData("""{"type":"send","id":1,"data":"x"}""", typeof(SendMessage))]
    [InlineData("""{"type":"bind","id":1,"local":"G8PZT"}""", typeof(BindMessage))]
    [InlineData("""{"type":"listen","id":1,"flags":0}""", typeof(ListenMessage))]
    [InlineData("""{"type":"connect","id":1,"remote":"G8PZT"}""", typeof(ConnectMessage))]
    [InlineData("""{"type":"status","id":1}""", typeof(StatusMessage))]
    [InlineData("""{"type":"sendto","id":1,"data":"x"}""", typeof(SendToMessage))]
    public void Requests_with_an_absent_handle_deserialize_to_null(string wire, Type expected)
    {
        var msg = Parse(wire);

        msg.Should().BeOfType(expected);
        var handle = (int?)expected.GetProperty("Handle")!.GetValue(msg);
        handle.Should().BeNull("an absent handle field must be distinguishable from 0");
    }

    [Fact]
    public void Reply_handle_is_omitted_when_null_matching_xrouters_parameter_error_shape()
    {
        // Live XRouter omits the handle echo on parameter errors ("Missing handle") —
        // there is nothing truthful to echo. Null must vanish, not serialize as 0.
        var json = ToJson(new CloseReplyMessage { Id = 1, ErrCode = 12, ErrText = "Missing handle" });
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("handle", out _).Should().BeFalse(json);
    }

    // -----------------------------------------------------------------
    //  hello — the removed (never-agreed) capability-discovery surface
    // -----------------------------------------------------------------

    [Fact]
    public void Hello_is_not_a_known_type_and_deserializes_as_UnknownMessage()
    {
        // The `hello`/`helloReply` capability-discovery extension was removed
        // (proposed in the rhp2lib field notes but never agreed —
        // packet-net/packet.net#449). The codec no longer recognises the type,
        // so a `hello` frame is an UnknownMessage like any other unsupported
        // type — which makes the server answer it via the unknown-type fallback
        // (helloReply errCode 2), exactly as real XRouter does.
        var msg = Parse("""{"type":"hello","id":31}""");

        var unknown = msg.Should().BeOfType<UnknownMessage>().Subject;
        unknown.Type.Should().Be("hello");
        unknown.Id.Should().Be(31);
    }

    [Fact]
    public void Status_notification_flags_decode_as_StatusFlags_bits()
    {
        var msg = (StatusMessage)Parse("""{"type":"status","seqno":2,"handle":9,"flags":6}""");

        var flags = (StatusFlags)(msg.Flags ?? 0);
        flags.Should().Be(StatusFlags.Connected | StatusFlags.Busy);
        flags.HasFlag(StatusFlags.ConOk).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    //  Round-trips: every catalogue type, with the type-first-key pin
    // -----------------------------------------------------------------

    public static TheoryData<RhpMessage> EveryMessageType() => new()
    {
        new AuthMessage { Id = 1, User = "g8pzt", Pass = "secret" },
        new AuthReplyMessage { Id = 1, ErrCode = 0, ErrText = "Ok" },
        new OpenMessage { Id = 2, Pfam = "ax25", Mode = "stream", Port = "1", Local = "G8PZT", Remote = "M0LTE-1", Flags = 0x80 },
        new OpenReplyMessage { Id = 2, Handle = 7, ErrCode = 0, ErrText = "Ok" },
        new SocketMessage { Id = 3, Pfam = "netrom", Mode = "seqpkt" },
        new SocketReplyMessage { Id = 3, Handle = 8, ErrCode = 0, ErrText = "Ok" },
        new BindMessage { Id = 4, Handle = 8, Local = "G8PZT-4", Port = "2" },
        new BindReplyMessage { Id = 4, Handle = 8, ErrCode = 0, ErrText = "Ok" },
        new ListenMessage { Id = 5, Handle = 8, Flags = 3 },
        new ListenReplyMessage { Id = 5, Handle = 8, ErrCode = 0, ErrText = "Ok" },
        new ConnectMessage { Id = 6, Handle = 8, Remote = "M0LTE-1" },
        new ConnectReplyMessage { Id = 6, Handle = 8, ErrCode = 0, ErrText = "Ok" },
        new SendMessage { Id = 7, Handle = 8, Data = "hello\r", Port = "1", Local = "G8PZT-4", Remote = "M0LTE-1" },
        new SendReplyMessage { Id = 7, Handle = 8, ErrCode = 0, ErrText = "Ok", Status = 6 },
        new SendToMessage { Id = 8, Handle = 9, Data = "ui\r", Port = "2", Local = "G8PZT-4", Remote = "M0LTE-1", Tos = 1 },
        new SendToReplyMessage { Id = 8, Handle = 9, ErrCode = 0, ErrText = "Ok" },
        new RecvMessage
        {
            Seqno = 1, Handle = 5, Data = "i\r", Port = "1", Local = "G9DUM-1", Remote = "G9DUM",
            Action = "sent", Srce = "G9DUM", Dest = "G9DUM-1", Ctrl = 0, FrameType = "I",
            Rseq = 0, Tseq = 0, Cr = "C", Pf = "P", Ilen = 2, Pid = 240, Ptcl = "DATA",
        },
        new AcceptMessage { Seqno = 3, Handle = 1, Child = 2, Remote = "M0XYZ", Local = "G8PZT", Port = "2" },
        new StatusMessage { Id = 9, Handle = 8, Flags = 6 },
        new StatusReplyMessage { Id = 9, Handle = 8, ErrCode = 0, ErrText = "Ok" },
        new CloseMessage { Id = 10, Handle = 8 },
        new CloseReplyMessage { Id = 10, Handle = 8, ErrCode = 0, ErrText = "Ok" },
    };

    [Theory]
    [MemberData(nameof(EveryMessageType))]
    public void Serialize_then_deserialize_round_trips_every_field(RhpMessage original)
    {
        var bytes = RhpJson.Serialize(original);
        var parsed = RhpJson.Deserialize(bytes);

        parsed.Should().BeOfType(original.GetType());
        // PreferringRuntimeMemberTypes: compare the concrete DTO's members,
        // not just the RhpMessage base members the declared type exposes.
        parsed.Should().BeEquivalentTo(original, options => options.PreferringRuntimeMemberTypes());

        // And the stronger wire pin: re-serializing the parsed message
        // reproduces the original bytes exactly.
        RhpJson.Serialize(parsed).Should().Equal(bytes);
    }

    [Theory]
    [MemberData(nameof(EveryMessageType))]
    public void Type_is_always_the_first_key_in_emitted_json(RhpMessage original)
    {
        // XRouter dispatches on `type` as it streams the object; emitting
        // it first matches its own output and keeps that cheap.
        var json = Encoding.UTF8.GetString(RhpJson.Serialize(original));
        json.Should().StartWith("{\"type\":");
    }
}
