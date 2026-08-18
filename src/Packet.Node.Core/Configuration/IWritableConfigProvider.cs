namespace Packet.Node.Core.Configuration;

/// <summary>
/// A <see cref="IConfigProvider"/> that also accepts <b>edits</b> - the seam the
/// web control API writes config through. Kept separate from
/// <see cref="IConfigProvider"/> so read-only consumers (and the test fakes) are
/// unaffected: only the providers that can persist an edit implement this.
/// </summary>
/// <remarks>
/// The contract mirrors the load path's atomicity (see <see cref="IConfigProvider"/>):
/// a candidate is fully validated before anything is persisted or
/// <see cref="IConfigProvider.Current"/> is touched, so a rejected edit leaves the
/// running node exactly as it was and raises no <see cref="IConfigProvider.OnChange"/>.
/// A successful <see cref="TryApply"/> persists the candidate (so it survives a
/// restart) and advances <see cref="IConfigProvider.Current"/> + raises
/// <see cref="IConfigProvider.OnChange"/> - the same signal the file watcher
/// raises - so the reconcile path is identical whether an edit arrived over the
/// web or by hand-editing the file.
/// </remarks>
public interface IWritableConfigProvider : IConfigProvider
{
    /// <summary>
    /// Validate a candidate config <b>without</b> applying it (the dry-run behind
    /// the editor's reconcile preview). Returns the validation failures, or an
    /// empty list when the candidate is valid. Never mutates state.
    /// </summary>
    IReadOnlyList<ConfigValidationError> Validate(NodeConfig candidate);

    /// <summary>
    /// Validate, persist, and apply a candidate config. On success the candidate is
    /// written to the backing store, <see cref="IConfigProvider.Current"/> advances
    /// to it, and <see cref="IConfigProvider.OnChange"/> fires (driving the
    /// reconcile). On a validation failure nothing is persisted, <c>Current</c> is
    /// unchanged, and <paramref name="errors"/> carries the reasons.
    /// </summary>
    /// <returns><c>true</c> if applied; <c>false</c> if rejected (see
    /// <paramref name="errors"/>).</returns>
    bool TryApply(NodeConfig candidate, out IReadOnlyList<ConfigValidationError> errors);

    /// <summary>
    /// The opaque version token of <see cref="IConfigProvider.Current"/> right now: the
    /// content fingerprint the control API serves as an <c>ETag</c> and accepts back as
    /// <c>If-Match</c>. Changes whenever the persisted document changes, and is stable across
    /// processes for the same document (it is a hash of the canonical JSON, not a counter), so
    /// a reader may cache it.
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Validate, persist and apply a candidate config, optionally under compare-and-swap.
    /// </summary>
    /// <param name="candidate">The whole document to persist.</param>
    /// <param name="expectedVersion">
    /// The <see cref="CurrentVersion"/> the caller built its candidate from. When supplied it is
    /// compared <b>inside</b> the provider's write lock, so a concurrent writer that landed
    /// between the caller's read and its write is caught and the apply is refused
    /// (<see cref="ConfigApplyOutcome.VersionMismatch"/>) instead of silently clobbering.
    /// <c>null</c> keeps the historical last-writer-wins behaviour.
    /// </param>
    /// <returns>The outcome, any validation errors, and the version in force afterwards
    /// (the new one on success, the current one on any refusal).</returns>
    ConfigApplyResult Apply(NodeConfig candidate, string? expectedVersion);
}

/// <summary>How an <see cref="IWritableConfigProvider.Apply"/> ended.</summary>
public enum ConfigApplyOutcome
{
    /// <summary>Persisted; <c>Current</c> advanced and <c>OnChange</c> fired.</summary>
    Applied,

    /// <summary>The candidate failed validation. Nothing was persisted.</summary>
    Invalid,

    /// <summary>The caller's <c>expectedVersion</c> is not the live one: somebody else wrote
    /// first. Nothing was persisted (the API answers 412).</summary>
    VersionMismatch,

    /// <summary>The candidate was valid but the backing store refused the write, or the
    /// provider is disposed. Nothing was persisted and the node runs on unchanged config.</summary>
    StoreFailed,
}

/// <summary>The result of an <see cref="IWritableConfigProvider.Apply"/>: the outcome, the
/// validation errors (empty unless <see cref="ConfigApplyOutcome.Invalid"/> or
/// <see cref="ConfigApplyOutcome.StoreFailed"/>), and the version in force afterwards.</summary>
public sealed record ConfigApplyResult(
    ConfigApplyOutcome Outcome,
    IReadOnlyList<ConfigValidationError> Errors,
    string Version)
{
    /// <summary>True when the candidate was persisted and applied.</summary>
    public bool Applied => Outcome == ConfigApplyOutcome.Applied;
}

/// <summary>One config validation failure - a dotted config path and a
/// human-readable message (the shape the API's <c>ValidationProblem</c> wraps).</summary>
public sealed record ConfigValidationError(string Path, string Message);
