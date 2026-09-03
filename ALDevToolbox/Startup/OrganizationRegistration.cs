using ALDevToolbox.Services;

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
        services.AddScoped<OrganizationAdminService>();
        services.AddScoped<TeamService>();
        return services;
    }
}
