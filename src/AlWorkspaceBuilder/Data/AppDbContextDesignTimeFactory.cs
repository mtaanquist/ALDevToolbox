using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AlWorkspaceBuilder.Data;

/// <summary>
/// Lets <c>dotnet ef</c> spin up an <see cref="AppDbContext"/> at design time
/// (for adding migrations and updating the database) without running the full
/// <c>WebApplication</c> pipeline. The connection string is intentionally a
/// throwaway file path because design-time tooling never opens it.
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=designtime.db")
            .Options;
        return new AppDbContext(options);
    }
}
