using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait;

/// <summary>Thrown when the radio answers a CCDI transaction with an ERROR message.</summary>
public sealed class TaitCcdiException : Exception
{
    /// <summary>Create from the radio's ERROR response to <paramref name="command"/>.</summary>
    public TaitCcdiException(string command, CcdiErrorMessage error)
        : base($"CCDI command '{command}' failed: {error.Describe()}")
    {
        Command = command;
        Error = error;
    }

    /// <summary>The encoded command (without CR) the radio rejected.</summary>
    public string Command { get; }

    /// <summary>The radio's ERROR message.</summary>
    public CcdiErrorMessage Error { get; }
}

/// <summary>Thrown when the radio answers a CCR-mode command with a negative acknowledgement.</summary>
public sealed class TaitCcrNakException : Exception
{
    /// <summary>Create from the radio's NAK response to <paramref name="command"/>.</summary>
    public TaitCcrNakException(string command, CcrNakMessage nak)
        : base($"CCR command '{command}' rejected: {nak.Describe()}")
    {
        Command = command;
        Nak = nak;
    }

    /// <summary>The encoded command (without CR) the radio rejected.</summary>
    public string Command { get; }

    /// <summary>The radio's negative acknowledgement.</summary>
    public CcrNakMessage Nak { get; }
}
