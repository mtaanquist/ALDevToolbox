namespace ALDevToolbox.Startup;

/// <summary>The stable per-deployment id that fingerprints off-site dumps.</summary>
public static class DeploymentIdentityRegistration
{
    /// <summary>Loads (or creates) the deployment id from the key volume and registers it.</summary>
    public static IServiceCollection AddDeploymentIdentity(this IServiceCollection services)
    {
        var oauthKeyDir = BootKeyPaths.KeyDirectory();
        var oauthKeyLogger = BootKeyPaths.KeyMaterialLogger();
        // Stable per-deployment id (same volume as the keys) used to fingerprint
        // off-site dumps so a restore won't clobber the DB with a neighbour
        // deployment's dump from a shared bucket. Registered as a singleton.
        var deploymentIdentity = ALDevToolbox.Services.DeploymentIdentity.LoadOrCreate(oauthKeyDir, oauthKeyLogger);
        services.AddSingleton(deploymentIdentity);
        return services;
    }
}
