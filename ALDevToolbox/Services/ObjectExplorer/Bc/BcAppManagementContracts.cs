using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Lifecycle of an app operation, parsed case-insensitively: the same status word comes
/// back lowercase from <c>pteInstall</c> and capitalised from the operations endpoints,
/// so callers must branch on this enum rather than on
/// <see cref="BcAppOperation.RawStatus"/>.
/// </summary>
public enum BcAppOperationStatus
{
    /// <summary>The response carried no status, or one we don't recognise — treat as "keep polling, don't act".</summary>
    Unknown,

    /// <summary>Accepted and queued for a later window; it will not go terminal while we watch.</summary>
    Scheduled,

    /// <summary>Installing now.</summary>
    Running,

    /// <summary>Terminal: installed.</summary>
    Succeeded,

    /// <summary>Terminal: refused or broke; see <see cref="BcAppOperation.ErrorCode"/>.</summary>
    Failed,

    /// <summary>Terminal: cancelled, by us or in the admin center.</summary>
    Canceled,

    /// <summary>Terminal: Business Central decided the operation wasn't needed.</summary>
    Skipped,
}

/// <summary>
/// One app install/update/uninstall operation as the Admin Center reports it — the
/// record the delivery flow polls to a terminal state.
/// <para>
/// <see cref="ErrorMessage"/> is <em>localized to the environment's language</em> (a real
/// failure came back in Danish), so it is display text only. Every decision must be keyed
/// on <see cref="ErrorCode"/> / <see cref="InnerErrorCode"/>, which Business Central
/// embeds as a JSON fragment inside that same message.
/// </para>
/// </summary>
public sealed record BcAppOperation(
    Guid Id,
    Guid? AppId,
    string Type,
    BcAppOperationStatus Status,
    string RawStatus,
    string SourceAppVersion,
    string TargetAppVersion,
    string? ScheduleKind,
    string ErrorMessage,
    string ErrorCode,
    string InnerErrorCode,
    bool CanBeCanceled,
    string CreatorPrincipalType,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? StartedOn,
    DateTimeOffset? CompletedOn)
{
    /// <summary>True once Business Central will report no further change for this operation.</summary>
    public bool IsTerminal => Status is BcAppOperationStatus.Succeeded
        or BcAppOperationStatus.Failed
        or BcAppOperationStatus.Canceled
        or BcAppOperationStatus.Skipped;
}

/// <summary>
/// One app installed in an environment, from <c>GET .../apps</c>. Read before an install
/// because the API only accepts the deferred deployment schedules for an app that is
/// already there.
/// </summary>
public sealed record BcInstalledApp(
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    string State,
    string AppType,
    bool CanBeUninstalled,
    Guid? LastOperationId,
    string LastUpdateAttemptResult)
{
    /// <summary>True for a per-tenant extension (the API spells the type <c>tenant</c>, casing not guaranteed).</summary>
    public bool IsPerTenant => string.Equals(AppType, "tenant", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A PTE version that has been uploaded and is waiting for its window, from
/// <c>GET .../apps/scheduledPteOperations</c>. <see cref="TargetAppVersion"/> and
/// <see cref="ScheduleKind"/> together identify the entry to
/// <c>removeScheduledPteVersion</c>, which is the only way to cancel it.
/// </summary>
public sealed record BcScheduledPteOperation(
    Guid Id,
    Guid? AppId,
    string Type,
    BcAppOperationStatus Status,
    string RawStatus,
    string TargetAppVersion,
    string? ScheduleKind,
    string Name,
    string Publisher,
    string SyncMode,
    string LanguageId,
    DateTimeOffset? CreatedOn);
