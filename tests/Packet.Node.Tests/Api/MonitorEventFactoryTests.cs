using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;

namespace Packet.Node.Tests.Api;

/// <summary>
/// Unit tests for <see cref="MonitorEventFactory"/> — the pure decode of a traced
/// <see cref="Ax25FrameEventArgs"/> into the web monitor's <see cref="MonitorEvent"/>
/// wire shape. Each test builds a real frame via the <see cref="Ax25Frame"/>
/// factories and asserts the projected type/class, N(S)/N(R) presence, PID name,
/// direction, command flag, and raw-byte fidelity. No WAF or host — this is the
/// decode in isolation.
/// </summary>
public sealed class MonitorEventFactoryTests
{
    private static readonly Callsign Dest = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Src = Callsign.Parse("G7XYZ-2");

    private static MonitorEvent Decode(Ax25Frame frame, FrameDirection direction = FrameDirection.Received)
    {
        var args = new Ax25FrameEventArgs
        {
            Frame = frame,
            Direction = direction,
            Timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        };
        return MonitorEventFactory.From(seq: 7, portId: "vhf-1", args);
    }

    [Fact]
    public void I_frame_decodes_with_ns_nr_pid_and_class_I()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        var frame = Ax25Frame.I(Dest, Src, nr: 3, ns: 5, payload, pid: Ax25Frame.PidNetRom, pollBit: true);

        var evt = Decode(frame);

        evt.Type.Should().Be("I");
        evt.ClassKind.Should().Be("I");
        evt.Ns.Should().Be(5);
        evt.Nr.Should().Be(3);
        evt.Pf.Should().Be(1);
        evt.Pid.Should().Be("0xCF");
        evt.PidName.Should().Be("NET/ROM");
        evt.Length.Should().Be(frame.ToBytes().Length);
        // The frame is built as a command (I-frames always are).
        evt.Command.Should().BeTrue();
    }

    [Fact]
    public void Ui_frame_with_no_layer3_pid_decodes_as_class_U()
    {
        var frame = Ax25Frame.Ui(Dest, Src, "hello"u8, pid: Ax25Frame.PidNoLayer3);

        var evt = Decode(frame);

        evt.Type.Should().Be("UI");
        evt.ClassKind.Should().Be("U");
        evt.Pid.Should().Be("0xF0");
        evt.PidName.Should().Be("No layer 3");
        // U-frames carry neither N(S) nor N(R).
        evt.Ns.Should().BeNull();
        evt.Nr.Should().BeNull();
    }

    [Fact]
    public void Rr_decodes_as_S_frame_with_nr_but_no_ns()
    {
        var frame = Ax25Frame.Rr(Dest, Src, nr: 4, isCommand: true);

        var evt = Decode(frame);

        evt.Type.Should().Be("RR");
        evt.ClassKind.Should().Be("S");
        evt.Nr.Should().Be(4);
        evt.Ns.Should().BeNull();
        // S-frames have no PID.
        evt.Pid.Should().BeNull();
        evt.PidName.Should().BeNull();
    }

    [Fact]
    public void Rej_decodes_as_S_frame()
    {
        var frame = Ax25Frame.Rej(Dest, Src, nr: 2, isCommand: false);

        var evt = Decode(frame);

        evt.Type.Should().Be("REJ");
        evt.ClassKind.Should().Be("S");
        evt.Nr.Should().Be(2);
        evt.Ns.Should().BeNull();
    }

    [Fact]
    public void Srej_decodes_as_S_frame()
    {
        var frame = Ax25Frame.Srej(Dest, Src, nr: 6, isCommand: false);

        var evt = Decode(frame);

        evt.Type.Should().Be("SREJ");
        evt.ClassKind.Should().Be("S");
        evt.Nr.Should().Be(6);
    }

    [Fact]
    public void Sabm_decodes_as_U_frame_command()
    {
        var frame = Ax25Frame.Sabm(Dest, Src);

        var evt = Decode(frame);

        evt.Type.Should().Be("SABM");
        evt.ClassKind.Should().Be("U");
        evt.Nr.Should().BeNull();
        evt.Ns.Should().BeNull();
        evt.Command.Should().BeTrue();
    }

    [Fact]
    public void Disc_decodes_as_U_frame()
    {
        var evt = Decode(Ax25Frame.Disc(Dest, Src));

        evt.Type.Should().Be("DISC");
        evt.ClassKind.Should().Be("U");
        evt.Command.Should().BeTrue();
    }

    [Fact]
    public void Ua_decodes_as_U_frame_response()
    {
        var evt = Decode(Ax25Frame.Ua(Dest, Src));

        evt.Type.Should().Be("UA");
        evt.ClassKind.Should().Be("U");
        // UA is a response — dest C=0, source C=1.
        evt.Command.Should().BeFalse();
    }

    [Fact]
    public void Direction_maps_received_to_in_and_transmitted_to_out()
    {
        var frame = Ax25Frame.Rr(Dest, Src, nr: 0, isCommand: true);

        Decode(frame, FrameDirection.Received).Direction.Should().Be("in");
        Decode(frame, FrameDirection.Transmitted).Direction.Should().Be("out");
    }

    [Fact]
    public void Source_and_dest_callsigns_are_projected()
    {
        var evt = Decode(Ax25Frame.Ui(Dest, Src, "x"u8));

        evt.Source.Should().Be("G7XYZ-2");
        evt.Dest.Should().Be("M0LTE-1");
        evt.PortId.Should().Be("vhf-1");
        evt.Seq.Should().Be(7);
    }

    [Fact]
    public void Raw_equals_the_frames_bytes_widened_to_ints()
    {
        var frame = Ax25Frame.I(Dest, Src, nr: 1, ns: 2, "payload"u8);
        var bytes = frame.ToBytes();

        var evt = Decode(frame);

        evt.Raw.Should().Equal(bytes.Select(b => (int)b));
    }

    public static TheoryData<Ax25Frame, string, string> SSubtypeFrames() => new()
    {
        { Ax25Frame.Rr(Dest, Src, nr: 0, isCommand: true), "RR", "S" },
        { Ax25Frame.Rnr(Dest, Src, nr: 0, isCommand: true), "RNR", "S" },
        { Ax25Frame.Rej(Dest, Src, nr: 0, isCommand: true), "REJ", "S" },
        { Ax25Frame.Srej(Dest, Src, nr: 0, isCommand: true), "SREJ", "S" },
    };

    [Theory]
    [MemberData(nameof(SSubtypeFrames))]
    public void Classify_discriminates_S_subtypes_from_the_SS_bits(Ax25Frame frame, string expectedType, string expectedClass)
    {
        // The SS bits at control[3..2] pick the supervisory subtype; the "01"
        // low bits mark it an S-frame.
        var (type, classKind) = MonitorEventFactory.Classify(frame);

        type.Should().Be(expectedType);
        classKind.Should().Be(expectedClass);
    }

    [Fact]
    public void Classify_separates_I_S_and_U_on_the_low_control_bits()
    {
        // I-frame: control bit 0 = 0.
        MonitorEventFactory.Classify(Ax25Frame.I(Dest, Src, nr: 0, ns: 0, "x"u8)).ClassKind.Should().Be("I");
        // S-frame: control bits 1-0 = 01.
        MonitorEventFactory.Classify(Ax25Frame.Rr(Dest, Src, nr: 0, isCommand: true)).ClassKind.Should().Be("S");
        // U-frame: control bits 1-0 = 11 (UI base = 0x03).
        MonitorEventFactory.Classify(Ax25Frame.Ui(Dest, Src, "x"u8)).ClassKind.Should().Be("U");
    }
}
