using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// A parsed node-console command — a closed, typed set. The parser
/// (<see cref="NodeCommandParser"/>) maps a raw input line to exactly one of
/// these and <b>never throws</b>; a line it cannot make sense of becomes
/// <see cref="UnknownCommand"/>, and a recognised verb with a bad argument
/// becomes a typed error variant (e.g. <see cref="MalformedConnect"/>) — never
/// an exception. This is the fuzz target's contract.
/// </summary>
public abstract record NodeCommand
{
    private protected NodeCommand() { }
}

/// <summary>
/// <c>C[onnect] &lt;call&gt;</c> — connect outbound to the given station and
/// relay until either side drops.
/// </summary>
public sealed record ConnectCommand(Callsign Target) : NodeCommand;

/// <summary><c>N[odes]</c> — list the node identity + configured ports.</summary>
public sealed record NodesCommand : NodeCommand;

/// <summary><c>I[nfo]</c> — node identity + version banner.</summary>
public sealed record InfoCommand : NodeCommand;

/// <summary><c>B[ye]</c> / <c>D[isconnect]</c> — tear the console connection down.</summary>
public sealed record ByeCommand : NodeCommand;

/// <summary><c>H[elp]</c> / <c>?</c> — the command list.</summary>
public sealed record HelpCommand : NodeCommand;

/// <summary>
/// An empty line — the user pressed enter with nothing typed. The console
/// re-prompts without comment (it is not an error).
/// </summary>
public sealed record EmptyCommand : NodeCommand;

/// <summary>
/// A recognised verb the parser could not complete — e.g. <c>CONNECT</c> with a
/// missing or unparseable callsign. Carries the offending input and a reason so
/// the console can reply helpfully. NOT an exception.
/// </summary>
public sealed record MalformedConnect(string Raw, string Reason) : NodeCommand;

/// <summary>An input line that matched no known verb.</summary>
public sealed record UnknownCommand(string Raw) : NodeCommand;
