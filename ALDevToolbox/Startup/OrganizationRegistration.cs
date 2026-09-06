using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Startup;

/// <summary>Per-organisation configuration, administration, teams and auditing.</summary>
public static class OrganizationRegistration
{
    /// <summary>Registers the organisation-scoped services.</summary>
    public static IServiceCollection AddOrganizationServices(this IServiceCollection services)
    {
        services.AddScoped<AuditService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<OrganizationBrandingService>();
        services.AddScoped<OrganizationConfigTomlImporter>();
        services.AddScoped<RepositoryProviderPolicyService>();
        services.AddScoped<OrganizationAdminService>();
        services.AddScoped<TeamService>();
        return services;
    }
}
