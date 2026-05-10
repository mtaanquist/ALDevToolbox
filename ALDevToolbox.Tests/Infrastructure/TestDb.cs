using ALDevToolbox.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Per-test SQLite in-memory database. The connection is held open for the
/// fixture's lifetime so the schema (and any rows) survive across the multiple
/// <see cref="AppDbContext"/> instances a test typically opens. Disposing the
/// fixture drops the database. The milestone explicitly calls for the SQLite
/// provider rather than the EF in-memory provider so tests see the same SQL
/// behaviour the app sees in production — see <c>.design/milestones.md</c>.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    /// <summary>Returns a fresh context bound to the same in-memory database.</summary>
    public AppDbContext NewContext() => new(_options);

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
        return new AppDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
