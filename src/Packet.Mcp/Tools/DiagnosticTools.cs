using System.ComponentModel;
using ModelContextProtocol.Server;
using Packet.Mcp.Decoding;

namespace Packet.Mcp.Tools;

/// <summary>
/// MCP tools that need no node state. <c>decode_frame</c> is pure (parser
/// libraries only), so it works over stdio against a node that isn't even
/// running — and is the natural test/wiring anchor for the whole surface.
/// </summary>
[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool(Name = "decode_frame")]
    [Description("Decode an AX.25 frame from hex into a human-readable breakdown " +
        "(addresses, path, frame type, control, PID, info). Accepts a bare AX.25 " +
        "frame (KISS form) or a full KISS frame; auto-detects which.")]
    public static DecodedFrame DecodeFrame(
        [Description("Frame bytes as hex. Whitespace, 0x, ':' and ',' separators are fine.")]
        string hex,
        [Description("Framing: Auto (default), Raw (bare AX.25), or Kiss (full KISS frame).")]
        FrameDecoder.Framing framing = FrameDecoder.Framing.Auto,
        [Description("Decode I/S frames as modulo-128 (2-octet control). The width isn't derivable from the bytes alone.")]
        bool extended = false)
        => FrameDecoder.Decode(hex, framing, extended);
}
