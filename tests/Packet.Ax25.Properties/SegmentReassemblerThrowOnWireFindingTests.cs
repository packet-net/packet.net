using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Properties;

/// <summary>
/// Regression tests for the SP-004 v2.2-fuzz finding of 2026-06-03: a hostile /
/// malformed segment sequence delivered as PID-0x08 I-frames reaches
/// <see cref="SegmentationLayer.OnDataIndication"/> on the receive path. The
/// finding observed that <see cref="Reassembler.Push"/>'s strict, documented
/// throw was reachable from the wire and masked only by
/// <see cref="Ax25Listener"/>'s blanket inbound catch-all (a silent drop, with
/// a mid-series reassembler left able to mis-react to the next valid segment).
/// See <c>tools/Packet.Fuzz/FINDINGS.md</c> 2026-06-03 for the original
/// write-up + hex inputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved: clean-reject at the wire seam.</b> Each of the finding's
/// malformed inputs is now <b>dropped cleanly</b> at
/// <see cref="SegmentationLayer.OnDataIndication"/> — the seam catches
/// <see cref="Reassembler.Push"/>'s documented
/// <see cref="System.ArgumentException"/> / <see cref="System.InvalidOperationException"/>,
/// resets any in-progress reassembly, and returns <c>null</c> (the same
/// "nothing to deliver yet" signal as a legitimate mid-series segment).
/// Nothing escapes to the caller, and nothing depends on the listener's
/// catch-all. This matches Dire Wolf's reassembler (logs a protocol error and
/// drops) and the §6.6 / Fig C5.2 treatment of a bad segment as discardable.
/// </para>
/// <para>
/// <b>The low-level contract is intentionally unchanged.</b>
/// <see cref="Reassembler.Push"/> still throws on a protocol violation — its
/// direct-call tests in <c>tests/Packet.Ax25.Tests/Session/SegmenterTests.cs</c>
/// continue to pin that strict contract. Only this wire-facing boundary softens
/// the throw to a drop. These tests pin the seam behaviour; if it ever regresses
/// to throwing (or stops resetting), they fail.
/// </para>
/// </remarks>
public class SegmentReassemblerThrowOnWireFindingTests
{
    private static SegmentationLayer NewWireLayer(Ax25SessionQuirks? quirks = null)
    {
        var ctx = new Ax25SessionContext
        {
            Local = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Quirks = quirks ?? Ax25SessionQuirks.Default,
        };
        return new SegmentationLayer(ctx);
    }

    private static DataLinkDataIndication Seg(params byte[] infoField)
        => new(infoField, Ax25Frame.PidSegmented);

    /// <summary>
    /// Case 1 — a non-First segment with no prior First reaches the wire seam and
    /// is <b>dropped cleanly</b> (no throw escapes, nothing delivered).
    /// Hex input (segment info field): <c>05 AA BB</c> (First bit clear,
    /// remaining-count = 5).
    /// </summary>
    [Fact]
    public void Wire_NonFirst_Without_Prior_First_Is_Dropped_Cleanly()
    {
        var layer = NewWireLayer();
        var act = () => layer.OnDataIndication(Seg(0x05, 0xAA, 0xBB));

        act.Should().NotThrow(
            "FINDING 2026-06-03 (resolved): a missing-first segment off the wire is dropped, not thrown");
        layer.OnDataIndication(Seg(0x05, 0xAA, 0xBB))
            .Should().BeNull("a malformed segment delivers nothing upward");
    }

    /// <summary>
    /// Case 2 — an empty info field on a PID-0x08 indication reaches the wire seam
    /// and is <b>dropped cleanly</b>.
    /// Hex input (segment info field): <c>(empty)</c>.
    /// </summary>
    [Fact]
    public void Wire_Empty_Segment_Info_Field_Is_Dropped_Cleanly()
    {
        var layer = NewWireLayer();
        var act = () => layer.OnDataIndication(Seg());

        act.Should().NotThrow(
            "FINDING 2026-06-03 (resolved): an empty 0x08-PID info field off the wire is dropped, not thrown");
        layer.OnDataIndication(Seg()).Should().BeNull("an empty segment delivers nothing upward");
    }

    /// <summary>
    /// Case 3 — under the default (inner-PID) quirk, a First segment carrying only
    /// the F/X octet (no inner-PID octet) reaches the wire seam and is
    /// <b>dropped cleanly</b>.
    /// Hex input (segment info field): <c>80</c> (First bit set, remaining-count = 0,
    /// no inner-PID octet).
    /// </summary>
    [Fact]
    public void Wire_InnerPid_First_Missing_Pid_Octet_Is_Dropped_Cleanly()
    {
        var layer = NewWireLayer(Ax25SessionQuirks.Default);   // SegmentFirstCarriesL3Pid = true
        var act = () => layer.OnDataIndication(Seg(0x80));

        act.Should().NotThrow(
            "FINDING 2026-06-03 (resolved): an inner-PID First lacking its PID octet, off the wire, is dropped");
        layer.OnDataIndication(Seg(0x80)).Should().BeNull("a short inner-PID First delivers nothing upward");
    }

    /// <summary>
    /// Case 4 — an out-of-sequence continuation (a valid First, then a segment
    /// whose remaining-count skips a value) is <b>dropped cleanly</b>.
    /// Hex inputs: First <c>85 CC</c> (StrictlyFaithful so no inner PID is read;
    /// First bit + remaining = 5), then <c>03 DD</c> (remaining = 3 where 4 was
    /// expected).
    /// </summary>
    [Fact]
    public void Wire_Out_Of_Sequence_Continuation_Is_Dropped_Cleanly()
    {
        // Use StrictlyFaithful so the first segment isn't consumed as inner-PID.
        var layer = NewWireLayer(Ax25SessionQuirks.StrictlyFaithful);

        // First segment: First bit set, remaining 5 — accepted, starts the series.
        layer.OnDataIndication(Seg(Segmenter.FirstBit | 5, 0xCC)).Should().BeNull("mid-series, nothing delivered yet");

        // Continuation with remaining 3 (expected 4) — out of sequence.
        var act = () => layer.OnDataIndication(Seg(3, 0xDD));
        act.Should().NotThrow(
            "FINDING 2026-06-03 (resolved): an out-of-sequence continuation off the wire is dropped, not thrown");
    }

    /// <summary>
    /// The reset half of the fix: a malformed segment that lands mid-series must
    /// not poison a subsequent <i>valid</i> series. After an out-of-sequence
    /// continuation is dropped, a brand-new well-formed series reassembles intact.
    /// </summary>
    [Fact]
    public void Wire_Valid_Series_After_A_Malformed_One_Still_Reassembles()
    {
        var layer = NewWireLayer(Ax25SessionQuirks.StrictlyFaithful);   // figure-literal: no inner PID

        // Start a series, then break it with an out-of-sequence continuation
        // (which is dropped + resets the reassembler).
        layer.OnDataIndication(Seg(Segmenter.FirstBit | 5, 0xCC)).Should().BeNull("mid-series, nothing delivered yet");
        layer.OnDataIndication(Seg(3, 0xDD)).Should().BeNull("the out-of-sequence segment is dropped");

        // A fresh, well-formed two-segment series now arrives. If the reset
        // worked, the stale partial state is gone and this reassembles cleanly.
        layer.OnDataIndication(Seg(Segmenter.FirstBit | 1, 0xAA)).Should().BeNull("First of the new series");
        var delivered = layer.OnDataIndication(Seg(0, 0xBB));

        delivered.Should().NotBeNull("the new series completes on its last segment");
        delivered!.Info.ToArray().Should().Equal(new byte[] { 0xAA, 0xBB },
            "the reassembled payload is exactly the new series' data — no leakage from the poisoned series");
        delivered.Pid.Should().Be(SegmentationLayer.FigureLiteralReassembledPid);
    }

    /// <summary>
    /// A stray non-First (dropped) immediately followed by a complete valid series
    /// also reassembles — the drop leaves the reassembler in the clean
    /// "waiting for a First" state, exactly where it started.
    /// </summary>
    [Fact]
    public void Wire_Valid_Series_After_A_Stray_NonFirst_Still_Reassembles()
    {
        var layer = NewWireLayer(Ax25SessionQuirks.StrictlyFaithful);

        // Stray non-First with no prior First — dropped.
        layer.OnDataIndication(Seg(0x05, 0xAA, 0xBB)).Should().BeNull("stray non-First is dropped");

        // A well-formed single-segment series then arrives and is delivered.
        var delivered = layer.OnDataIndication(Seg(Segmenter.FirstBit | 0, 0x42));
        delivered.Should().NotBeNull();
        delivered!.Info.ToArray().Should().Equal(0x42);
    }

    /// <summary>
    /// Sanity counterpart: a well-formed single-segment series at the wire seam
    /// does <b>not</b> throw and delivers the reassembled payload. Confirms the
    /// finding is specifically about <i>malformed</i> input, not the happy path.
    /// </summary>
    [Fact]
    public void Wire_WellFormed_Single_Segment_Does_Not_Throw_And_Delivers()
    {
        var layer = NewWireLayer(Ax25SessionQuirks.StrictlyFaithful);   // figure-literal: no inner PID

        // First + last in one: First bit set, remaining 0, one data byte.
        var delivered = layer.OnDataIndication(Seg(Segmenter.FirstBit | 0, 0x42));

        delivered.Should().NotBeNull();
        delivered!.Info.ToArray().Should().Equal(0x42);
        delivered.Pid.Should().Be(SegmentationLayer.FigureLiteralReassembledPid);
    }
}
