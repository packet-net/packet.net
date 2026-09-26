namespace Packet.Aprs;

/// <summary>
/// An APRS display symbol: a symbol table identifier (or overlay) and a symbol code
/// (APRS12c §21, APRS-Symbols.pdf).
/// </summary>
/// <remarks>
/// <see cref="Table"/> is <c>/</c> for the primary table, <c>\</c> for the alternate table, or
/// an overlay character <c>0</c>-<c>9</c> / <c>A</c>-<c>Z</c>, which selects the alternate table
/// with that character drawn on top. Compressed positions write numeric overlays as
/// <c>a</c>-<c>j</c> because a compressed report may not start with a digit; they are decoded to
/// the digit here and re-encoded as the letter, so <see cref="Table"/> is always the
/// uncompressed form.
/// </remarks>
/// <param name="Table">Symbol table identifier or overlay.</param>
/// <param name="Code">Symbol code, printable ASCII <c>!</c> to <c>~</c>.</param>
public readonly record struct AprsSymbol(char Table, char Code)
{
    /// <summary>True for the primary symbol table (<c>/</c>).</summary>
    public bool IsPrimaryTable => Table == '/';

    /// <summary>True for the alternate table, with or without an overlay.</summary>
    public bool IsAlternateTable => Table != '/';

    /// <summary>The overlay character, or null for the plain primary or alternate table.</summary>
    public char? Overlay => Table is '/' or '\\' ? null : Table;

    /// <summary>True for the weather-station symbols <c>/_</c> and <c>\_</c> (with or without overlay),
    /// which make a position report a complete weather report (APRS12c §12).</summary>
    public bool IsWeatherStation => Code == '_';

    /// <summary>A short English description from the APRS symbol tables, or null if the symbol is
    /// not defined.</summary>
    public string? Description => AprsSymbolTable.Describe(this);

    /// <summary>True when <see cref="Table"/> and <see cref="Code"/> are characters that may appear
    /// in a symbol on air.</summary>
    public bool IsValid => IsValidTable(Table) && Code is >= '!' and <= '~';

    /// <summary>Parses a two-character symbol such as <c>/&gt;</c> or <c>\n</c> or <c>3#</c>.</summary>
    public static AprsSymbol Parse(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Length != 2)
        {
            throw new FormatException("a symbol is exactly two characters: table (or overlay) then code");
        }

        var result = new AprsSymbol(symbol[0], symbol[1]);
        return result.IsValid ? result : throw new FormatException($"'{symbol}' is not a valid APRS symbol");
    }

    /// <summary>Returns the table (or overlay) character followed by the code.</summary>
    public override string ToString() => $"{Table}{Code}";

    internal static bool IsValidTable(char table) => table is '/' or '\\' or (>= '0' and <= '9') or (>= 'A' and <= 'Z');

    internal void Validate(string paramName)
    {
        if (!IsValid)
        {
            throw new ArgumentException($"'{this}' is not a valid APRS symbol (table / \\ 0-9 A-Z, code ! to ~)", paramName);
        }
    }
}
