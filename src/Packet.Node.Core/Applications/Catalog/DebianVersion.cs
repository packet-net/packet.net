using System.Globalization;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// Debian version ordering for app package versions: the algorithm behind
/// <c>dpkg --compare-versions</c> (Debian Policy 5.6.12), used by the "Available apps" view to
/// decide whether the catalog's version is an update over the installed one.
/// </summary>
/// <remarks>
/// <para>A version is <c>[epoch:]upstream[-revision]</c>. The epoch (default 0) is compared
/// numerically first, then the upstream version, then the revision (default empty), each with
/// dpkg's <c>verrevcmp</c>: the string is walked as alternating non-digit and digit runs; digit
/// runs compare numerically (so <c>0.2.10</c> is above <c>0.2.9</c>, and <c>1.0</c> equals
/// <c>1.00</c>); in non-digit runs letters sort before every other character, and <c>~</c> sorts
/// before everything, even the end of the string (so <c>1.0~rc1</c> is below <c>1.0</c>). A
/// suffix therefore counts: <c>6.0.25.41-pdn1</c> is below <c>6.0.25.41-pdn2</c>.</para>
/// <para>This is not the node's own self-update ordering (<c>NodeVersion</c>), which has its
/// own dev-build rule and is left alone.</para>
/// <para>Parsing is strict: a string dpkg would reject or warn about (empty, embedded
/// whitespace, a non-numeric epoch, an empty revision, an upstream version that does not start
/// with a digit, or a character outside the allowed set) is unreadable, and
/// <see cref="TryCompare"/> reports that rather than guessing. Callers treat unreadable as "no
/// update", which is the conservative answer.</para>
/// </remarks>
public static class DebianVersion
{
    /// <summary>Whether <paramref name="version"/> is a well-formed Debian version.</summary>
    public static bool IsValid(string? version) => TryParse(version, out _, out _, out _);

    /// <summary>
    /// Compare two Debian versions. Returns <c>false</c> (and <paramref name="result"/> 0) when
    /// either side is not a well-formed Debian version; otherwise <paramref name="result"/> is
    /// negative, zero or positive as <paramref name="a"/> sorts below, equal to or above
    /// <paramref name="b"/>. Never throws.
    /// </summary>
    public static bool TryCompare(string? a, string? b, out int result)
    {
        result = 0;
        if (!TryParse(a, out var epochA, out var upstreamA, out var revisionA)
            || !TryParse(b, out var epochB, out var upstreamB, out var revisionB))
        {
            return false;
        }

        result = epochA.CompareTo(epochB);
        if (result == 0)
        {
            result = Math.Sign(VerRevCmp(upstreamA, upstreamB));
        }
        if (result == 0)
        {
            result = Math.Sign(VerRevCmp(revisionA, revisionB));
        }
        return true;
    }

    /// <summary>Whether <paramref name="candidate"/> sorts strictly above
    /// <paramref name="current"/>. <c>false</c> when either side is unreadable.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryCompare(candidate, current, out var result) && result > 0;

    /// <summary>Split and validate, following dpkg's <c>parseversion</c> (its warnings are
    /// treated as errors here).</summary>
    private static bool TryParse(string? text, out int epoch, out string upstream, out string revision)
    {
        epoch = 0;
        upstream = "";
        revision = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        int colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var epochText = s[..colon];
            if (!int.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out epoch))
            {
                return false;
            }
            s = s[(colon + 1)..];
            if (s.Length == 0)
            {
                return false;
            }
        }

        int hyphen = s.LastIndexOf('-');
        if (hyphen >= 0)
        {
            revision = s[(hyphen + 1)..];
            s = s[..hyphen];
            if (revision.Length == 0 || !revision.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~'))
            {
                return false;
            }
        }

        upstream = s;
        return upstream.Length > 0
            && char.IsAsciiDigit(upstream[0])
            && upstream.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '~' or ':');
    }

    /// <summary>dpkg's <c>order()</c>: the weight of one character in a non-digit run. The end of
    /// the string (and a digit, which ends the run) weighs 0; <c>~</c> weighs less than that;
    /// letters weigh their code; every other character weighs more than any letter.</summary>
    private static int Order(char c) => c switch
    {
        '\0' => 0,
        _ when char.IsAsciiDigit(c) => 0,
        _ when char.IsAsciiLetter(c) => c,
        '~' => -1,
        _ => c + 256,
    };

    /// <summary>dpkg's <c>verrevcmp</c> over one upstream-version or revision string.</summary>
    private static int VerRevCmp(string a, string b)
    {
        int i = 0, j = 0;
        char At(string s, int k) => k < s.Length ? s[k] : '\0';

        while (i < a.Length || j < b.Length)
        {
            // Non-digit run: compare character by character by weight.
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                int ac = Order(At(a, i));
                int bc = Order(At(b, j));
                if (ac != bc)
                {
                    return ac - bc;
                }
                i++;
                j++;
            }

            // Digit run: numeric compare, ignoring leading zeros, without overflow.
            while (At(a, i) == '0')
            {
                i++;
            }
            while (At(b, j) == '0')
            {
                j++;
            }
            int firstDiff = 0;
            while (char.IsAsciiDigit(At(a, i)) && char.IsAsciiDigit(At(b, j)))
            {
                if (firstDiff == 0)
                {
                    firstDiff = At(a, i) - At(b, j);
                }
                i++;
                j++;
            }
            if (char.IsAsciiDigit(At(a, i)))
            {
                return 1;
            }
            if (char.IsAsciiDigit(At(b, j)))
            {
                return -1;
            }
            if (firstDiff != 0)
            {
                return firstDiff;
            }
        }
        return 0;
    }
}
