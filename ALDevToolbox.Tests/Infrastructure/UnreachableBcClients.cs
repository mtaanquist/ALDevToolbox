using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Tests.Infrastructure;

// Business Central doubles for page tests that must never reach it: a render, or a
// read answered from our own mirror or the panel cache, has no business making a
// call, and one that tries fails the test instead of quietly going nowhere.

internal sealed class UnreachableHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new NotSupportedException();
}

internal sealed class UnreachableAdminClient : IBcAdminClient
{
    public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class UnreachableAppManagementClient : IBcAppManagementClient
{
    public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        => throw new NotSupportedException();
}
