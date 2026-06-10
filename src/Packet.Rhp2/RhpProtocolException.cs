namespace Packet.Rhp2;

/// <summary>
/// Thrown when a peer violates the RHPv2 protocol at the codec level —
/// e.g. a frame whose JSON is not an object, or a message with no
/// <c>type</c> discriminator.
/// </summary>
/// <remarks>
/// Deliberately NOT thrown for an unrecognised <c>type</c> value: unknown
/// message types map to <see cref="UnknownMessage"/> instead, so a newer
/// XRouter can introduce messages without breaking older clients.
/// </remarks>
public class RhpProtocolException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public RhpProtocolException()
    {
    }

    /// <summary>Creates the exception with a message describing the violation.</summary>
    public RhpProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an inner cause.</summary>
    public RhpProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
