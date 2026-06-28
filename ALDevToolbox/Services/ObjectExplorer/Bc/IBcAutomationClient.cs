namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// HTTP seam over the Business Central <em>automation</em> API for a single
/// environment (the URL keys on the environment name). Slice 1 only needs company
/// discovery; the extension upload/install/poll surface lands in slice 3 (delivery).
/// An interface so the orchestration is unit-testable without hitting Microsoft. See
/// <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IBcAutomationClient
{
    /// <summary>
    /// Lists the companies in <paramref name="environmentName"/>. Throws
    /// <see cref="BcApiException"/> on a non-success status.
    /// </summary>
    Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(
        string accessToken, string environmentName, CancellationToken ct = default);
}
