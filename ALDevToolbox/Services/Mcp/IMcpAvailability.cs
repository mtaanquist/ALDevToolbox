using ALDevToolbox.Services;

namespace ALDevToolbox.Services.Mcp;

/// <summary>
/// Resolves whether MCP is currently turned on for this deployment. The
/// default implementation just delegates to
/// <see cref="SystemSettingsService.IsMcpEnabledAsync"/>; tests that don't
/// want to wire up the full settings stack can substitute a fake.
/// </summary>
public interface IMcpAvailability
{
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
}

internal sealed class SystemSettingsMcpAvailability : IMcpAvailability
{
    private readonly SystemSettingsService _settings;

    public SystemSettingsMcpAvailability(SystemSettingsService settings)
    {
        _settings = settings;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        _settings.IsMcpEnabledAsync(ct);
}
