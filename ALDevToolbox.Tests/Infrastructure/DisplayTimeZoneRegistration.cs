using ALDevToolbox.Data;
using ALDevToolbox.Services.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Registers what <c>&lt;Timestamp&gt;</c> needs in a bUnit context (issue #942):
/// a page that shows a time now renders one, and the component injects
/// <see cref="DisplayTimeZone"/>, which reads the zone through the context
/// factory. <c>TryAdd</c> so a class that already registers a factory keeps it.
/// </summary>
internal static class DisplayTimeZoneRegistration
{
    public static IServiceCollection AddDisplayTimeZone(this IServiceCollection services, TestDb db)
    {
        services.TryAddSingleton<IDbContextFactory<AppDbContext>>(_ => db.NewContextFactory());
        services.TryAddScoped<DisplayTimeZone>();
        return services;
    }
}
