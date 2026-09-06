using System.Reflection;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Pins the two properties the <see cref="IDbContextFactory{TContext}"/>
/// registration added in issue #741 has to keep: a factory-created context is
/// still tenant-scoped, and an <see cref="AuditService"/> read no longer
/// collides with a save running on the circuit's shared context.
/// </summary>
public sealed class AuditServiceContextFactoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Factory_created_context_is_scoped_to_the_acting_org()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-other", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        // The app's own registration shape: a scoped factory over a scoped
        // IOrganizationContext. If the factory built its contexts through
        // AppDbContext's one-argument constructor, the org context would be the
        // null sentinel and the tenant filter would stop meaning anything.
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        services.AddDbContextFactory<AppDbContext>(
            opts => opts
                .UseNpgsql(_db.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
            ServiceLifetime.Scoped);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var keys = await ctx.RuntimeTemplates
            .AsNoTracking()
            .Select(t => t.Key)
            .ToListAsync(TestContext.Current.CancellationToken);

        keys.Should().BeEquivalentTo(["runtime-default"],
            "a factory-created context must carry the request's organisation context");
    }

    [Fact]
    public async Task Audit_read_does_not_collide_with_a_save_on_the_shared_context()
    {
        await using var shared = _db.NewContext();
        var template = TemplateBuilder.Default("runtime-shared", organizationId: TestDb.DefaultOrgId);
        shared.RuntimeTemplates.Add(template);
        await shared.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (var seed = _db.NewContext())
        {
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = template.Id,
                Action = AuditAction.Updated,
                Timestamp = DateTime.UtcNow,
                ChangedBy = "alice",
                SnapshotJson = null,
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Before #741 the audit read ran on `shared` as well, so these two
        // racing operations threw "A second operation was started on this
        // context instance" and the user's edit was lost.
        var service = new AuditService(_db.NewContextFactory(), _db.OrgContext);
        var read = service.GetForEntityAsync(
            AuditEntityType.RuntimeTemplate, template.Id, ct: TestContext.Current.CancellationToken);
        template.Name = "Renamed";
        var save = shared.SaveChangesAsync(TestContext.Current.CancellationToken);

        var act = async () => await Task.WhenAll(read, save);
        await act.Should().NotThrowAsync();
        (await read).Should().HaveCount(1);
    }
}

/// <summary>
/// <see cref="AppDbContext"/> has two public constructors and EF's factory
/// picks one through <c>ActivatorUtilities</c>. Which one it picks is a
/// tenant-isolation question, not a detail: the one-argument constructor falls
/// back to the null organisation context, whose filter sentinel changes what
/// every query returns. Constructing a context opens no connection, so this
/// test needs no database and runs where Docker is unavailable.
/// </summary>
public sealed class AppDbContextFactoryConstructorTests
{
    [Fact]
    public void Factory_uses_the_organization_aware_constructor()
    {
        var orgContext = new AmbientOrganizationContext { CurrentOrganizationId = 42 };
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(orgContext);
        services.AddDbContextFactory<AppDbContext>(
            opts => opts
                .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
            ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        using var ctx = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContext();

        var field = typeof(AppDbContext).GetField("_orgContext", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("AppDbContext keeps its organisation context in a private field");
        field!.GetValue(ctx).Should().BeSameAs(orgContext,
            "the factory must build contexts through the (options, IOrganizationContext) constructor");
    }
}
