namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>What an environment's Admin Center <c>status</c> means for a delivery.</summary>
public enum BcEnvironmentReadiness
{
    /// <summary>The environment is <c>Active</c> — safe to publish to.</summary>
    Ready,

    /// <summary>Busy with an update or not up yet (<c>Upgrading</c>, <c>Preparing</c>, <c>NotReady</c>, <c>Recovering</c>). Retry later.</summary>
    Busy,

    /// <summary>On its way out (<c>Removing</c>, <c>SoftDeleting</c>, <c>SoftDeleted</c>). Retrying won't help.</summary>
    Deleting,

    /// <summary>Any <c>*Failed</c> status — the environment needs attention in Business Central.</summary>
    Failed,

    /// <summary>No status recorded, or one we don't recognise. Deliberately not a refusal — see <see cref="BcEnvironmentStatus"/>.</summary>
    Unknown,
}

/// <summary>
/// Classifies the Admin Center environment <c>status</c> string so both delivery
/// gates — at scheduling and again at claim time — refuse for the same reasons with
/// the same words. Comparisons are case-insensitive because Microsoft's casing is
/// inconsistent across endpoints, and an unrecognised (or absent) status is treated
/// as <see cref="BcEnvironmentReadiness.Unknown"/> and <em>allowed</em>: rows fetched
/// before this field was captured have no status, and a status Microsoft adds later
/// shouldn't silently block every release. See <c>.design/saas-delivery.md</c>.
/// </summary>
public static class BcEnvironmentStatus
{
    /// <summary>The one status that means "publish away".</summary>
    public const string Active = "Active";

    private static readonly string[] BusyStatuses = ["Upgrading", "Preparing", "NotReady", "Recovering"];
    private static readonly string[] DeletingStatuses = ["Removing", "SoftDeleting", "SoftDeleted"];

    /// <summary>Classifies a status string. Null/blank is <see cref="BcEnvironmentReadiness.Unknown"/>.</summary>
    public static BcEnvironmentReadiness Classify(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return BcEnvironmentReadiness.Unknown;
        var s = status.Trim();

        if (s.Equals(Active, StringComparison.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Ready;
        if (BusyStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Busy;
        if (DeletingStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Deleting;
        // Every failure status Microsoft documents ends in "Failed" (PreparingFailed,
        // UpgradingFailed, ...), so match the suffix rather than enumerating them.
        if (s.EndsWith("Failed", StringComparison.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Failed;

        return BcEnvironmentReadiness.Unknown;
    }

    /// <summary>True when a delivery may go ahead (ready, or no status to judge by).</summary>
    public static bool CanPublish(string? status) =>
        Classify(status) is BcEnvironmentReadiness.Ready or BcEnvironmentReadiness.Unknown;

    /// <summary>
    /// The refusal message for a status that blocks publishing, naming the status and
    /// saying what to do next. Returns null when the status doesn't block.
    /// </summary>
    public static string? RefusalMessage(string environmentName, string? status)
    {
        var name = string.IsNullOrWhiteSpace(environmentName) ? "This environment" : $"'{environmentName}'";
        var shown = (status ?? string.Empty).Trim();
        return Classify(status) switch
        {
            BcEnvironmentReadiness.Busy =>
                $"Business Central reports {name} as {shown} right now, so it can't take an install. Wait for it to finish, then release again.",
            BcEnvironmentReadiness.Deleting =>
                $"Business Central reports {name} as {shown} — it's being removed. Pick another environment.",
            BcEnvironmentReadiness.Failed =>
                $"Business Central reports {name} as {shown} — the environment is in a failed state in Business Central. Sort it out in the admin center, then release again.",
            _ => null,
        };
    }
}
