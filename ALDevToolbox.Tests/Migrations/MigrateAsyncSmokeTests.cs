using ALDevToolbox.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Several migrations are hand-rolled raw SQL — M14's organisation table
/// rebuilds, the audit-log backfill, the seed Default org INSERT — none of
/// which the rest of the suite exercises because <see cref="TestDb"/> uses
/// <c>EnsureCreated</c>. This fixture runs the real migration pipeline against
/// a fresh SQLite database so SQL-generation and runtime errors both surface
/// in CI rather than the next time a developer runs <c>dotnet run</c>.
/// </summary>
public sealed class MigrateAsyncSmokeTests
{
    [Fact]
    public async Task All_migrations_apply_to_a_fresh_sqlite_database()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            // Mirror Program.cs: the model snapshot is intentionally not kept
            // in lock-step with the hand-rolled migrations, so EF's pending-
            // changes check would otherwise abort MigrateAsync.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var ctx = new AppDbContext(options);

        Func<Task> migrate = () => ctx.Database.MigrateAsync();
        await migrate.Should().NotThrowAsync();

        // Spot-check the Default organisation seeded by M14 — proves the raw
        // INSERT and the FK rebuild both ran, not just CREATE TABLE.
        var defaultOrg = await ctx.Organizations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(o => o.Slug == "default");
        defaultOrg.Should().NotBeNull();
    }
}
