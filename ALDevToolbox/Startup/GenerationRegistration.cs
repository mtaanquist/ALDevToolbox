using ALDevToolbox.Services;

namespace ALDevToolbox.Startup;

/// <summary>Workspace / extension generation and export.</summary>
public static class GenerationRegistration
{
    /// <summary>Registers the generation pipeline.</summary>
    public static IServiceCollection AddGeneration(this IServiceCollection services)
    {
        services.AddScoped<WorkspaceConfigService>();
        services.AddSingleton<ALDevToolbox.Services.Generation.MustacheRenderer>();
        services.AddScoped<ALDevToolbox.Services.Generation.WorkspaceZipBuilder>();
        services.AddScoped<GenerationService>();
        services.AddScoped<ExportService>();
        return services;
    }
}
