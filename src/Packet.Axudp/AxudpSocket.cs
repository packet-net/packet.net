using System.Net;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Core;

namespace Packet.Axudp;

/// <summary>
/// A bidirectional AXUDP endpoint. AXUDP is UDP encapsulation of AX.25 frames
/// per the RFC-1226 "AX.25 over IP" convention — the UDP payload is the AX.25
/// frame body (no opening / closing HDLC flag, no bit-stuffing) followed by the
/// 2-octet AX.25 FCS. Each peer maintains its own UDP socket and addresses
/// remotes by <see cref="IPEndPoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>AXUDP unconditionally carries the 2-octet AX.25 FCS — there is no FCS-less
/// form.</b> The FCS is part of the wire format: <see cref="SendAsync"/> always
/// appends it (CRC-16-CCITT / X.25, low byte first) and <see cref="ReceiveAsync"/>
/// always strips + validates it, dropping any datagram whose FCS doesn't check —
/// exactly as every real peer does.
/// </para>
/// <para>
/// This is settled by a citation survey of every real AXIP/AXUDP implementation
/// (see <c>docs/strict-vs-pragmatic-audit.md</c> § "AXUDP / AXIP-over-IP FCS framing"):
/// <list type="bullet">
///   <item><b>RFC 1226</b> (and the modern <c>rfc1226-bis</c> draft), the AX.25-over-IP
///   standard: "The 16-bit CRC-CCITT frame check sequence … is included" — mandatory,
///   with no FCS-less option.</item>
///   <item><b>ax25ipd</b> (the classic Linux AXIP daemon, <c>ax25-apps</c>): appends the
///   FCS unconditionally on transmit (<c>process.c</c> <c>add_crc</c>) and drops any
///   received datagram whose FCS residue isn't <c>0xf0b8</c> (<c>process.c</c>
///   <c>ok_crc</c>). No CRC config knob exists.</item>
///   <item><b>LinBPQ's BPQAXIP driver over UDP REQUIRES the FCS.</b> Source-verified
///   in <c>bpqaxip.c</c> (LinBPQ 6.0.25.23) and confirmed on the wire: its UDP receive
///   path computes the FCS over the whole datagram and drops anything whose residue
///   isn't <c>0xf0b8</c> ("BPQAXIP Invalid CRC"); its send path appends the 2-octet FCS.
///   There is no per-MAP "no CRC" knob.</item>
///   <item><b>XRouter's AXUDP likewise requires the FCS</b> (it counts FCS-less bodies
///   as "non-AXUDP ignored" — verified on the wire).</item>
/// </list>
/// All compute the identical CRC (poly 0x1021, init 0xffff, ^0xffff, low byte
/// first, good residue 0xf0b8) — byte-for-byte our <see cref="Packet.Core.Crc16Ccitt"/>.
/// A pdn-only FCS-less form once existed as a "pdn↔pdn opt-out"; it interoperated
/// with no real implementation and was removed.
/// </para>
/// <para>
/// <see cref="SendAsync"/> writes the body plus the FCS; <see cref="ReceiveAsync"/>
/// strips + validates the FCS and returns the bare AX.25 frame body. The
/// <see cref="SendRawAsync"/> escape hatch sends arbitrary bytes verbatim (no FCS
/// appended) for replaying captures.
/// </para>
/// </remarks>
public sealed class AxudpSocket : IDisposable
{
    private readonly UdpClient udp;

    /// <summary>The local UDP port we're bound to.</summary>
    public int LocalPort { get; }

    /// <summary>
    /// Open an AXUDP endpoint bound to <paramref name="localPort"/> (0 picks
    /// any free ephemeral port). The endpoint listens on all interfaces.
    /// </summary>
    public AxudpSocket(int localPort = 0)
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Send a serialised AX.25 frame to <paramref name="remote"/>, with the
    /// 2-octet AX.25 FCS (CRC-16-CCITT / X.25, low byte first) appended — the
    /// RFC-1226 AXIP/AXUDP wire form that every real peer (LinBPQ's BPQAXIP,
    /// XRouter, ax25ipd, JNOS) requires. The FCS is unconditional; there is no
    /// FCS-less form.
    /// </summary>
    /// <param name="remote">The remote endpoint to send to.</param>
    /// <param name="frame">Frame to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> SendAsync(IPEndPoint remote, Ax25Frame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = frame.ToBytesWithFcs();
        return await udp.SendAsync(bytes, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send raw bytes to <paramref name="remote"/>. Caller is responsible for
    /// constructing a well-formed AX.25 frame body — useful for replaying
    /// captures or forwarding unparsed frames.
    /// </summary>
    public async Task<int> SendRawAsync(IPEndPoint remote, ReadOnlyMemory<byte> rawFrame, CancellationToken cancellationToken = default)
    {
        return await udp.SendAsync(rawFrame, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the next valid datagram. The trailing 2-octet AX.25 FCS is
    /// stripped + validated; a datagram that is too short to carry an FCS, or
    /// whose FCS doesn't check, is dropped (and the wait continues) — exactly as
    /// every real AXIP/AXUDP peer drops a bad-CRC datagram. Returns the sender
    /// endpoint, the bare AX.25 frame body (FCS removed), and an attempted decode
    /// (null if the body isn't a parseable AX.25 frame).
    /// </summary>
    /// <remarks>
    /// The decode is best-effort at modulo-8: this transport layer has no
    /// session context, so it can't know whether the link is extended
    /// (modulo-128) — and an extended I/S frame's control-field width isn't
    /// derivable from the octets alone. The bare body is returned alongside,
    /// so a session-aware consumer must re-parse at the link's negotiated
    /// modulo (see <c>Ax25Frame.TryParse(…, extended, …)</c>) before trusting
    /// N(S)/N(R)/PID/info on an extended link. <see cref="AxudpReceiveResult.RawFrame"/>
    /// is the FCS-stripped frame body (what the AX.25 parser consumes);
    /// <see cref="AxudpReceiveResult.DecodedFrame"/> is suitable for
    /// monitor/identification (addresses + frame type, which are
    /// modulo-independent) without that second pass.
    /// </remarks>
    public async Task<AxudpReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!TryStripFcs(result.Buffer, out var body))
            {
                continue;   // too short, or FCS mismatch — drop, as real peers do
            }
            Ax25Frame? decoded = Ax25Frame.TryParse(body, out var frame) ? frame : null;
            return new AxudpReceiveResult(result.RemoteEndPoint, body, decoded);
        }
    }

    // Validate + strip the trailing 2-octet FCS (low byte first, matching
    // Ax25Frame.WriteToWithFcs). Returns false on a too-short datagram or an FCS
    // mismatch (a corrupt datagram to drop).
    private static bool TryStripFcs(byte[] datagram, out byte[] body)
    {
        body = datagram;
        if (datagram.Length < (2 * 7) + 1 + 2)   // min AX.25 frame (2 addresses + control) + 2-octet FCS
        {
            return false;
        }
        var bodySpan = datagram.AsSpan(0, datagram.Length - 2);
        ushort expected = Crc16Ccitt.Compute(bodySpan);
        ushort actual = (ushort)(datagram[^2] | (datagram[^1] << 8));
        if (expected != actual)
        {
            return false;
        }
        body = bodySpan.ToArray();
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => udp.Dispose();
}

/// <summary>
/// One received AXUDP datagram, after the trailing FCS has been stripped +
/// validated.
/// </summary>
/// <param name="From">The remote endpoint that sent the datagram.</param>
/// <param name="RawFrame">The AX.25 frame body — the datagram payload with the
/// 2-octet FCS removed (what the AX.25 parser consumes).</param>
/// <param name="DecodedFrame">
/// The parsed frame, or <c>null</c> if the body did not decode as a valid
/// AX.25 frame (callers may still want to inspect <see cref="RawFrame"/>).
/// </param>
public readonly record struct AxudpReceiveResult(IPEndPoint From, byte[] RawFrame, Ax25Frame? DecodedFrame);
