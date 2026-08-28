namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Wire values for the <c>deploymentSchedule</c> field of the Admin Center
/// <c>pteInstall</c> endpoint — <em>when</em> Business Central installs the package
/// it has accepted. These are the strings the API expects, so they're sent verbatim;
/// the user-facing labels are a separate concern for the release-pipeline UI.
/// <para>
/// The API constrains the value by whether the app is already installed in the
/// environment: a brand-new PTE must use <see cref="Immediate"/> or
/// <see cref="UpdateWindow"/>, while an update to an installed PTE may use any of them.
/// </para>
/// </summary>
public static class BcDeploymentSchedule
{
    /// <summary>Install as soon as the upload is accepted; the operation goes to <c>running</c>.</summary>
    public const string Immediate = "Immediate";

    /// <summary>Defer to the environment's Microsoft update window; the operation stays <c>scheduled</c>.</summary>
    public const string UpdateWindow = "UpdateWindow";

    /// <summary>Defer to the environment's next minor platform update.</summary>
    public const string NextMinorUpdate = "NextMinorUpdate";

    /// <summary>Defer to the environment's next major platform update.</summary>
    public const string NextMajorUpdate = "NextMajorUpdate";

    /// <summary>Every accepted wire value, in the order the API documents them.</summary>
    public static readonly IReadOnlyList<string> All = [Immediate, UpdateWindow, NextMinorUpdate, NextMajorUpdate];

    /// <summary>
    /// Returns the canonical wire spelling of <paramref name="value"/>, or <c>null</c> if it
    /// isn't a known schedule. Case-insensitive because the API's own casing drifts between
    /// endpoints (a real response carried <c>creatorPrincipalType: "app"</c> where the docs
    /// say <c>"App"</c>), so nothing may depend on the casing that came back.
    /// </summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Wire values for the <c>syncMode</c> field of <c>pteInstall</c> — how the schema
/// change is applied. Note the API dropped the space the older automation API used
/// ("Force Sync"), so a value stored under the old surface is not valid here.
/// </summary>
public static class BcSyncMode
{
    /// <summary>Additive schema changes only; the safe default the API also assumes when omitted.</summary>
    public const string Add = "Add";

    /// <summary>Destructive schema changes allowed — data in dropped fields is lost.</summary>
    public const string ForceSync = "ForceSync";

    /// <summary>Every accepted wire value.</summary>
    public static readonly IReadOnlyList<string> All = [Add, ForceSync];

    /// <summary>Returns the canonical wire spelling of <paramref name="value"/>, or <c>null</c> if unknown. Case-insensitive.</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

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
