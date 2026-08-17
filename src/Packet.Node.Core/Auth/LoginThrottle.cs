using System.Collections.Concurrent;

namespace Packet.Node.Core.Auth;

/// <summary>
/// An in-memory sliding-window failure counter that rate-limits login attempts —
/// the login-hardening component. Tracked independently per key (the host registers
/// a failure under BOTH the username AND the source IP, so neither a single
/// hammered account nor a single hostile IP can keep guessing): once a key
/// accumulates <see cref="MaxFailures"/> failures within <see cref="Window"/>, it is
/// locked out for the remainder of the window — further attempts under that key are
/// refused (the host returns 429) without even reaching the password verify.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sliding window, no wall-clock (repo rule §2.7).</b> Each key keeps the
/// timestamps of its recent failures (from the injected <see cref="TimeProvider"/>);
/// a failure or a lockout check first drops the timestamps older than
/// <see cref="Window"/>, so the count is always "failures in the last window". When
/// the count reaches the threshold the key is locked until its oldest in-window
/// failure ages out — a self-healing cooldown, no separate timer.
/// </para>
/// <para>
/// <b>Success resets.</b> A successful login clears the key's failure history
/// outright, so a legitimate user who fat-fingers a couple of times and then gets it
/// right is not penalised.
/// </para>
/// <para>
/// <b>Bounded memory.</b> Keys whose entire history has aged out are pruned
/// opportunistically on each touch, and a global sweep runs when the map grows past
/// a soft cap — so a flood of distinct usernames/IPs cannot leak memory.
/// </para>
/// <para>
/// Thread-safe (a <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-key entries
/// each guarded by its own lock) so it can be a singleton shared across request
/// threads.
/// </para>
/// </remarks>
public sealed class LoginThrottle
{
    /// <summary>Default failures within the window before a key locks out.</summary>
    public const int DefaultMaxFailures = 5;

    // Soft cap: once the key map exceeds this, a touch sweeps fully-aged-out keys.
    private const int PruneThreshold = 4096;

    private readonly TimeProvider clock;
    private readonly int maxFailures;
    private readonly TimeSpan window;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>The number of failures within <see cref="Window"/> that triggers a lockout.</summary>
    public int MaxFailures => maxFailures;

    /// <summary>The sliding window over which failures are counted (and the max cooldown).</summary>
    public TimeSpan Window => window;

    /// <summary>
    /// Construct with the clock and (optionally) the threshold + window.
    /// </summary>
    /// <param name="clock">The injected clock — all timing rides this (no wall-clock).</param>
    /// <param name="maxFailures">Failures within the window before lockout (default
    /// <see cref="DefaultMaxFailures"/> = 5).</param>
    /// <param name="window">The sliding window / max cooldown (default 5 minutes).</param>
    public LoginThrottle(TimeProvider clock, int? maxFailures = null, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        this.maxFailures = maxFailures is { } m && m > 0 ? m : DefaultMaxFailures;
        this.window = window is { } w && w > TimeSpan.Zero ? w : TimeSpan.FromMinutes(5);
    }

    /// <summary>The canonical throttle key for a USERNAME on any credential-guessing
    /// endpoint. Every surface that verifies a password (/auth/login, the OAuth
    /// consent POST) must record failures under this SAME key namespace - separate
    /// namespaces would let an attacker split guesses across endpoints and double
    /// the per-window budget.</summary>
    public static string UserKey(string username) => "user:" + username;

    /// <summary>The canonical throttle key for a SOURCE IP on any credential-guessing
    /// endpoint. Same shared-namespace rule as <see cref="UserKey"/>: one budget per
    /// IP across every password-verifying surface.</summary>
    public static string IpKey(string ip) => "ip:" + ip;

    /// <summary>
    /// Whether <paramref name="key"/> is currently locked out (≥ <see cref="MaxFailures"/>
    /// failures still inside the window). Pure-ish: it ages out stale failures as a
    /// side effect, so a key whose window has passed reads as unlocked.
    /// </summary>
    public bool IsLocked(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!entries.TryGetValue(key, out var entry))
        {
            return false;
        }
        var now = clock.GetUtcNow();
        lock (entry.Gate)
        {
            entry.Trim(now - window);
            return entry.Count >= maxFailures;
        }
    }

    /// <summary>
    /// Record a failed attempt under <paramref name="key"/> and report whether that
    /// failure pushed the key into (or kept it in) a locked state.
    /// </summary>
    public bool RecordFailure(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = clock.GetUtcNow();
        MaybeSweep(now);
        var entry = entries.GetOrAdd(key, static _ => new Entry());
        lock (entry.Gate)
        {
            entry.Trim(now - window);
            entry.Add(now);
            return entry.Count >= maxFailures;
        }
    }

    /// <summary>Clear a key's failure history — call on a successful login so a good
    /// login resets the counter for that username (and its source IP).</summary>
    public void Reset(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        entries.TryRemove(key, out _);
    }

    // When the map grows past the soft cap, drop keys whose entire failure history has
    // aged out of the window — bounding memory under a flood of distinct keys.
    private void MaybeSweep(DateTimeOffset now)
    {
        if (entries.Count <= PruneThreshold)
        {
            return;
        }
        var cutoff = now - window;
        foreach (var (key, entry) in entries)
        {
            bool empty;
            lock (entry.Gate)
            {
                entry.Trim(cutoff);
                empty = entry.Count == 0;
            }
            if (empty)
            {
                // Remove only if still empty (a concurrent failure may have re-added).
                entries.TryRemove(KeyValuePair.Create(key, entry));
            }
        }
    }

    // One key's recent-failure timestamps. A small ring-free list is plenty (the count
    // is bounded by maxFailures + a few stragglers before the next Trim).
    private sealed class Entry
    {
        public readonly object Gate = new();
        private readonly List<DateTimeOffset> failures = [];

        public int Count => failures.Count;

        public void Add(DateTimeOffset when) => failures.Add(when);

        // Drop every timestamp at/before the cutoff (i.e. older than one window).
        public void Trim(DateTimeOffset cutoff)
        {
            int i = 0;
            while (i < failures.Count && failures[i] <= cutoff)
            {
                i++;
            }
            if (i > 0)
            {
                failures.RemoveRange(0, i);
            }
        }
    }
}
