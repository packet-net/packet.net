using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Properties;

/// <summary>
/// Round-trip + fuzz properties for the AX.25 v2.2 §6.6 segmenter / reassembler
/// (<see cref="Segmenter"/> / <see cref="Reassembler"/>). The example-based
/// suite <c>tests/Packet.Ax25.Tests/Session/SegmenterTests.cs</c> pins specific
/// payload sizes and the Dire Wolf worked example; these properties assert the
/// identity round-trip across an arbitrary payload + N1 domain, for BOTH the
/// figure-literal and the inner-PID (<see cref="Ax25SessionQuirks.SegmentFirstCarriesL3Pid"/>)
/// formats.
/// </summary>
/// <remarks>
/// <para>What these pin (per the workstream brief, item c):</para>
/// <list type="bullet">
/// <item>Random payload + N1 → segment → reassemble → identity (figure-literal).</item>
/// <item>Random payload + N1 → segment → reassemble → identity (inner-PID), AND
/// the original L3 PID is recovered via <see cref="Reassembler.LastRecoveredPid"/>.</item>
/// <item>The same identity through the on-the-wire <see cref="SegmentationLayer"/>
/// send → receive seam, under both quirk settings.</item>
/// <item>The reassembler never suffers a <i>crash-class</i> exception on a hostile
/// segment sequence (its documented <see cref="System.InvalidOperationException"/>
/// / <see cref="System.ArgumentException"/> contract is tolerated; see
/// <c>FINDINGS.md</c> 2026-06-03).</item>
/// </list>
/// </remarks>
public class SegmentationRoundTripProperties
{
    /// <summary>
    /// Figure-literal: any payload (bounded so the 128-segment ceiling isn't the
    /// thing under test) segmented at any valid N1 reassembles to the identical
    /// bytes, and no inner PID is recovered (the figure-literal format carries
    /// none).
    /// </summary>
    [Property(MaxTest = 500)]
    public void FigureLiteral_Segment_Then_Reassemble_Is_Identity(byte[] payload, byte n1Raw)
    {
        payload ??= [];
        var (n1, capped) = ClampPayloadToCeiling(payload, n1Raw, innerPid: false);

        var segments = Segmenter.Segment(capped, n1);   // innerPid: null = figure-literal
        var reassembler = new Reassembler();            // expectInnerPid: false

        byte[]? completed = null;
        foreach (var seg in segments)
        {
            completed = reassembler.Push(seg);
        }

        completed.Should().NotBeNull("the last segment must complete the series");
        completed!.Should().Equal(capped, "figure-literal segment → reassemble is the identity");
        reassembler.LastRecoveredPid.Should().BeNull("figure-literal carries no inner L3 PID");
    }

    /// <summary>
    /// Inner-PID (Dire Wolf): any payload at any valid N1 (≥3) reassembles to the
    /// identical bytes AND the original L3 PID is recovered - so segmentation
    /// under the default quirk no longer loses the Layer-3 PID. The PID is drawn
    /// from FsCheck so it isn't accidentally the figure-literal default.
    /// </summary>
    [Property(MaxTest = 500)]
    public void InnerPid_Segment_Then_Reassemble_Recovers_Payload_And_L3_Pid(
        byte[] payload, byte n1Raw, byte innerPid)
    {
        payload ??= [];
        var (n1, capped) = ClampPayloadToCeiling(payload, n1Raw, innerPid: true);

        var segments = Segmenter.Segment(capped, n1, innerPid: innerPid);
        var reassembler = new Reassembler(expectInnerPid: true);

        byte[]? completed = null;
        foreach (var seg in segments)
        {
            completed = reassembler.Push(seg);
        }

        completed.Should().NotBeNull();
        completed!.Should().Equal(capped, "inner-PID segment → reassemble is the identity");
        reassembler.LastRecoveredPid.Should().Be(innerPid,
            "the inner-PID format carries the original L3 PID on the first segment, recovered on reassembly");
    }

    /// <summary>
    /// The first segment of a series always has the First bit set and a remaining
    /// count equal to the number of segments after it; every later segment has
    /// the First bit clear and a strictly-decreasing count ending at 0. Property
    /// form of the figure-2 header invariant, across both formats.
    /// </summary>
    [Property(MaxTest = 500)]
    public void Segment_Headers_Follow_Figure_6_2(byte[] payload, byte n1Raw, bool innerPidFormat, byte innerPid)
    {
        payload ??= [];
        var (n1, capped) = ClampPayloadToCeiling(payload, n1Raw, innerPidFormat);
        var segments = Segmenter.Segment(capped, n1, innerPidFormat ? innerPid : (byte?)null);

        segments.Count.Should().BeInRange(1, Segmenter.MaxSegments);

        for (int i = 0; i < segments.Count; i++)
        {
            byte header = segments[i][0];
            bool first = (header & Segmenter.FirstBit) != 0;
            int remaining = header & Segmenter.CountMask;

            if (i == 0)
            {
                first.Should().BeTrue("segment 0 is the First");
                remaining.Should().Be(segments.Count - 1, "First's remaining count = segments after it");
            }
            else
            {
                first.Should().BeFalse($"segment {i} is not the First");
                remaining.Should().Be(segments.Count - 1 - i, "remaining count counts down to 0");
            }
        }
    }

    /// <summary>
    /// The on-the-wire seam round-trips: <see cref="SegmentationLayer.BuildSendRequests"/>
    /// (send side) produces a sequence of PID-0x08 DL-DATA requests that, fed
    /// back through a receiving <see cref="SegmentationLayer.OnDataIndication"/>
    /// (receive side), deliver a single reassembled indication identical to the
    /// original payload - with the original L3 PID under the default quirk, or
    /// 0xF0 under StrictlyFaithful. Both peers must agree on the format (as they
    /// would after XID negotiation).
    /// </summary>
    [Property(MaxTest = 300)]
    public void SegmentationLayer_Send_Then_Receive_Round_Trips(byte[] payload, byte n1Raw, byte pidRaw, bool quirkOn)
    {
        payload ??= [];
        // The application's L3 PID must not be 0x08 - that value is reserved for
        // the segment marker (§6.6), so a real upper layer never hands it down as
        // its protocol id. (Feeding 0x08 here would make the un-segmented
        // pass-through indistinguishable from a segment on the receive side - a
        // distinct hostile-input concern covered by the *_Never_Crash_Throws_*
        // properties, not the valid round-trip under test here.)
        byte pid = pidRaw == Ax25Frame.PidSegmented ? Ax25Frame.PidNoLayer3 : pidRaw;
        // N1 in a sane band; both sides share it.
        int n1 = 16 + (n1Raw % 240);   // 16..255
        // Cap payload so it needs more than one segment but stays within the
        // ceiling at this N1 (we want to actually exercise multi-segment).
        int maxPayload = Math.Min(payload.Length, (Segmenter.MaxSegments - 1) * (n1 - 1));
        var capped = payload[..maxPayload];

        var quirks = quirkOn ? Ax25SessionQuirks.Default : Ax25SessionQuirks.StrictlyFaithful;

        var sendCtx = NewSegmentingContext(n1, quirks);
        var recvCtx = NewSegmentingContext(n1, quirks);
        var sender = new SegmentationLayer(sendCtx);
        var receiver = new SegmentationLayer(recvCtx);

        var requests = sender.BuildSendRequests(capped, pid);

        DataLinkDataIndication? delivered = null;
        int deliveries = 0;
        foreach (var req in requests)
        {
            var ind = new DataLinkDataIndication(req.Data, req.Pid);
            var outp = receiver.OnDataIndication(ind);
            if (outp is not null) { delivered = outp; deliveries++; }
        }

        if (capped.Length <= n1)
        {
            // Fits - pass-through, single request with the original PID.
            requests.Should().ContainSingle();
            deliveries.Should().Be(1);
            delivered!.Info.ToArray().Should().Equal(capped);
            delivered.Pid.Should().Be(pid, "an un-segmented payload passes through with its original PID");
        }
        else
        {
            // Segmented - every request is a 0x08 segment; exactly one delivery
            // on the last segment, identical to the original payload.
            requests.Should().OnlyContain(r => r.Pid == Ax25Frame.PidSegmented);
            deliveries.Should().Be(1, "exactly one reassembled indication on the final segment");
            delivered!.Info.ToArray().Should().Equal(capped, "send → wire → receive is the identity");
            var expectedPid = quirkOn ? pid : SegmentationLayer.FigureLiteralReassembledPid;
            delivered.Pid.Should().Be(expectedPid,
                quirkOn
                    ? "the inner-PID quirk recovers the original L3 PID across the wire"
                    : "the figure-literal format delivers reassembled data as 0xF0");
        }
    }

    /// <summary>
    /// A hostile / arbitrary segment sequence must never drive the reassembler to
    /// a <i>crash-class</i> exception (IndexOutOfRange, NullReference, …). Its
    /// documented <see cref="System.InvalidOperationException"/> /
    /// <see cref="System.ArgumentException"/> throws (missing-first,
    /// out-of-sequence, empty/short field) are the contract and are tolerated
    /// here; anything else escapes and fails the property. See
    /// <c>FINDINGS.md</c> 2026-06-03 for why those throws reach the wire path.
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void Reassembler_Never_Crash_Throws_On_Hostile_Segments(byte[][] rawSegments, bool expectInnerPid)
    {
        rawSegments ??= [];
        var reassembler = new Reassembler(expectInnerPid);

        foreach (var seg in rawSegments)
        {
            var s = seg ?? [];
            try
            {
                _ = reassembler.Push(s);
            }
            catch (System.InvalidOperationException) { /* documented contract */ }
            catch (System.ArgumentException) { /* documented contract */ }
            // Any other exception type escapes → the property fails, which is
            // exactly the crash-class regression we want to catch.
        }
    }

    /// <summary>
    /// Same hostile-sequence crash-proofing, but through the on-the-wire
    /// <see cref="SegmentationLayer.OnDataIndication"/> seam - the path
    /// <see cref="Ax25Listener"/> actually drives. Each arbitrary buffer is
    /// delivered as a PID-0x08 indication; only the reassembler's documented
    /// throws are tolerated.
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void SegmentationLayer_Never_Crash_Throws_On_Hostile_Indications(byte[][] rawSegments, bool quirkOn)
    {
        rawSegments ??= [];
        var ctx = NewSegmentingContext(256, quirkOn ? Ax25SessionQuirks.Default : Ax25SessionQuirks.StrictlyFaithful);
        var layer = new SegmentationLayer(ctx);

        foreach (var seg in rawSegments)
        {
            var ind = new DataLinkDataIndication(seg ?? [], Ax25Frame.PidSegmented);
            try
            {
                _ = layer.OnDataIndication(ind);
            }
            catch (System.InvalidOperationException) { /* documented contract */ }
            catch (System.ArgumentException) { /* documented contract */ }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Choose a valid N1 for the format and cap the payload so it never exceeds
    /// the 128-segment ceiling (which is its own tested concern, not the subject
    /// of an identity round-trip). Figure-literal needs N1 ≥ 2; inner-PID N1 ≥ 3.
    /// </summary>
    private static (int N1, byte[] Payload) ClampPayloadToCeiling(byte[] payload, byte n1Raw, bool innerPid)
    {
        int minN1 = innerPid ? 3 : 2;
        int n1 = minN1 + (n1Raw % 64);                  // minN1 .. minN1+63
        int perSegment = n1 - 1;
        // Max payload at this N1 within MaxSegments. Inner-PID steals one slot.
        int maxPayload = innerPid
            ? Segmenter.MaxSegments * perSegment - 1
            : Segmenter.MaxSegments * perSegment;
        var capped = payload.Length > maxPayload ? payload[..maxPayload] : payload;
        return (n1, capped);
    }

    /// <summary>
    /// A session context configured for segmentation: the segmenter/reassembler
    /// is enabled (negotiated), N1 set, quirks chosen.
    /// </summary>
    private static Ax25SessionContext NewSegmentingContext(int n1, Ax25SessionQuirks quirks) => new()
    {
        Local = new Packet.Core.Callsign("M0LTE", 0),
        Remote = new Packet.Core.Callsign("G7XYZ", 7),
        N1 = n1,
        SegmenterReassemblerEnabled = true,
        Quirks = quirks,
    };
}
