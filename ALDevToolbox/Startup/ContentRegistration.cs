using ALDevToolbox.Services;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Startup;

/// <summary>
/// The authored content behind the generator: templates, modules, the
/// catalogue and application versions.
/// </summary>
public static class ContentRegistration
{
    /// <summary>Registers the template / module / catalogue services.</summary>
    public static IServiceCollection AddContent(this IServiceCollection services)
    {
        services.AddScoped<FolderTreeHydrator>();
        services.AddScoped<TemplateService>();
        services.AddScoped<ModuleService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<ApplicationVersionService>();
        services.AddScoped<TemplateImportService>();
        return services;
    }
}
