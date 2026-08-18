using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// Pins the codec contract that the shared-library convergence with rhp2lib-net relies on
/// (packet-net/packet.net#474). <see cref="Packet.Rhp2"/> and rhp2lib-net's embedded codec
/// (<c>RhpV2.Client.Protocol</c>) are byte-identical on the wire today; once <c>Packet.Rhp2</c>
/// is published as the single shared codec package, rhp2lib-net can depend on it and drop its
/// copy. These tests guard the surface that must stay stable for that to be a drop-in:
/// the <see cref="RhpErrorCode"/> value/text table (the one place a silent code-vs-text drift
/// would corrupt the wire on every reply), and the catalogue's <c>type</c> discriminators.
/// A change here is a change to the shared wire format - make it deliberately, on both sides.
/// </summary>
public class ConvergenceTests
{
    // The full RhpErrorCode table as rhp2lib-net's RhpV2.Client.Protocol.RhpErrorCode spells it,
    // value -> canonical errText, Ok(0) .. NotConnected(17). Verified identical to rhp2lib-net's
    // codec when #474's convergence assessment was made. The capitalisation quirks are part of
    // the wire contract: "No Route" capitalised, "Operation not supported" not - XRouter emits
    // exactly these, so a server built on this codec must too.
    public static TheoryData<int, string> ErrorCodeTable() => new()
    {
        { 0, "Ok" },
        { 1, "Unspecified" },
        { 2, "Bad or missing type" },
        { 3, "Invalid handle" },
        { 4, "No memory" },
        { 5, "Bad or missing mode" },
        { 6, "Invalid local address" },
        { 7, "Invalid remote address" },
        { 8, "Bad or missing family" },
        { 9, "Duplicate socket" },
        { 10, "No such port" },
        { 11, "Invalid protocol" },
        { 12, "Bad parameter" },
        { 13, "No buffers" },
        { 14, "Unauthorised" },
        { 15, "No Route" },
        { 16, "Operation not supported" },
        { 17, "Not connected" },
    };

    [Theory]
    [MemberData(nameof(ErrorCodeTable))]
    public void ErrorCode_value_maps_to_the_shared_canonical_text(int code, string expectedText)
        => RhpErrorCode.Text(code).Should().Be(expectedText);

    [Fact]
    public void ErrorCode_constants_have_the_shared_numeric_values()
    {
        // Pin the named constants to their wire numbers - a renumbering would diverge from
        // rhp2lib-net's identical table and corrupt every reply that carries the code.
        RhpErrorCode.Ok.Should().Be(0);
        RhpErrorCode.Unspecified.Should().Be(1);
        RhpErrorCode.BadOrMissingType.Should().Be(2);
        RhpErrorCode.InvalidHandle.Should().Be(3);
        RhpErrorCode.NoMemory.Should().Be(4);
        RhpErrorCode.BadOrMissingMode.Should().Be(5);
        RhpErrorCode.InvalidLocalAddress.Should().Be(6);
        RhpErrorCode.InvalidRemoteAddress.Should().Be(7);
        RhpErrorCode.BadOrMissingFamily.Should().Be(8);
        RhpErrorCode.DuplicateSocket.Should().Be(9);
        RhpErrorCode.NoSuchPort.Should().Be(10);
        RhpErrorCode.InvalidProtocol.Should().Be(11);
        RhpErrorCode.BadParameter.Should().Be(12);
        RhpErrorCode.NoBuffers.Should().Be(13);
        RhpErrorCode.Unauthorised.Should().Be(14);
        RhpErrorCode.NoRoute.Should().Be(15);
        RhpErrorCode.OperationNotSupported.Should().Be(16);
        RhpErrorCode.NotConnected.Should().Be(17);
    }

    [Fact]
    public void Unknown_error_code_text_matches_the_shared_fallback_shape()
        => RhpErrorCode.Text(999).Should().Be("Unknown (999)");

    [Theory]
    [InlineData(RhpMessageType.Auth, "auth")]
    [InlineData(RhpMessageType.AuthReply, "authReply")]
    [InlineData(RhpMessageType.Open, "open")]
    [InlineData(RhpMessageType.OpenReply, "openReply")]
    [InlineData(RhpMessageType.Socket, "socket")]
    [InlineData(RhpMessageType.SocketReply, "socketReply")]
    [InlineData(RhpMessageType.Bind, "bind")]
    [InlineData(RhpMessageType.BindReply, "bindReply")]
    [InlineData(RhpMessageType.Listen, "listen")]
    [InlineData(RhpMessageType.ListenReply, "listenReply")]
    [InlineData(RhpMessageType.Connect, "connect")]
    [InlineData(RhpMessageType.ConnectReply, "connectReply")]
    [InlineData(RhpMessageType.Send, "send")]
    [InlineData(RhpMessageType.SendReply, "sendReply")]
    [InlineData(RhpMessageType.SendTo, "sendto")]          // lowercase "to" - the wire casing trap
    [InlineData(RhpMessageType.SendToReply, "sendtoReply")]
    [InlineData(RhpMessageType.Recv, "recv")]
    [InlineData(RhpMessageType.Accept, "accept")]
    [InlineData(RhpMessageType.Status, "status")]
    [InlineData(RhpMessageType.StatusReply, "statusReply")]
    [InlineData(RhpMessageType.Close, "close")]
    [InlineData(RhpMessageType.CloseReply, "closeReply")]
    public void MessageType_discriminator_has_the_shared_wire_spelling(string constant, string expectedWire)
        => constant.Should().Be(expectedWire);

    [Theory]
    [InlineData(ProtocolFamily.Ax25, "ax25")]
    [InlineData(ProtocolFamily.NetRom, "netrom")]
    [InlineData(ProtocolFamily.Inet, "inet")]
    [InlineData(ProtocolFamily.Unix, "unix")]
    public void ProtocolFamily_has_the_shared_wire_spelling(string constant, string expectedWire)
        => constant.Should().Be(expectedWire);

    [Theory]
    [InlineData(SocketMode.Stream, "stream")]
    [InlineData(SocketMode.Dgram, "dgram")]
    [InlineData(SocketMode.Seqpkt, "seqpkt")]
    [InlineData(SocketMode.Custom, "custom")]
    [InlineData(SocketMode.SemiRaw, "semiraw")]
    [InlineData(SocketMode.Trace, "trace")]
    [InlineData(SocketMode.Raw, "raw")]
    public void SocketMode_has_the_shared_wire_spelling(string constant, string expectedWire)
        => constant.Should().Be(expectedWire);

    [Fact]
    public void OpenFlags_bits_match_the_shared_wire_values()
    {
        ((int)OpenFlags.Passive).Should().Be(0x00);
        ((int)OpenFlags.TraceIncoming).Should().Be(0x01);
        ((int)OpenFlags.TraceOutgoing).Should().Be(0x02);
        ((int)OpenFlags.TraceSupervisory).Should().Be(0x04);
        ((int)OpenFlags.Active).Should().Be(0x80);
    }

    [Fact]
    public void StatusFlags_bits_match_the_shared_wire_values()
    {
        ((int)StatusFlags.None).Should().Be(0);
        ((int)StatusFlags.ConOk).Should().Be(1);
        ((int)StatusFlags.Connected).Should().Be(2);
        ((int)StatusFlags.Busy).Should().Be(4);
    }

    [Fact]
    public void Framing_payload_cap_matches_the_shared_16bit_limit()
        => RhpFraming.MaxPayloadLength.Should().Be(0xFFFF);
}
