using System.Globalization;

namespace Packet.Node.Tests.Support;

/// <summary>
/// The one place the node tests get a temporary directory or file path.
/// </summary>
/// <remarks>
/// <para>
/// Every path handed out lives under a root that is unique per user AND per test run:
/// <c>{TMPDIR}/pdn-tests-{user}/{pid}-{short-guid}/...</c>. Both halves of that are load-bearing.
/// </para>
/// <list type="bullet">
/// <item><b>Per user (#628).</b> Fixtures used to be built under fixed shared parents such as
/// <c>/tmp/pdn-apps-tests/…</c> and <c>/tmp/pdn-tsnet-tests/…</c>. The first user to run the
/// suite created that parent; <c>/tmp</c> is sticky and the parent is not group-writable, so
/// every other user on the box then got <c>UnauthorizedAccessException: Permission denied</c>
/// and 61 tests failed for anyone but the original owner.</item>
/// <item><b>Per run.</b> Several worktrees of this repo are commonly under test on one box at
/// the same time (parallel agents, or CI legs beside a local run). A shared parent, or a
/// fixture that cleans up by deleting the shared parent, lets one run delete another run's
/// state mid-test: the symptoms were random HTTP 500s and vstest host crashes.</item>
/// </list>
/// <para>
/// The root is deleted on process exit, and roots left behind by crashed runs of the same user
/// are reaped after a day, so a box that runs the suite for years does not silently fill
/// <c>/tmp</c>.
/// </para>
/// </remarks>
public static class TestPaths
{
    private static readonly string Root = CreateRoot();
    private static int counter;

    /// <summary>This run's temporary root. Created on first use, deleted on process exit.</summary>
    public static string RunRoot => Root;

    /// <summary>
    /// Create and return a fresh empty directory for one test's fixtures.
    /// </summary>
    /// <param name="label">Short human hint so a leftover directory is identifiable (e.g. "apps").</param>
    public static string NewDirectory(string label = "t")
    {
        var path = Path.Combine(Root, Unique(label));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Reserve a fresh path under this run's root without creating anything at it. The parent
    /// directory exists, so a caller is free to create a file, a directory or nothing at all.
    /// </summary>
    /// <param name="label">Short human hint (e.g. "audit").</param>
    /// <param name="extension">File extension including the dot (e.g. ".db"), or empty.</param>
    public static string NewPath(string label = "t", string extension = "")
    {
        return Path.Combine(Root, Unique(label) + extension);
    }

    private static string Unique(string label)
    {
        var n = Interlocked.Increment(ref counter);
        return string.Create(CultureInfo.InvariantCulture, $"{Sanitise(label)}-{n:D3}");
    }

    private static string Sanitise(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "t";
        }

        var cleaned = new string([.. label.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return cleaned.Length == 0 ? "t" : cleaned;
    }

    private static string CreateRoot()
    {
        // Per-user parent: a stale root owned by another user on a sticky /tmp must never be
        // in the way (#628). Per-run leaf: concurrent runs must not share, or clean up, each
        // other's fixtures.
        var parent = Path.Combine(Path.GetTempPath(), "pdn-tests-" + Sanitise(Environment.UserName));
        // Short leaf on purpose: these paths carry Unix domain sockets, and sun_path is capped
        // at 108 bytes. Process id plus 8 hex of a guid is unique enough for one box.
        var root = Path.Combine(
            parent,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Environment.ProcessId:x}-{Guid.NewGuid().ToString("N")[..8]}"));
        Directory.CreateDirectory(root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(root);
        ReapAbandonedRoots(parent, root);
        return root;
    }

    private static void ReapAbandonedRoots(string parent, string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(dir, keep, StringComparison.Ordinal))
                {
                    continue;
                }

                // A day is far longer than any run of this suite, so anything older belongs to
                // a run that died without its exit hook.
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow - TimeSpan.FromDays(1))
                {
                    Delete(dir);
                }
            }
        }
        catch (Exception)
        {
            // Reaping is housekeeping: a racing sibling run, or a directory we cannot read,
            // must never fail a test.
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort: a file still held open by a not-quite-stopped background task must
            // not fail the run. The reaper above collects whatever is left next time.
        }
    }
}
