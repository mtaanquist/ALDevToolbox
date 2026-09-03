namespace ALDevToolbox.Startup;

/// <summary>The mirrored BCQuality knowledge base. See .design/bcquality.md.</summary>
public static class BcQualityRegistration
{
    /// <summary>Registers the BCQuality ingest and search services.</summary>
    public static IServiceCollection AddBcQuality(this IServiceCollection services)
    {
        // The mirrored BCQuality knowledge base: the ingest side (git + walker, driven
        // by BcQualityRefreshScheduler) and the read side the MCP tools call. System-
        // level content — no organisation scoping. See .design/bcquality.md.
        services.AddScoped<ALDevToolbox.Services.BcQuality.BcQualityIngestService>();
        services.AddScoped<ALDevToolbox.Services.BcQuality.BcQualitySearchService>();
        return services;
    }
}
