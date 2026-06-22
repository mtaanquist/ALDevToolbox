using ALDevToolbox.Endpoints;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Startup must resolve the singleton system org idempotently across boots.
/// Single-tenant first-run seeding can rename the org's slug, so keying the
/// lookup on the literal <c>default</c> slug used to miss the renamed org on
/// the next boot and attempt a second <c>is_system</c> insert — a crash loop
/// against <c>ix_organizations_is_system_singleton</c>. See
/// <see cref="StartupTasks.EnsureSystemOrganizationAsync"/>.
/// </summary>
public sealed class StartupSystemOrgTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Finds_the_system_org_even_after_its_slug_was_renamed()
    {
        // Simulate a prior single-tenant boot that renamed the system org's slug.
        await using (var seed = _db.NewContext())
        {
            var org = await seed.Organizations.IgnoreQueryFilters().FirstAsync(o => o.IsSystem);
            org.Slug = "acme";
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        // The buggy slug-keyed path threw DbUpdateException here on the second boot.
        var act = () => StartupTasks.EnsureSystemOrganizationAsync(ctx, CancellationToken.None);
        var resolved = await act.Should().NotThrowAsync();

        resolved.Subject.IsSystem.Should().BeTrue();
        resolved.Subject.Slug.Should().Be("acme", "the existing renamed org is reused, not replaced");

        await using var read = _db.NewContext();
        (await read.Organizations.IgnoreQueryFilters().CountAsync(o => o.IsSystem))
            .Should().Be(1, "no second system org may be inserted");
    }

    [Fact]
    public async Task Is_idempotent_on_repeated_calls()
    {
        await using var ctx = _db.NewContext();
        var first = await StartupTasks.EnsureSystemOrganizationAsync(ctx, CancellationToken.None);
        var second = await StartupTasks.EnsureSystemOrganizationAsync(ctx, CancellationToken.None);

        second.Id.Should().Be(first.Id);
        await using var read = _db.NewContext();
        (await read.Organizations.IgnoreQueryFilters().CountAsync(o => o.IsSystem)).Should().Be(1);
    }
}
