namespace Packet.Node.Core.Auth;

/// <summary>
/// The three web control-API scopes, in increasing privilege, plus the
/// implication helper the authorization policies use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implication model: admin ⊃ operate ⊃ read.</b> A user is granted exactly
/// one scope (the highest they need); the implication is encoded in the
/// authorization check (<see cref="Satisfies"/>), <em>not</em> by stuffing all
/// three scopes into the token. So an <c>admin</c> token satisfies a
/// <c>read</c>-gated endpoint without carrying a <c>read</c> claim - keeping the
/// token small and the grant single-valued (one source of truth for "what is
/// this user").
/// </para>
/// <para>
/// The scope travels as a single <c>scope</c> claim on the JWT (its value is one
/// of <see cref="Read"/> / <see cref="Operate"/> / <see cref="Admin"/>). A token
/// with an unknown or absent scope satisfies nothing.
/// </para>
/// </remarks>
public static class AuthScopes
{
    /// <summary>Read-only access: status, ports/sessions/routes/links/log reads,
    /// config read, the SSE event feed.</summary>
    public const string Read = "read";

    /// <summary>Operate access (implies <see cref="Read"/>): config writes, port
    /// lifecycle, session connect/disconnect/send/stream, ping.</summary>
    public const string Operate = "operate";

    /// <summary>Administrative access (implies <see cref="Operate"/> and
    /// <see cref="Read"/>): user management.</summary>
    public const string Admin = "admin";

    /// <summary>The claim type carrying a user's granted scope on the JWT.</summary>
    public const string ScopeClaim = "scope";

    // Privilege rank - higher grants everything at-or-below. Anything not a known
    // scope ranks below read (rank 0), so it satisfies nothing.
    private static int Rank(string? scope) => scope switch
    {
        Admin => 3,
        Operate => 2,
        Read => 1,
        _ => 0,
    };

    /// <summary>Whether a user holding <paramref name="granted"/> may access an
    /// endpoint requiring <paramref name="required"/> - i.e. the granted scope
    /// ranks at or above the required one (admin ⊃ operate ⊃ read). An unknown /
    /// null granted scope satisfies nothing; an unknown required scope is never
    /// satisfied.</summary>
    public static bool Satisfies(string? granted, string required)
    {
        int needed = Rank(required);
        return needed > 0 && Rank(granted) >= needed;
    }

    /// <summary>Whether <paramref name="scope"/> is one of the three known scopes
    /// (used when validating a created/edited user).</summary>
    public static bool IsKnown(string? scope) =>
        scope is Read or Operate or Admin;
}
