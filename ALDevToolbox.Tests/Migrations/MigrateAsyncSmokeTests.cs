using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Smoke-tests the migration pipeline against a real Postgres database. Our
/// regular fixture already runs <see cref="DatabaseFacade.MigrateAsync"/> in
/// its constructor; this test pins the contract — a brand-new database lands
/// with the seeded Default organisation visible — so a regression there
/// surfaces directly rather than as a confusing fixture failure elsewhere.
/// </summary>
public sealed class MigrateAsyncSmokeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Migration_seeds_the_default_organisation()
    {
        await using var ctx = _db.NewContext();

        var defaultOrg = await ctx.Organizations.IgnoreQueryFilters()
            .SingleOrDefaultAsync(o => o.Slug == "default");
        defaultOrg.Should().NotBeNull();
        defaultOrg!.IsSystem.Should().BeTrue("the Default org is the singleton system org other orgs fork from");
    }
}
