namespace Packet.Rig;

/// <summary>Base for all rig-control failures, so callers can catch one type per rig.
/// Capability misses are <em>not</em> <see cref="RigException"/>s — feature-probe-then-call
/// violations throw <see cref="NotSupportedException"/> (see <see cref="IRigControl"/>).</summary>
public class RigException : Exception
{
    public RigException(string message) : base(message) { }

    public RigException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The control link is down or died mid-command: dial failure, socket fault, or the
/// backend closed on us. The rig itself may be fine — retry/reconnect is a reasonable caller
/// response, and backends re-dial automatically on the next command.</summary>
public class RigConnectionException : RigException
{
    public RigConnectionException(string message) : base(message) { }

    public RigConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>No reply within the command timeout. The connection is torn down when this is
/// thrown (a late reply would otherwise desynchronise the request/response stream), so the next
/// command re-dials.</summary>
public class RigTimeoutException : RigException
{
    public RigTimeoutException(string message) : base(message) { }
}

/// <summary>The backend answered and said no: hamlib <c>RPRT &lt;negative&gt;</c>, an XML-RPC
/// fault, or an equivalent explicit rejection.</summary>
public class RigCommandException : RigException
{
    public RigCommandException(string message, int backendErrorCode) : base(message)
        => BackendErrorCode = backendErrorCode;

    /// <summary>The backend's native error code — a hamlib error number (positive form of the
    /// <c>RPRT</c> value) or an XML-RPC fault code. Diagnostic only; meaning is per-backend.</summary>
    public int BackendErrorCode { get; }
}

/// <summary>The backend replied with something this library could not parse — protocol drift,
/// a proxy in the path, or a bug. Carries the offending payload for diagnosis.</summary>
public class RigProtocolException : RigException
{
    public RigProtocolException(string message) : base(message) { }
}
