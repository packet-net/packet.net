using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Api;

// The config-write DTOs the control API (Slice 3) returns from PUT /config and its
// dry-run preview. Field names match docs/node-api.yaml + the web UI's types
// (System.Text.Json web defaults camel-case the PascalCase properties).

/// <summary>
/// What applying a candidate config would disrupt, grouped by how disruptive each
/// change is. Plain-language and never leaks internal type names — it's operator-facing.
/// </summary>
public sealed record ReconcilePreview(
    bool Valid,
    IReadOnlyList<ReconcileChange> Live,
    IReadOnlyList<ReconcileChange> PortRestart,
    IReadOnlyList<ReconcileChange> NodeReset);

/// <summary>A <see cref="ReconcilePreview"/> plus whether it was actually applied
/// (the PUT result) or was a dry-run (<c>applied: false</c>).</summary>
public sealed record ReconcileResult(
    bool Valid,
    IReadOnlyList<ReconcileChange> Live,
    IReadOnlyList<ReconcileChange> PortRestart,
    IReadOnlyList<ReconcileChange> NodeReset,
    bool Applied);

/// <summary>One pending change: a dotted config path, its apply impact
/// (<c>live</c> | <c>port-restart</c> | <c>node-reset</c>), and a plain-language summary.</summary>
public sealed record ReconcileChange(string Path, string Impact, string Summary);

/// <summary>The 422 body when a candidate config fails validation.</summary>
public sealed record ValidationProblem(IReadOnlyList<ConfigValidationError> Errors);
