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
        // Prefer the standard configuration key so `dotnet ef database update`
        // can target a real database in CI (the migration-forward-compat job
        // sets ConnectionStrings__DefaultConnection). Fall back to a throwaway
        // DSN for "add migration" calls that don't open a connection.
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=aldevtoolbox_designtime;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs)
            .Options;
        // Design-time tooling doesn't supply an org context; pass nothing
        // and the parameter-less overload disables filtering.
        return new AppDbContext(options);
    }
}
