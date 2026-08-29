namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// A BC environment as returned by the Admin Center API. <see cref="Name"/> and
/// <see cref="Type"/> are the two fields every response carries; everything else is
/// optional and stays <c>null</c> when the payload omits it (the by-name response
/// omits <see cref="GeoName"/>, for instance). Enum-ish values are kept
/// <strong>verbatim</strong> — Microsoft's casing is inconsistent across endpoints
/// (<c>productFamily: "BusinessCentral"</c> beside <c>creatorPrincipalType: "app"</c>),
/// so comparisons are case-insensitive and the stored string is whatever the API said.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed record BcEnvironment(string Name, string Type)
{
    /// <summary>The display name a BC admin sees in the admin center; often equals <see cref="Name"/>.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>The application family the environment belongs to (e.g. <c>BusinessCentral</c>). Needed to address the environment in later admin-center calls.</summary>
    public string? ApplicationFamily { get; init; }

    /// <summary>Lifecycle status (<c>Active</c>, <c>Upgrading</c>, <c>SoftDeleted</c>, ...). The delivery gate reads this.</summary>
    public string? Status { get; init; }

    public string? CountryCode { get; init; }
    public Guid? AadTenantId { get; init; }

    /// <summary>Deep link to the environment's web client, for an "Open in Business Central" action.</summary>
    public string? WebClientLoginUrl { get; init; }

    public string? LocationName { get; init; }

    /// <summary>Azure geography. Present in the list response only — the by-name response omits it.</summary>
    public string? GeoName { get; init; }

    public string? RingName { get; init; }

    /// <summary>How AppSource app updates are applied to this environment.</summary>
    public string? AppSourceAppsUpdateCadence { get; init; }

    /// <summary>The platform/application version, from <c>versionDetails</c>.</summary>
    public string? Version { get; init; }

    public DateTime? GracePeriodStartDate { get; init; }
    public DateTime? EnforcedUpdatePeriodStartDate { get; init; }

    /// <summary>Set once the customer soft-deletes the environment; it still returns from the API until hard-deleted.</summary>
    public DateTime? SoftDeletedOn { get; init; }

    public DateTime? HardDeletePendingOn { get; init; }
    public string? DeleteReason { get; init; }
}

/// <summary>
/// Classification of a "Test connection" outcome, so the UI can name the step that
/// actually needs fixing. The two denial cases are deliberately separate: Entra
/// issuing a token says nothing about whether Business Central will accept the app,
/// and the two failures have different remedies in different portals.
/// </summary>
public enum BcConnectionResult
{
    /// <summary>Token acquired and environments listed.</summary>
    Success,

    /// <summary>The credentials themselves were rejected (bad tenant/client/secret, or the key ring can't decrypt the stored secret).</summary>
    AuthFailed,

    /// <summary>
    /// 401 from the Admin Center API: Entra issued the token, but Business Central
    /// won't accept the app at all. Almost always the app is missing from the admin
    /// center's "Authorized Microsoft Entra apps" list — a registration that lives in
    /// BC, not Entra, so the Entra portal looks complete while every call fails.
    /// </summary>
    AppNotAuthorized,

    /// <summary>
    /// 403 from the Admin Center API: the app is known to Business Central but isn't
    /// allowed to list environments — a missing/unconsented <c>AdminCenter.ReadWrite.All</c>,
    /// or, when acting on a customer's tenant as a partner, a missing delegated admin
    /// (GDAP) relationship.
    /// </summary>
    AccessDenied,

    /// <summary>Any other failure (network, unexpected status, malformed response).</summary>
    Error,
}

/// <summary>
/// The outcome of a "Test connection" / "Refresh environments" run: the
/// classification, the number of environments fetched on success, and a
/// user-facing message. Never carries the secret.
/// </summary>
public sealed record BcConnectionTestResult(BcConnectionResult Result, int EnvironmentCount, string Message)
{
    public bool IsSuccess => Result == BcConnectionResult.Success;
}

/// <summary>
/// Raised by the BC HTTP clients when the API returns a non-success status, so the
/// orchestrating service can classify it (e.g. 401/403 on the admin call → GDAP
/// missing). Carries the status code and a short, secret-free detail.
/// </summary>
public sealed class BcApiException : Exception
{
    public System.Net.HttpStatusCode? StatusCode { get; }

    public BcApiException(System.Net.HttpStatusCode? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// An environment's <em>Microsoft platform-update window</em>, from
/// <c>settings/upgrade</c> — when Microsoft's own updates run against that environment.
/// <para>
/// <b>This is not the toolbox's delivery slot.</b> The delivery slot lives on
/// <c>ProjectEnvironment.UpdateWindowStart/End</c>, is a commercial arrangement with the
/// customer, and is enforced by our own worker. This record is mirrored context so a
/// consultant can see Microsoft's maintenance hours before choosing that slot. The two
/// are never sourced from each other.
/// </para>
/// <para>
/// <see cref="StartTime"/>/<see cref="EndTime"/> plus <see cref="WindowsTimeZoneId"/>
/// are the stable definition of the window; the UTC pair the API also returns names only
/// the next occurrence and drifts, so it is not persisted.
/// </para>
/// </summary>
/// <param name="StartTime">Wall-clock start in <see cref="WindowsTimeZoneId"/>, or null when the environment has no window.</param>
/// <param name="EndTime">Wall-clock end, or null.</param>
/// <param name="WindowsTimeZoneId">A <em>Windows</em> time-zone id (e.g. <c>Romance Standard Time</c>) — the only form this API accepts or returns.</param>
public sealed record BcUpdateSettings(TimeOnly? StartTime, TimeOnly? EndTime, string? WindowsTimeZoneId)
{
    /// <summary>True when Microsoft has a real window for this environment (both bounds set).</summary>
    public bool IsConfigured => StartTime is not null && EndTime is not null;
}

/// <summary>
/// One platform target version for an environment, from <c>GET .../environments/{env}/updates</c>
/// — how "when does this customer get 27.6?" is answered without opening the admin center.
/// <para>
/// A version may be released or not yet (<see cref="Available"/>), and at most one is
/// <see cref="Selected"/> as the environment's next update. An unreleased version carries
/// only a rough expected month/year; a released one carries the scheduling detail.
/// </para>
/// </summary>
public sealed record BcEnvironmentUpdate(
    string TargetVersion,
    bool Available,
    bool Selected,
    string UpdateStatus,
    string TargetVersionType,
    DateTimeOffset? SelectedDateTime,
    DateTimeOffset? LatestSelectableDateTime,
    bool IgnoreUpdateWindow,
    string RolloutStatus,
    int? ExpectedMonth,
    int? ExpectedYear)
{
    /// <summary>"August 2025" for a version Microsoft hasn't released yet, else null.</summary>
    public string? ExpectedAvailability =>
        ExpectedYear is { } y && ExpectedMonth is >= 1 and <= 12
            ? $"{System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(ExpectedMonth.Value)} {y}"
            : null;
}

/// <summary>
/// A Windows time zone Business Central will accept for an update window, from
/// <c>GET applications/settings/timezones</c> — the only ids the update-window write
/// takes, so the picker is populated from here rather than from the host's own list.
/// </summary>
public sealed record BcTimeZone(string Id, string DisplayName, string CurrentUtcOffset);

/// <summary>
/// How often Marketplace (AppSource) apps on an environment are updated. Wire values,
/// sent verbatim.
/// </summary>
public static class BcAppUpdateCadence
{
    /// <summary>Microsoft's own default cadence for the environment.</summary>
    public const string Default = "Default";

    /// <summary>Only when the environment takes a major update.</summary>
    public const string DuringMajorUpgrade = "DuringMajorUpgrade";

    /// <summary>With every major and minor update.</summary>
    public const string DuringMajorMinorUpgrade = "DuringMajorMinorUpgrade";

    /// <summary>Every accepted value, in the order the picker offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Default, DuringMajorUpgrade, DuringMajorMinorUpgrade];

    /// <summary>Canonical spelling of a stored value, case-insensitively; null when unknown.</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How a cadence reads on screen. Every arm is a full-cased phrase, and an unknown
    /// value falls back to a phrase too rather than to the wire token — a value Microsoft
    /// adds later would otherwise reach a consultant as <c>DuringMajorMinorUpgrade</c>.
    /// </summary>
    public static string Display(string? value) => Normalize(value) switch
    {
        Default => "Microsoft's default",
        DuringMajorUpgrade => "Only with major updates",
        DuringMajorMinorUpgrade => "With major and minor updates",
        _ => "Not set",
    };

    /// <summary>
    /// The whole sentence a confirm shows for a cadence, naming the environment. Written
    /// out per option because "will update: only with major updates" is not a sentence,
    /// and lower-casing a display string to force one is how wire values leak into copy.
    /// </summary>
    public static string ConfirmSentence(string? value, string environmentName) => Normalize(value) switch
    {
        Default => $"AppSource apps on {environmentName} will update on Microsoft's default schedule.",
        DuringMajorUpgrade => $"AppSource apps on {environmentName} will only update when the environment takes a major Business Central update.",
        DuringMajorMinorUpgrade => $"AppSource apps on {environmentName} will update when the environment takes a major or a minor Business Central update.",
        _ => $"The way AppSource apps update on {environmentName} will change.",
    };
}
