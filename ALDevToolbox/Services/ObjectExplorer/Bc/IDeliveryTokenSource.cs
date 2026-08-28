namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// What the delivery worker needs from the connection layer to call BC for a project:
/// a valid S2S access token and the tenant its URLs must address. A seam (implemented
/// by <see cref="ProjectConnectionService"/>) so the delivery orchestration can be
/// unit-tested without the real OAuth round-trip or the Data Protection key ring — the
/// same testability reason we seam the automation API behind
/// <see cref="IBcAutomationClient"/>. The secret never crosses this boundary; only the
/// resulting bearer token does. See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IDeliveryTokenSource
{
    /// <summary>
    /// Returns the token and tenant for the project, or throws
    /// <see cref="BcApiException"/> with a clear, secret-free message when the
    /// connection isn't set up, the secret has expired, or Entra rejects the
    /// credentials.
    /// </summary>
    Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default);
}

/// <summary>
/// A project's resolved BC calling context. Both halves are needed on every automation
/// call: the bearer token authenticates it, and the tenant id is part of the URL (see
/// <see cref="BcConstants.AutomationBaseFormat"/>), so returning only the token left
/// the worker unable to build a valid URL.
/// </summary>
public sealed record BcDeliveryContext(string AccessToken, Guid TenantId);
