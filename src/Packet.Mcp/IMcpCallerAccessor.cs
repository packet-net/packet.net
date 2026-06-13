namespace Packet.Mcp;

/// <summary>
/// Supplies the <see cref="McpCaller"/> for the in-flight tool invocation, so a
/// write tool can attribute its audit entry. The host registers a per-transport
/// implementation: the stdio bridge yields <see cref="McpCaller.LocalStdio"/>;
/// the SSE transport yields the authenticated token subject. Defaults to
/// local-stdio so a bare in-process host (and tests) need no wiring.
/// </summary>
public interface IMcpCallerAccessor
{
    /// <summary>The caller for the current invocation.</summary>
    McpCaller Current { get; }
}

/// <summary>The default accessor — always the local stdio user.</summary>
public sealed class LocalStdioCallerAccessor : IMcpCallerAccessor
{
    /// <inheritdoc />
    public McpCaller Current => McpCaller.LocalStdio;
}
