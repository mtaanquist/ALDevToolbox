namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// Wire values for the <c>deploymentSchedule</c> field of the Admin Center
/// <c>pteInstall</c> endpoint — <em>when</em> Business Central installs the package
/// it has accepted. These are the strings the API expects, and they're what a release
/// pipeline stores, so nothing has to map between the two; the user-facing labels are
/// a separate concern, in <see cref="DeliveryModeDisplay"/>.
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
    /// The schedules a release pipeline may be set to.
    /// <para>
    /// <see cref="UpdateWindow"/> is deliberately absent. The engine supports it and the
    /// API accepts it, but it means "whenever Microsoft next patches this environment",
    /// which is a different promise to a customer than the delivery slot the toolbox
    /// already schedules — offering both without distinguishing them would be a trap.
    /// Whether to offer it is an open product question; until it's answered, it isn't
    /// pickable.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Pickable = [Immediate, NextMinorUpdate, NextMajorUpdate];

    /// <summary>
    /// Anything other than <see cref="Immediate"/> hands the install to Business
    /// Central to run later, so the run can't watch it finish.
    /// </summary>
    public static bool IsDeferred(string? value) => Normalize(value) is { } v && v != Immediate;

    /// <summary>
    /// True for a schedule the API only accepts on an app that is <em>already</em>
    /// installed in the environment. A first-time upload must be
    /// <see cref="Immediate"/> or <see cref="UpdateWindow"/>.
    /// </summary>
    public static bool RequiresInstalledApp(string? value) =>
        Normalize(value) is NextMinorUpdate or NextMajorUpdate;

    /// <summary>
    /// Returns the canonical wire spelling of <paramref name="value"/>, or <c>null</c> if it
    /// isn't a known schedule. Case-insensitive because the API's own casing drifts between
    /// endpoints (a real response carried <c>creatorPrincipalType: "app"</c> where the docs
    /// say <c>"App"</c>), so nothing may depend on the casing that came back.
    /// <para>
    /// A value stored before this surface existed (<c>"Current Version"</c> and friends)
    /// returns null and is refused rather than guessed at.
    /// </para>
    /// </summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the stored value is one the API will accept.</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;
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

    /// <summary>True when the stored value is one the API will accept (the legacy <c>"Force Sync"</c> is not).</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;
}

/// <summary>
/// The user-facing wording for the delivery modes. The stored values are Microsoft's
/// wire spellings, which are not English ("NextMinorUpdate", "ForceSync") — so no
/// screen renders a stored value directly; it renders one of these instead.
/// </summary>
public static class DeliveryModeDisplay
{
    /// <summary>How a deployment schedule reads on screen. An unknown (legacy) value is returned as-is so a broken row is visible rather than blank.</summary>
    public static string Schedule(string? value) => BcDeploymentSchedule.Normalize(value) switch
    {
        BcDeploymentSchedule.Immediate => "Right away",
        BcDeploymentSchedule.UpdateWindow => "In the Business Central update window",
        BcDeploymentSchedule.NextMinorUpdate => "Next minor update",
        BcDeploymentSchedule.NextMajorUpdate => "Next major update",
        _ => value ?? string.Empty,
    };

    /// <summary>How a sync mode reads on screen.</summary>
    public static string SyncMode(string? value) => BcSyncMode.Normalize(value) switch
    {
        BcSyncMode.Add => "Add",
        BcSyncMode.ForceSync => "Force sync",
        _ => value ?? string.Empty,
    };
}
