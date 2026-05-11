using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Per-test Postgres database scaffolded on a process-wide shared host
/// (Milestone P4.16). The host is either:
///   * a runner-provided service container — when the
///     <c>ALDT_TEST_POSTGRES_CONNECTION</c> env var is set, that connection
///     string is used as-is. CI uses this path against
///     <c>postgres:18</c> as a service container; or
///   * a Testcontainers <c>postgres:18-alpine</c> spun up on first use —
///     local-dev path. Requires Docker on the host.
///
/// Each test fixture creates a unique database off the shared host, runs
/// migrations against it, and drops it on dispose. Migrations apply in
/// milliseconds against a small schema, so we don't bother template-database
/// cloning — measure first if the wall-clock starts to bite.
/// </summary>
public sealed class TestDb : IDisposable
{
    public const int DefaultOrgId = 1;
    public const int OtherOrgId = 2;

    private static readonly Lazy<PostgresHost> SharedHost = new(PostgresHost.Start, isThreadSafe: true);

    private readonly string _databaseName;
    private readonly string _connectionString;
    private readonly DbContextOptions<AppDbContext> _options;

    public AmbientOrganizationContext OrgContext { get; } = new() { CurrentOrganizationId = DefaultOrgId };

    public TestDb()
    {
        var host = SharedHost.Value;
        _databaseName = "aldt_test_" + Guid.NewGuid().ToString("N");
        host.CreateDatabase(_databaseName);
        _connectionString = host.ConnectionStringFor(_databaseName);

        _options = BuildOptions();

        using var ctx = new AppDbContext(_options, OrgContext);
        ctx.Database.Migrate();

        // Migration seeds the Default organisation; flip IsSeeded=true so test
        // bootstrap paths that branch on it don't kick off seed runs they
        // didn't ask for. Add the Other organisation tests use to verify
        // cross-org isolation.
        var defaultOrg = ctx.Organizations.IgnoreQueryFilters().Single(o => o.Id == DefaultOrgId);
        defaultOrg.IsSeeded = true;
        ctx.Organizations.Add(new Organization
        {
            Id = OtherOrgId,
            Name = "Other",
            Slug = "other",
            IsSeeded = true,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();

        // Postgres identity sequences don't advance when a row is inserted
        // with an explicit id; the next nextval() would otherwise collide
        // with our seeded OtherOrg at id=2. Re-align the sequence to MAX(id)
        // so SignupAsync-style inserts in tests get a free id.
        ctx.Database.ExecuteSqlRaw(
            "SELECT setval(pg_get_serial_sequence('organizations', 'id'), (SELECT MAX(id) FROM organizations))");
    }

    private DbContextOptions<AppDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    /// <summary>Returns a fresh context bound to the per-fixture database, scoped to <see cref="OrgContext"/>.</summary>
    public AppDbContext NewContext() => new(_options, OrgContext);

    /// <summary>
    /// Returns a fresh context with the audit interceptor wired up. Lets audit
    /// tests exercise the same write-path the application uses without going
    /// through DI.
    /// </summary>
    public AppDbContext NewContextWithAudit(AuditInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new AppDbContext(options, OrgContext);
    }

    public void Dispose()
    {
        // Wipe the org-config cache between fixtures: it's static, so a previous
        // test's entries would otherwise bleed into the next test's reads.
        OrganizationConfigService.ClearCache();
        // Idle pool connections hold open the per-fixture database and would
        // block DROP DATABASE; clear them before issuing the drop.
        NpgsqlConnection.ClearAllPools();
        SharedHost.Value.DropDatabase(_databaseName);
    }

    /// <summary>
    /// Returns a fresh <see cref="OrganizationConfigService"/> wired against a
    /// stub <see cref="IWebHostEnvironment"/>. Tests that don't need
    /// <c>Templates.seed/organization-defaults/</c> can use this directly; tests
    /// that do should override <c>SEED_PATH</c> via env var inside their scope.
    /// </summary>
    public OrganizationConfigService NewOrganizationConfigService(AppDbContext ctx) =>
        new(ctx, OrgContext, new StubWebHostEnvironment(), NullLogger<OrganizationConfigService>.Instance);

    /// <summary>
    /// Returns an <see cref="AuditInterceptor"/> wired to an empty
    /// <see cref="IHttpContextAccessor"/>. Audit rows attribute changes to
    /// "unknown" unless the test installs a principal on the accessor first.
    /// </summary>
    public static AuditInterceptor NewAuditInterceptor() =>
        new(new HttpContextAccessor());

    /// <summary>
    /// In-memory <see cref="IDataProtectionProvider"/> for tests that need to
    /// encrypt / decrypt round-trip — e.g. <c>SystemSettingsService</c>'s
    /// SMTP password. Lazy-initialised once per fixture.
    /// </summary>
    public IDataProtectionProvider DataProtectionProvider => _dpProvider.Value;

    private readonly Lazy<IDataProtectionProvider> _dpProvider = new(() =>
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    });

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

/// <summary>
/// Process-wide handle to the Postgres instance backing <see cref="TestDb"/>.
/// Either wraps a runner-provided service container (CI) or a Testcontainers
/// instance started on first use (local dev). Container lifetime equals the
/// test process; Testcontainers' Resource Reaper handles the local-dev cleanup
/// when a process exits abnormally.
/// </summary>
internal sealed class PostgresHost
{
    private readonly string _adminConnectionString;
    private readonly PostgreSqlContainer? _container;

    private PostgresHost(string adminConnectionString, PostgreSqlContainer? container)
    {
        _adminConnectionString = adminConnectionString;
        _container = container;
    }

    public static PostgresHost Start()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ALDT_TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new PostgresHost(fromEnv, container: null);
        }

        var container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .Build();
        container.StartAsync().GetAwaiter().GetResult();
        return new PostgresHost(container.GetConnectionString(), container);
    }

    public string ConnectionStringFor(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database };
        return builder.ConnectionString;
    }

    public void CreateDatabase(string name)
    {
        using var conn = new NpgsqlConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Quoting the identifier is sufficient because `name` is a fresh
        // GUID-derived string we control — never user input.
        cmd.CommandText = $"CREATE DATABASE \"{name}\"";
        cmd.ExecuteNonQuery();
    }

    public void DropDatabase(string name)
    {
        using var conn = new NpgsqlConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // WITH (FORCE) terminates any lingering sessions so DROP doesn't block
        // on the just-disposed test connections.
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}
