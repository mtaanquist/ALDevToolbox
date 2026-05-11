namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Outcome of a single entity inside a bulk admin action. Used by the bulk
/// action bars on the admin list pages to render per-row results when some
/// entities in the selection couldn't be processed (e.g. last-admin guard
/// trips a role-change).
/// </summary>
public sealed record BulkActionFailure(int Id, string DisplayName, string Reason);

/// <summary>
/// Aggregated outcome of a bulk admin action. Each entity inside the
/// selection is processed independently — the loop continues past a
/// failure so a single misbehaving row can't block the rest. See
/// <c>.design/milestones.md</c> Milestone 20.
/// </summary>
public sealed record BulkActionResult(
    int TotalRequested,
    IReadOnlyList<int> SucceededIds,
    IReadOnlyList<BulkActionFailure> Failures)
{
    public bool AllSucceeded => Failures.Count == 0 && SucceededIds.Count == TotalRequested;
    public int SucceededCount => SucceededIds.Count;
    public int FailedCount => Failures.Count;
}
