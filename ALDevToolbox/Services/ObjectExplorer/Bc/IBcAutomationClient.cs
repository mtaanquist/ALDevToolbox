namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// HTTP seam over the Business Central <em>automation</em> API for a single
/// environment (the URL keys on the environment name). Covers company discovery and
/// the per-app extension publish flow (create upload → set content → trigger upload →
/// poll deployment status). An interface so the delivery orchestration is
/// unit-testable without hitting Microsoft — the same reason we seam the compiler and
/// git behind <c>IProcessRunner</c>. See <c>.design/saas-delivery.md</c>
/// ("Publish flow").
/// </summary>
public interface IBcAutomationClient
{
    /// <summary>
    /// Lists the companies in <paramref name="environmentName"/>. Throws
    /// <see cref="BcApiException"/> on a non-success status.
    /// </summary>
    Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(
        string accessToken, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Creates an <c>extensionUpload</c> in the target company with the version target
    /// (<paramref name="schedule"/>, the API's <c>schedule</c>) and
    /// <paramref name="schemaSyncMode"/>. Returns the created upload (its
    /// <c>systemId</c>). Throws <see cref="BcApiException"/> on failure.
    /// </summary>
    Task<BcExtensionUpload> CreateExtensionUploadAsync(
        string accessToken, string environmentName, Guid companyId,
        string schedule, string schemaSyncMode, CancellationToken ct = default);

    /// <summary>
    /// Uploads the <c>.app</c> bytes into the <c>extensionContent</c> of the
    /// extensionUpload identified by <paramref name="uploadSystemId"/>
    /// (<c>application/octet-stream</c>, <c>If-Match: *</c>). Throws
    /// <see cref="BcApiException"/> on failure.
    /// </summary>
    Task SetExtensionContentAsync(
        string accessToken, string environmentName, Guid companyId,
        string uploadSystemId, byte[] appBytes, CancellationToken ct = default);

    /// <summary>
    /// Triggers the install of the uploaded extension (the <c>Microsoft.NAV.upload</c>
    /// bound action). Returns when BC has accepted the job — installation then proceeds
    /// asynchronously, tracked via <see cref="GetDeploymentStatusAsync"/>. Throws
    /// <see cref="BcApiException"/> on failure.
    /// </summary>
    Task TriggerExtensionUploadAsync(
        string accessToken, string environmentName, Guid companyId,
        string uploadSystemId, CancellationToken ct = default);

    /// <summary>
    /// Reads the environment's <c>extensionDeploymentStatus</c> rows for the company,
    /// for polling install progress. Throws <see cref="BcApiException"/> on failure.
    /// </summary>
    Task<IReadOnlyList<BcDeploymentStatus>> GetDeploymentStatusAsync(
        string accessToken, string environmentName, Guid companyId, CancellationToken ct = default);
}
