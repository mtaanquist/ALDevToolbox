using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Per-test SQLite in-memory database. The connection is held open for the
/// fixture's lifetime so the schema (and any rows) survive across the multiple
/// <see cref="AppDbContext"/> instances a test typically opens. Disposing the
/// fixture drops the database. The milestone explicitly calls for the SQLite
/// provider rather than the EF in-memory provider so tests see the same SQL
/// behaviour the app sees in production — see <c>.design/milestones.md</c>.
///
/// M13: <see cref="OrgContext"/> is the ambient organisation scope for tests.
/// Mutate <see cref="AmbientOrganizationContext.CurrentOrganizationId"/> /
/// <c>CurrentUserId</c> to switch tenants mid-test. <see cref="DefaultOrgId"/>
/// is seeded automatically into <c>organizations</c> so foreign-keys resolve.
/// </summary>
public sealed class TestDb : IDisposable
{
    public const int DefaultOrgId = 1;
    public const int OtherOrgId = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AmbientOrganizationContext OrgContext { get; } = new() { CurrentOrganizationId = DefaultOrgId };

    public TestDb()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options, OrgContext);
        ctx.Database.EnsureCreated();
        // Seed the two organisations every test starts with.
        ctx.Organizations.AddRange(
            new Organization { Id = DefaultOrgId, Name = "Default", Slug = "default", IsSeeded = true, CreatedAt = DateTime.UtcNow },
            new Organization { Id = OtherOrgId, Name = "Other", Slug = "other", IsSeeded = true, CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    /// <summary>Returns a fresh context bound to the same in-memory database, scoped to <see cref="OrgContext"/>.</summary>
    public AppDbContext NewContext() => new(_options, OrgContext);

    /// <summary>
    /// Returns a fresh context with the audit interceptor wired up. Lets audit
    /// tests exercise the same write-path the application uses without going
    /// through DI.
    /// </summary>
    public AppDbContext NewContextWithAudit(AuditInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        return new AppDbContext(options, OrgContext);
    }

    public void Dispose()
    {
        // Wipe the org-config cache between fixtures: it's static, so a previous
        // test's entries would otherwise bleed into the next test's reads.
        OrganizationConfigService.ClearCache();
        _connection.Dispose();
    }

    /// <summary>
    /// Returns a fresh <see cref="OrganizationConfigService"/> wired against a
    /// stub <see cref="IWebHostEnvironment"/>. Tests that don't need
    /// <c>Templates.seed/organization-defaults/</c> can use this directly; tests
    /// that do should override <c>SEED_PATH</c> via env var inside their scope.
    /// </summary>
    public OrganizationConfigService NewOrganizationConfigService(AppDbContext ctx) =>
        new(ctx, OrgContext, new StubWebHostEnvironment(), NullLogger<OrganizationConfigService>.Instance);

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ALDevToolbox.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
