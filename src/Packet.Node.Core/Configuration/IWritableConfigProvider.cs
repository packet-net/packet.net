namespace Packet.Node.Core.Configuration;

/// <summary>
/// A <see cref="IConfigProvider"/> that also accepts <b>edits</b> — the seam the
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
/// <see cref="IConfigProvider.OnChange"/> — the same signal the file watcher
/// raises — so the reconcile path is identical whether an edit arrived over the
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
}

/// <summary>One config validation failure — a dotted config path and a
/// human-readable message (the shape the API's <c>ValidationProblem</c> wraps).</summary>
public sealed record ConfigValidationError(string Path, string Message);
