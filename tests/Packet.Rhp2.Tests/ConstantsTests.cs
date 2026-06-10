using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// Pins the wire-visible constant values: discriminator strings (including
/// the casing traps), flag bit positions, and the error-code table with its
/// canonical texts. These are contract, not implementation detail.
/// </summary>
public class ConstantsTests
{
    [Fact]
    public void Message_type_discriminators_match_the_wire_exactly()
    {
        // The two casing traps first: lowercase "to", camelCase "Reply".
        RhpMessageType.SendTo.Should().Be("sendto");
        RhpMessageType.SendToReply.Should().Be("sendtoReply");

        RhpMessageType.Auth.Should().Be("auth");
        RhpMessageType.AuthReply.Should().Be("authReply");
        RhpMessageType.Open.Should().Be("open");
        RhpMessageType.OpenReply.Should().Be("openReply");
        RhpMessageType.Socket.Should().Be("socket");
        RhpMessageType.SocketReply.Should().Be("socketReply");
        RhpMessageType.Bind.Should().Be("bind");
        RhpMessageType.BindReply.Should().Be("bindReply");
        RhpMessageType.Listen.Should().Be("listen");
        RhpMessageType.ListenReply.Should().Be("listenReply");
        RhpMessageType.Connect.Should().Be("connect");
        RhpMessageType.ConnectReply.Should().Be("connectReply");
        RhpMessageType.Send.Should().Be("send");
        RhpMessageType.SendReply.Should().Be("sendReply");
        RhpMessageType.Recv.Should().Be("recv");
        RhpMessageType.Accept.Should().Be("accept");
        RhpMessageType.Status.Should().Be("status");
        RhpMessageType.StatusReply.Should().Be("statusReply");
        RhpMessageType.Close.Should().Be("close");
        RhpMessageType.CloseReply.Should().Be("closeReply");
    }

    [Fact]
    public void Protocol_families_and_socket_modes_are_lowercase_wire_strings()
    {
        ProtocolFamily.Unix.Should().Be("unix");
        ProtocolFamily.Inet.Should().Be("inet");
        ProtocolFamily.Ax25.Should().Be("ax25");
        ProtocolFamily.NetRom.Should().Be("netrom");

        SocketMode.Stream.Should().Be("stream");
        SocketMode.Dgram.Should().Be("dgram");
        SocketMode.Seqpkt.Should().Be("seqpkt");
        SocketMode.Custom.Should().Be("custom");
        SocketMode.SemiRaw.Should().Be("semiraw");
        SocketMode.Trace.Should().Be("trace");
        SocketMode.Raw.Should().Be("raw");
    }

    [Fact]
    public void Open_flag_bits_match_the_spec()
    {
        ((int)OpenFlags.Passive).Should().Be(0x00);
        ((int)OpenFlags.TraceIncoming).Should().Be(0x01);
        ((int)OpenFlags.TraceOutgoing).Should().Be(0x02);
        ((int)OpenFlags.TraceSupervisory).Should().Be(0x04);
        ((int)OpenFlags.Active).Should().Be(0x80);
    }

    [Fact]
    public void Status_flag_bits_match_the_spec()
    {
        ((int)StatusFlags.None).Should().Be(0);
        ((int)StatusFlags.ConOk).Should().Be(1);
        ((int)StatusFlags.Connected).Should().Be(2);
        ((int)StatusFlags.Busy).Should().Be(4);
    }

    [Theory]
    [InlineData(RhpErrorCode.Ok, "Ok")]
    [InlineData(RhpErrorCode.Unspecified, "Unspecified")]
    [InlineData(RhpErrorCode.BadOrMissingType, "Bad or missing type")]
    [InlineData(RhpErrorCode.InvalidHandle, "Invalid handle")]
    [InlineData(RhpErrorCode.NoMemory, "No memory")]
    [InlineData(RhpErrorCode.BadOrMissingMode, "Bad or missing mode")]
    [InlineData(RhpErrorCode.InvalidLocalAddress, "Invalid local address")]
    [InlineData(RhpErrorCode.InvalidRemoteAddress, "Invalid remote address")]
    [InlineData(RhpErrorCode.BadOrMissingFamily, "Bad or missing family")]
    [InlineData(RhpErrorCode.DuplicateSocket, "Duplicate socket")]
    [InlineData(RhpErrorCode.NoSuchPort, "No such port")]
    [InlineData(RhpErrorCode.InvalidProtocol, "Invalid protocol")]
    [InlineData(RhpErrorCode.BadParameter, "Bad parameter")]
    [InlineData(RhpErrorCode.NoBuffers, "No buffers")]
    [InlineData(RhpErrorCode.Unauthorised, "Unauthorised")]
    // Note the spec's own inconsistent capitalisation on the next two —
    // canonical text reproduces it rather than tidying it up.
    [InlineData(RhpErrorCode.NoRoute, "No Route")]
    [InlineData(RhpErrorCode.OperationNotSupported, "Operation not supported")]
    // 17 is XRouter-observed, not in the published spec.
    [InlineData(RhpErrorCode.NotConnected, "Not connected")]
    public void Error_code_canonical_text_matches_the_spec(int code, string expected)
    {
        RhpErrorCode.Text(code).Should().Be(expected);
    }

    [Fact]
    public void Error_codes_are_the_pinned_integers()
    {
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
    public void Unknown_error_code_text_names_the_code()
    {
        RhpErrorCode.Text(99).Should().Be("Unknown (99)");
    }
}
