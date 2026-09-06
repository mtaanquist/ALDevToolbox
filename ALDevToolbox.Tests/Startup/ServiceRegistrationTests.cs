using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Startup;

/// <summary>
/// Boots the app and resolves the registrations that have no route of their own,
/// so a service dropped while moving registrations between the Startup/ Add*
/// methods fails here rather than at first use in production. Covers every
/// <see cref="BackgroundService"/> in the app assembly and every MCP tool class.
/// </summary>
// Boots the app and sets ConnectionStrings__DefaultConnection, a process-wide
// variable; serialise with every other app-booting class or two hosts race it.
[Collection(EndpointFactoryCollection.Name)]
public class ServiceRegistrationTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;

    public ServiceRegistrationTests(TestDb db) => _db = db;

    [Fact]
    public void Every_background_service_and_mcp_tool_resolves()
    {
        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        var previousScheduler = Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _db.ConnectionString);
        Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", "1");
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseContentRoot(LocateProjectFolder());
                    builder.UseEnvironment("Test");
                });
            using var _ = factory.CreateClient();

            var appAssembly = typeof(ALDevToolbox.Services.Generation.GenerationService).Assembly;

            // Hosted services are constructed by the host, so resolving them
            // also proves their whole dependency graph is registered.
            var registered = factory.Services.GetServices<IHostedService>()
                .Select(s => s.GetType())
                .ToHashSet();
            var expected = appAssembly.GetTypes()
                .Where(t => typeof(BackgroundService).IsAssignableFrom(t)
                    && !t.IsAbstract
                    && !t.IsGenericTypeDefinition)
                .ToList();
            expected.Should().NotBeEmpty("the app registers background workers");
            expected.Except(registered).Should().BeEmpty(
                "every BackgroundService in the app assembly must be registered as a hosted service");

            // MCP tool classes are built per request by the MCP server, so a
            // dependency it can no longer resolve surfaces as a failing tool
            // call in production and nowhere else. Construct each one from a
            // request scope — most are registered in DI, the rest are activated
            // by the SDK, and both paths need the same graph to resolve.
            var tools = appAssembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true }
                    && t.GetCustomAttributes(inherit: false)
                        .Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
                .ToList();
            tools.Should().NotBeEmpty("the MCP server exposes tool classes");

            using var scope = factory.Services.CreateScope();
            var notInContainer = new List<string>();
            foreach (var tool in tools)
            {
                var instance = scope.ServiceProvider.GetService(tool);
                if (instance is null)
                {
                    notInContainer.Add(tool.Name);
                    instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, tool);
                }
                instance.Should().NotBeNull($"{tool.Name} and its dependencies must resolve");
            }

            // Every tool class but this one is registered in the container. Keep
            // that true: a tool that quietly falls off the registration list
            // still works today (the SDK activates it) but loses its lifetime.
            notInContainer.Should().BeEquivalentTo(new[] { "BcQualityTools" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", previousConnection);
            Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", previousScheduler);
        }
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
