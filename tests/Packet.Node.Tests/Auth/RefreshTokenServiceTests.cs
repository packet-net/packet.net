using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The refresh-token rotation core: issue → rotate gives a new usable token and
/// revokes the old; a rotated (old) token is rejected; reuse of a revoked token
/// revokes the WHOLE family (a sibling then rejected); expired tokens are rejected;
/// logout revokes the family; a store fault degrades (never throws). All on
/// <see cref="FakeTimeProvider"/> — no wall-clock.
/// </summary>
[Trait("Category", "Node")]
public sealed class RefreshTokenServiceTests
{
    private static (RefreshTokenService Svc, InMemoryRefreshStore Store, FakeTimeProvider Clock) Make(
        TimeSpan? lifetime = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRefreshStore();
        var svc = new RefreshTokenService(store, lifetime ?? TimeSpan.FromDays(7), clock);
        return (svc, store, clock);
    }

    [Fact]
    public void Issue_returns_an_opaque_token_stored_only_as_a_hash()
    {
        var (svc, store, _) = Make();

        var token = svc.Issue("m0lte");
        token.Should().NotBeNullOrEmpty();

        // The store holds the HASH, never the plaintext.
        store.Rows.Should().ContainSingle();
        var row = store.Rows.Single();
        row.TokenHash.Should().NotBe(token);
        row.TokenHash.Should().Be(RefreshTokenService.HashToken(token!));
        row.Username.Should().Be("m0lte");
        row.Revoked.Should().BeFalse();
    }

    [Fact]
    public void Rotate_mints_a_new_usable_token_and_revokes_the_old()
    {
        var (svc, store, _) = Make();
        var first = svc.Issue("m0lte")!;
        var family = store.Rows.Single().Family;

        var result = svc.Rotate(first);
        result.IsSuccess.Should().BeTrue();
        result.Outcome.Should().Be(RefreshOutcome.Rotated);
        result.NewToken.Should().NotBeNullOrEmpty();
        result.NewToken.Should().NotBe(first);
        result.Username.Should().Be("m0lte");

        // The successor is in the SAME family; the old token is now revoked.
        store.Rows.Should().HaveCount(2);
        store.FindByHash(RefreshTokenService.HashToken(first))!.Revoked.Should().BeTrue();
        var next = store.FindByHash(RefreshTokenService.HashToken(result.NewToken!))!;
        next.Revoked.Should().BeFalse();
        next.Family.Should().Be(family);

        // The new token itself rotates again (it is usable).
        svc.Rotate(result.NewToken!).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_rotated_old_token_replayed_after_the_leeway_is_reuse_and_burns_the_family()
    {
        var (svc, store, clock) = Make();
        var first = svc.Issue("m0lte")!;
        var rotated = svc.Rotate(first);          // first is now revoked (consumed)
        var second = rotated.NewToken!;

        // After the reuse-leeway window, replaying the OLD (already-consumed) token is
        // theft, not a self-race → revoke the whole family.
        clock.Advance(TimeSpan.FromMinutes(1));
        var replay = svc.Rotate(first);
        replay.IsSuccess.Should().BeFalse();
        replay.Outcome.Should().Be(RefreshOutcome.ReuseDetected);

        // The legitimate-looking successor is now also revoked (family burned).
        store.FindByHash(RefreshTokenService.HashToken(second))!.Revoked.Should().BeTrue();
        svc.Rotate(second).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Reuse_after_the_leeway_revokes_a_sibling_in_the_family()
    {
        // Two logins are two DIFFERENT families; reuse in one must not touch the other.
        var (svc, _, clock) = Make();
        var famA1 = svc.Issue("alice")!;          // family A
        var famB1 = svc.Issue("alice")!;          // family B (separate login)

        // Rotate A once, then (after the leeway) replay the consumed A token → A burned.
        var famA2 = svc.Rotate(famA1).NewToken!;
        clock.Advance(TimeSpan.FromMinutes(1));
        svc.Rotate(famA1).Outcome.Should().Be(RefreshOutcome.ReuseDetected);

        // The A successor is dead; family B is untouched + still usable.
        svc.Rotate(famA2).IsSuccess.Should().BeFalse();
        svc.Rotate(famB1).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_concurrent_replay_within_the_leeway_is_benign_and_keeps_the_session()
    {
        // The real-world symptom: the legitimate client races itself (two tabs / a
        // retried silent refresh) and presents the SAME just-rotated token twice within
        // a moment. This must NOT burn the family — it mints another successor and the
        // session survives. (Before the leeway, this logged the user out on every
        // access-token expiry — see packet-net/packet.net auth audit, REUSE-DETECTED pairs.)
        var (svc, store, clock) = Make();
        var first = svc.Issue("tom")!;
        var rot1 = svc.Rotate(first);             // first consumed, second minted
        rot1.IsSuccess.Should().BeTrue();
        var second = rot1.NewToken!;

        clock.Advance(TimeSpan.FromSeconds(2));   // still inside the 10s default leeway
        var rot2 = svc.Rotate(first);
        rot2.IsSuccess.Should().BeTrue();
        rot2.Outcome.Should().Be(RefreshOutcome.Rotated);
        rot2.NewToken.Should().NotBe(second);

        // The first successor is untouched and still usable — nothing was burned.
        store.FindByHash(RefreshTokenService.HashToken(second))!.Revoked.Should().BeFalse();
        svc.Rotate(second).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void The_leeway_grace_cannot_resurrect_a_logged_out_family()
    {
        var (svc, store, clock) = Make();
        var first = svc.Issue("tom")!;
        var second = svc.Rotate(first).NewToken!; // first consumed within the leeway window
        var family = store.Rows.First().Family;

        svc.Logout(second);                       // whole family hard-revoked

        // A racing in-flight request replays the just-consumed `first` while still
        // inside the leeway window. The family is dead, so grace must NOT revive it.
        clock.Advance(TimeSpan.FromSeconds(1));
        var replay = svc.Rotate(first);
        replay.IsSuccess.Should().BeFalse();
        replay.Outcome.Should().Be(RefreshOutcome.ReuseDetected);

        // No successor was minted; the family stays fully revoked.
        store.Rows.Where(r => r.Family == family).Should().OnlyContain(r => r.Revoked);
        store.HasLiveToken(family).Should().BeFalse();
    }

    [Fact]
    public void Zero_leeway_restores_strict_zero_tolerance_reuse_detection()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryRefreshStore();
        var svc = new RefreshTokenService(store, TimeSpan.FromDays(7), clock, TimeSpan.Zero);

        var first = svc.Issue("m0lte")!;
        var second = svc.Rotate(first).NewToken!;

        // With the leeway off, even an immediate replay is theft → family burned.
        svc.Rotate(first).Outcome.Should().Be(RefreshOutcome.ReuseDetected);
        svc.Rotate(second).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void A_negative_leeway_is_rejected()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryRefreshStore();
        var act = () => new RefreshTokenService(store, TimeSpan.FromDays(7), clock, TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void An_expired_token_is_rejected()
    {
        var (svc, _, clock) = Make(TimeSpan.FromMinutes(10));
        var token = svc.Issue("m0lte")!;

        // Still valid inside the window.
        clock.Advance(TimeSpan.FromMinutes(9));
        // (don't consume it — just check it would rotate, then re-issue for the expiry test)

        var (svc2, _, clock2) = Make(TimeSpan.FromMinutes(10));
        var token2 = svc2.Issue("m0lte")!;
        clock2.Advance(TimeSpan.FromMinutes(11));    // past expiry
        var result = svc2.Rotate(token2);
        result.IsSuccess.Should().BeFalse();
        result.Outcome.Should().Be(RefreshOutcome.Expired);

        // And the in-window token from the first svc still rotates.
        svc.Rotate(token).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_token_is_invalid_not_reuse()
    {
        var (svc, _, _) = Make();
        var result = svc.Rotate("not-a-real-token");
        result.IsSuccess.Should().BeFalse();
        result.Outcome.Should().Be(RefreshOutcome.Invalid);
    }

    [Fact]
    public void Logout_revokes_the_whole_family()
    {
        var (svc, store, _) = Make();
        var first = svc.Issue("m0lte")!;
        var second = svc.Rotate(first).NewToken!;   // family now has two rows

        var (user, family) = svc.Logout(second);
        user.Should().Be("m0lte");
        family.Should().NotBeNullOrEmpty();

        // Every token in the family is revoked → none rotates.
        store.Rows.Where(r => r.Family == family).Should().OnlyContain(r => r.Revoked);
        svc.Rotate(second).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Logout_of_an_unknown_token_is_a_safe_noop()
    {
        var (svc, _, _) = Make();
        var (user, family) = svc.Logout("nope");
        user.Should().BeNull();
        family.Should().BeNull();
    }

    [Fact]
    public void A_store_fault_degrades_and_never_throws()
    {
        var clock = new FakeTimeProvider();
        var faulting = new FaultingRefreshStore();
        var svc = new RefreshTokenService(faulting, TimeSpan.FromDays(7), clock);

        // Issue can't persist → null (login still succeeds with just the access token).
        svc.Issue("m0lte").Should().BeNull();

        // Rotate over a faulting store → Invalid, not an exception.
        var rotate = svc.Rotate("anything");
        rotate.IsSuccess.Should().BeFalse();

        // Logout + prune swallow faults.
        var act = () => { svc.Logout("anything"); svc.PruneExpired(); };
        act.Should().NotThrow();
    }

    [Fact]
    public void HashToken_is_deterministic_and_not_the_plaintext()
    {
        var h1 = RefreshTokenService.HashToken("abc");
        var h2 = RefreshTokenService.HashToken("abc");
        h1.Should().Be(h2);
        h1.Should().NotBe("abc");
        // base64url: no +, /, or = padding.
        h1.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    // --- Test doubles ---------------------------------------------------------

    // A trivial in-memory IRefreshTokenStore (the same semantics as the SQLite one).
    private sealed class InMemoryRefreshStore : IRefreshTokenStore
    {
        private readonly Dictionary<string, RefreshTokenRecord> rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<RefreshTokenRecord> Rows => rows.Values;

        public bool Insert(RefreshTokenRecord token)
        {
            rows[token.TokenHash] = token;
            return true;
        }

        public RefreshTokenRecord? FindByHash(string tokenHash) =>
            rows.TryGetValue(tokenHash, out var r) ? r : null;

        public bool Revoke(string tokenHash, DateTimeOffset? consumedAtUtc)
        {
            if (!rows.TryGetValue(tokenHash, out var r))
            {
                return false;
            }
            rows[tokenHash] = r with { Revoked = true, RevokedUtc = consumedAtUtc };
            return true;
        }

        public int RevokeFamily(string family)
        {
            int n = 0;
            foreach (var key in rows.Keys.ToList())
            {
                var r = rows[key];
                if (r.Family == family && !r.Revoked)
                {
                    rows[key] = r with { Revoked = true };   // hard kill: no leeway stamp
                    n++;
                }
            }
            return n;
        }

        public bool HasLiveToken(string family) =>
            rows.Values.Any(r => r.Family == family && !r.Revoked);

        public int PruneExpired(DateTimeOffset olderThanUtc)
        {
            int n = 0;
            foreach (var key in rows.Keys.ToList())
            {
                if (rows[key].ExpiresUtc < olderThanUtc)
                {
                    rows.Remove(key);
                    n++;
                }
            }
            return n;
        }
    }

    // A store that fails every operation the way a broken SQLite store degrades.
    private sealed class FaultingRefreshStore : IRefreshTokenStore
    {
        public bool Insert(RefreshTokenRecord token) => false;
        public RefreshTokenRecord? FindByHash(string tokenHash) => null;
        public bool Revoke(string tokenHash, DateTimeOffset? consumedAtUtc) => false;
        public int RevokeFamily(string family) => 0;
        public bool HasLiveToken(string family) => false;
        public int PruneExpired(DateTimeOffset olderThanUtc) => 0;
    }
}
