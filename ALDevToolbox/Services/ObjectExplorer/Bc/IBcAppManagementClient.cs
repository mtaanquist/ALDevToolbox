namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// HTTP seam over the Business Central Admin Center's <em>App Management</em> surface —
/// the endpoints that upload a per-tenant extension and report what happened to it.
/// Same host and token as <see cref="IBcAdminClient"/>, but a distinct surface with its
/// own failure modes, and an interface for the same reason the other BC clients have one:
/// the delivery flow has to be testable without calling Microsoft.
/// <para>
/// Every method takes <c>applicationFamily</c> as a string rather than hardcoding
/// <c>BusinessCentral</c>, because the family is a property of the customer's environment
/// and the API is inconsistent about its casing — pass back what the environments call
/// returned. All methods throw <see cref="BcApiException"/> on a non-success status.
/// </para>
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IBcAppManagementClient
{
    /// <summary>
    /// Uploads a <c>.app</c> package and schedules its install. Returns the operation to
    /// track: <c>running</c> for an <see cref="BcDeploymentSchedule.Immediate"/> install,
    /// <c>scheduled</c> for any deferred one, which will not go terminal while we watch.
    /// <para>
    /// <paramref name="fileName"/> must end in <c>.app</c> and <paramref name="appBytes"/>
    /// must be at most 50 MB — both are API rules, checked here so a bad call fails before
    /// it costs a round trip. Throws <see cref="ArgumentException"/> for either.
    /// </para>
    /// </summary>
    /// <param name="deploymentSchedule">A <see cref="BcDeploymentSchedule"/> value; blank leaves the API's default (Immediate).</param>
    /// <param name="syncMode">A <see cref="BcSyncMode"/> value; blank leaves the API's default (Add).</param>
    /// <param name="languageId">Install locale for the extension, e.g. <c>en-US</c>; blank leaves the API's default.</param>
    /// <param name="installOrUpdateNeededDependencies">
    /// Lets Business Central pull in dependencies it can already see. It cannot conjure a
    /// sibling extension that hasn't been uploaded yet, so it does not replace uploading in
    /// dependency order. Defaults to false at the API, so it is always sent explicitly.
    /// </param>
    Task<BcAppOperation> InstallPteAsync(
        string accessToken,
        string applicationFamily,
        string environmentName,
        byte[] appBytes,
        string fileName,
        string deploymentSchedule,
        string syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        CancellationToken ct = default);

    /// <summary>
    /// Reads one app operation by id — the poll that follows an immediate install. Returns
    /// <c>null</c> when the environment knows no such operation. Keyed on the app id and
    /// operation id from the install response rather than on the app's name, so two
    /// extensions sharing a name can't be confused for each other.
    /// </summary>
    Task<BcAppOperation?> GetAppOperationAsync(
        string accessToken,
        string applicationFamily,
        string environmentName,
        Guid appId,
        Guid operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the apps installed in the environment. Read once per delivery: the API only
    /// accepts a deferred deployment schedule for an app that is already installed.
    /// </summary>
    Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(
        string accessToken,
        string applicationFamily,
        string environmentName,
        CancellationToken ct = default);

    /// <summary>
    /// Lists PTE versions uploaded and waiting for their window. Fetched on demand — there
    /// is deliberately no background poller for these.
    /// </summary>
    Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(
        string accessToken,
        string applicationFamily,
        string environmentName,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels one scheduled PTE version, identified by app id plus
    /// <paramref name="targetVersion"/> and <paramref name="scheduleKind"/> (the API needs
    /// all three). This permanently deletes the uploaded package from Business Central's
    /// storage, so a re-release has to upload again. Returns the canceled operation.
    /// </summary>
    /// <param name="scheduleKind">The deferred schedule the version is waiting on — one of <c>UpdateWindow</c>, <c>NextMinorUpdate</c>, <c>NextMajorUpdate</c>.</param>
    Task<BcAppOperation> RemoveScheduledPteVersionAsync(
        string accessToken,
        string applicationFamily,
        string environmentName,
        Guid appId,
        string targetVersion,
        string scheduleKind,
        CancellationToken ct = default);
}
