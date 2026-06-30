namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The one thing the delivery worker needs from the connection layer: a valid BC S2S
/// access token for a project. A seam (implemented by
/// <see cref="ProjectConnectionService"/>) so the delivery orchestration can be
/// unit-tested without the real OAuth round-trip or the Data Protection key ring — the
/// same testability reason we seam the automation API behind
/// <see cref="IBcAutomationClient"/>. The secret never crosses this boundary; only the
/// resulting bearer token does. See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IDeliveryTokenSource
{
    /// <summary>
    /// Returns a valid access token for the project, or throws <see cref="BcApiException"/>
    /// with a clear, secret-free message when the connection isn't set up, the secret has
    /// expired, or Entra rejects the credentials.
    /// </summary>
    Task<string> AcquireDeliveryTokenAsync(int projectId, CancellationToken ct = default);
}
