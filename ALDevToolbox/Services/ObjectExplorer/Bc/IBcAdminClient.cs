namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// HTTP seam over the Business Central <em>Admin Center</em> API — the surface that
/// lists a customer's environments (tenant-scoped by the token, authorized by the
/// maintainer's GDAP relationship). An interface so the connection orchestration is
/// unit-testable without hitting Microsoft, the same reason <c>IProcessRunner</c>
/// exists for git/alc. See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IBcAdminClient
{
    /// <summary>
    /// Lists the customer's BC environments. Throws <see cref="BcApiException"/> on a
    /// non-success status — a 401/403 here is the GDAP-missing signal the caller turns
    /// into a clear "grant GDAP, then retry" message.
    /// </summary>
    Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default);
}
