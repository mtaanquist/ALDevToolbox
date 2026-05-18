using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// xUnit collection that serialises every test class which uses
/// <see cref="EndpointFactory"/>. The factory swaps the process-wide
/// <c>ConnectionStrings__DefaultConnection</c> env var so the booted
/// <c>Program.cs</c> binds to the per-fixture Postgres database. xUnit
/// runs test classes in parallel by default, so two factory-using classes
/// racing on that env var can pick up the wrong connection string and
/// blow up with a stream error when the other fixture disposes its
/// database. Sharing one collection makes their execution sequential.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EndpointFactoryCollection
{
    public const string Name = "EndpointFactory";
}

/// <summary>
/// Boots <c>Program.cs</c> end-to-end against a per-fixture Postgres database
/// supplied by <see cref="TestDb"/>. Used by endpoint behaviour tests that
/// need the real auth + antiforgery + routing stack — the service-layer
/// fixtures don't.
///
/// The lifecycle is tied to <see cref="TestDb"/>: each fixture owns one
/// scratch database, one host, and disposes both together. CI's service
/// container is the same one used by <c>TestDb</c>; we don't fork a second
/// host (Issue #69 §"shared fixture").
/// </summary>
public sealed class EndpointFactory : IDisposable
{
    private readonly TestDb _db;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string? _previousConnection;
    private readonly string? _previousScheduler;

    public EndpointFactory(TestDb db)
    {
        _db = db;

        // ConnectionStrings:DefaultConnection is read inside
        // WebApplication.CreateBuilder(args), before any WithWebHostBuilder
        // hook can inject configuration. Setting the env var up front is
        // the supported workaround — same shape as EndpointAmbiguityTests.
        _previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        _previousScheduler = Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _db.ConnectionString);
        Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", "1");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(LocateProjectFolder());
                builder.UseEnvironment("Test");
            });
    }

    /// <summary>
    /// Returns a fresh <see cref="HttpClient"/>. Redirects are not followed so
    /// tests can assert the redirect target. The auth cookie defaults to
    /// "Secure" so requests must use HTTPS — base address is set to https.
    /// </summary>
    public HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });

    public IServiceProvider Services => _factory.Services;

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _previousConnection);
        Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", _previousScheduler);
    }

    private static string LocateProjectFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ALDevToolbox", "ALDevToolbox.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "ALDevToolbox");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the ALDevToolbox project folder from " + AppContext.BaseDirectory);
    }
}
