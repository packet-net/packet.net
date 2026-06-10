namespace Packet.Node.Core.Auth;

/// <summary>
/// One web control-API user as persisted in <c>pdn.db</c>. The
/// <see cref="PasswordHash"/> is the full self-describing Argon2id encoded hash
/// (algorithm, parameters, salt and digest in one string — see
/// <see cref="PasswordHasher"/>); it is never returned to a client.
/// </summary>
/// <param name="Username">Unique, case-sensitive login name.</param>
/// <param name="PasswordHash">The full encoded Argon2id hash (params + salt + digest).</param>
/// <param name="Scope">The granted scope: one of <see cref="AuthScopes.Read"/> /
/// <see cref="AuthScopes.Operate"/> / <see cref="AuthScopes.Admin"/>.</param>
/// <param name="CreatedUtc">When the user was created.</param>
/// <param name="LastLoginUtc">When the user last successfully logged in, or null.</param>
/// <param name="Callsign">The amateur-radio callsign this user proves over RF (the
/// over-the-air sysop identity), or null if no TOTP credential is enrolled. Unique
/// across users (a callsign maps to at most one account — the over-RF gate looks a
/// user up by it). Set together with <see cref="TotpSecret"/> at enrolment.</param>
/// <param name="TotpSecret">The user's base32 TOTP shared secret, or null if no
/// over-RF credential is enrolled. <b>Stored in plaintext</b> in the 0640
/// packetnet-owned <c>pdn.db</c> — the same accepted decision as the JWT signing key:
/// the db is the trust boundary (file perms + the OS user own it), and a secret the
/// node must use to <em>verify</em> a presented code (not just compare a hash) cannot
/// be one-way hashed. Never logged, never returned to a client (see
/// <see cref="UserSummary"/>, which exposes only <see cref="UserSummary.HasTotp"/>).</param>
/// <param name="LastTotpCounter">The highest TOTP time-step counter already accepted
/// for this user (the single-use replay high-water mark <see cref="TotpService"/>
/// persists), or null if none has been accepted yet (e.g. just after enrolment, before
/// the first over-RF use).</param>
public sealed record UserRecord(
    string Username,
    string PasswordHash,
    string Scope,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,
    string? Callsign = null,
    string? TotpSecret = null,
    long? LastTotpCounter = null);

/// <summary>
/// A user projected for the API — the hash is deliberately absent, and so is the
/// TOTP secret. This is the only shape <c>/users</c> returns;
/// <see cref="UserRecord.PasswordHash"/> and <see cref="UserRecord.TotpSecret"/> never
/// leave the store. It MAY expose whether a TOTP credential is enrolled
/// (<see cref="HasTotp"/>) and the bound <see cref="Callsign"/> so the UI can show the
/// over-RF enrolment state — neither is secret.
/// </summary>
public sealed record UserSummary(
    string Username,
    string Scope,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,
    bool HasTotp = false,
    string? Callsign = null)
{
    /// <summary>Project a <see cref="UserRecord"/> to its hash-free, secret-free
    /// summary. <see cref="HasTotp"/> is derived from the presence of a stored secret;
    /// the secret itself is never copied across.</summary>
    public static UserSummary From(UserRecord user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserSummary(
            user.Username,
            user.Scope,
            user.CreatedUtc,
            user.LastLoginUtc,
            HasTotp: !string.IsNullOrEmpty(user.TotpSecret),
            Callsign: user.Callsign);
    }
}
