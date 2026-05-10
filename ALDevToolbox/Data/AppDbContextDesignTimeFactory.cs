using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ALDevToolbox.Data;

/// <summary>
/// Lets <c>dotnet ef</c> spin up an <see cref="AppDbContext"/> at design time
/// (for adding migrations and updating the database) without running the full
/// <c>WebApplication</c> pipeline. The connection string is intentionally a
/// throwaway DSN because design-time tooling never opens it.
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=aldevtoolbox_designtime;Username=postgres;Password=postgres")
            .Options;
        // Design-time tooling never opens a connection or executes a query,
        // so the org context is irrelevant. Pass nothing to use the parameter-
        // less overload that disables filtering.
        return new AppDbContext(options);
    }
}
