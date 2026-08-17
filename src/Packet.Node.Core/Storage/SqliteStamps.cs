using System.Globalization;

namespace Packet.Node.Core.Storage;

/// <summary>
/// The one definition of how a <see cref="DateTimeOffset"/> is written to (and read back from) a
/// SQLite TEXT timestamp column. Every Dapper-backed store in this library stamps through here, so
/// the persisted form is identical in every table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always UTC.</b> <see cref="Stamp"/> normalises with <c>ToUniversalTime()</c> before
/// formatting, so a column only ever holds a <c>Z</c> stamp. That is the load-bearing part:
/// several stores range-compare these columns AS TEXT in SQL (the heard log's
/// <c>WHERE last_heard_utc &lt; @cutoff</c> and its <c>ORDER BY last_heard_utc DESC</c>, the
/// refresh-token <c>WHERE expires_utc &lt; @e</c> prune, the OAuth client list's
/// <c>ORDER BY created_utc DESC</c>). SQLite has no date type, so a value stamped with a non-zero
/// offset such as <c>+01:00</c> gets compared on its local wall-clock digits against a UTC cutoff
/// and therefore sorts and prunes wrongly. Normalising first makes the lexical order the
/// chronological order.
/// </para>
/// <para>
/// The round-trip ("o") format is fixed-width and invariant-culture, which is what makes that
/// lexical compare valid in the first place. <see cref="ParseStamp"/> returns the instant in UTC
/// whatever offset the stored text carries, so rows written by an older build (before this was
/// centralised) still read back as the right instant.
/// </para>
/// </remarks>
internal static class SqliteStamps
{
    /// <summary>Format <paramref name="value"/> for a SQLite TEXT timestamp column: normalised to
    /// UTC, invariant round-trip ("o") form.</summary>
    public static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Read a stamp written by <see cref="Stamp"/> back as a UTC
    /// <see cref="DateTimeOffset"/>. Throws <see cref="FormatException"/> on a malformed value,
    /// which is what the stores' read paths catch and degrade on.</summary>
    public static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
