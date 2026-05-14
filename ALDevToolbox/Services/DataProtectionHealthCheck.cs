using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ALDevToolbox.Services;

/// <summary>
/// Round-trips a tiny payload through Data Protection so <c>/healthz</c> fails
/// if the key ring is unreadable or the persistence directory has become
/// unwritable. Cookie auth and the SMTP-password ciphertext both depend on the
/// same ring, so an operator wants 503 before traffic starts hitting a broken
/// node.
/// </summary>
public sealed class DataProtectionHealthCheck : IHealthCheck
{
    private const string Purpose = "ALDevToolbox.HealthCheck";

    private readonly IDataProtectionProvider _provider;

    public DataProtectionHealthCheck(IDataProtectionProvider provider)
    {
        _provider = provider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var protector = _provider.CreateProtector(Purpose);
            var roundTripped = protector.Unprotect(protector.Protect("ok"));
            return Task.FromResult(roundTripped == "ok"
                ? HealthCheckResult.Healthy("Data Protection key ring is readable.")
                : HealthCheckResult.Unhealthy("Data Protection round-trip returned unexpected payload."));
        }
        catch (CryptographicException ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Data Protection key ring is not readable.", ex));
        }
        // Anything else (NRE, configuration bug, etc.) is a real bug and should
        // surface as an unhandled exception, not a masked "unhealthy" signal.
    }
}
