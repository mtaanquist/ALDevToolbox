using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// What a bUnit test registers so a page's <c>&lt;Timestamp&gt;</c>s can render
/// (issue #942): <see cref="DisplayTimeZone"/> and the context factory it reads the
/// organisation's zone through.
/// </summary>
internal static class DisplayTimeZoneTestServices
{
    /// <summary>
    /// For a test with a <see cref="TestDb"/>: the zone is read from the test
    /// organisation's settings, UTC until a test sets one. The test registers
    /// <see cref="IOrganizationContext"/> itself, as those tests already do.
    /// </summary>
    public static IServiceCollection AddDisplayTimeZone(this IServiceCollection services, TestDb db)
    {
        // TryAdd, so a class that already registers a factory keeps its own.
        services.TryAddSingleton<IDbContextFactory<AppDbContext>>(_ => db.NewContextFactory());
        services.TryAddScoped<DisplayTimeZone>();
        return services;
    }

    /// <summary>
    /// For a test without a database. No organisation is in scope, so the zone is UTC
    /// without a read and the timestamps render on the first pass: a test can assert
    /// straight after <c>Render</c>, as it did before the component existed.
    /// </summary>
    public static IServiceCollection AddUtcDisplayTimeZone(this IServiceCollection services)
    {
        services.AddSingleton<IOrganizationContext>(new AmbientOrganizationContext());
        services.AddSingleton<IDbContextFactory<AppDbContext>>(new NoDatabase());
        services.AddScoped<DisplayTimeZone>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>Never asked: with no organisation in scope the zone is UTC without a read.</summary>
    private sealed class NoDatabase : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new NotSupportedException("A test without a database has no organisation to read a time zone for.");
    }
}
