using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The one-off pass that stamps <c>app_id</c> on build artifacts retained before
/// the column existed, from each package's own manifest (#901, Part 3).
/// </summary>
public sealed class BuildArtifactAppIdBackfillTests : IDisposable
{
    private const string AppId = "5a1b2c3d-0000-0000-0000-00000000000a";
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_row_without_an_app_id_is_stamped_from_its_manifest_and_an_unreadable_one_is_left_and_logged()
    {
        var readable = await SeedArtifactAsync(TestDb.DefaultOrgId, SyntheticApp.Build(AppId.ToUpperInvariant(), "CRONUS Base", "CRONUS", "1.4.0.0"));
        var unreadable = await SeedArtifactAsync(TestDb.DefaultOrgId, "not an app"u8.ToArray());
        var logs = new CapturingLoggerProvider();

        await using (var ctx = _db.NewContext())
        {
            var stamped = await BuildArtifactAppIdBackfill.StampOrganizationAsync(ctx, logs.CreateLogger("test"), CancellationToken.None);
            stamped.Should().Be(1);
        }

        (await AppIdOfAsync(readable)).Should().Be(AppId, "the column holds one spelling: lower-case, no braces");
        (await AppIdOfAsync(unreadable)).Should().BeNull();
        logs.Messages.Should().Contain(m => m.StartsWith("Warning:") && m.Contains($"Build artifact {unreadable}"));
    }

    [Fact]
    public async Task The_startup_pass_stamps_every_organisation_through_its_own_filter()
    {
        var mine = await SeedArtifactAsync(TestDb.DefaultOrgId, SyntheticApp.Build(AppId, "CRONUS Base", "CRONUS", "1.4.0.0"));
        var theirs = await SeedArtifactAsync(TestDb.OtherOrgId, SyntheticApp.Build(AppId, "CRONUS Base", "CRONUS", "1.4.0.0"));

        // Contexts built the way the app builds them: the organisation comes from
        // the ambient scope the pass enters, not from a fixed test context.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var services = new ServiceCollection()
            .AddScoped(_ => new AppDbContext(options, new HttpOrganizationContext(new HttpContextAccessor())))
            .BuildServiceProvider();

        await BuildArtifactAppIdBackfill.RunAsync(services, NullLogger.Instance, CancellationToken.None);

        (await AppIdOfAsync(mine)).Should().Be(AppId);
        (await AppIdOfAsync(theirs, TestDb.OtherOrgId)).Should().Be(AppId);
    }

    [Theory]
    [InlineData("{5A1B2C3D-0000-0000-0000-00000000000A}", AppId)]
    [InlineData(" 5a1b2c3d-0000-0000-0000-00000000000a ", AppId)]
    [InlineData("not-a-guid", null)]
    [InlineData(null, null)]
    public void An_app_json_id_is_stored_in_one_spelling(string? raw, string? expected) =>
        BuildArtifactAppIdBackfill.CanonicalAppId(raw).Should().Be(expected);

    private async Task<int> SeedArtifactAsync(int organizationId, byte[] content)
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject { OrganizationId = organizationId, Name = "CRONUS " + Guid.NewGuid().ToString("N")[..6], CreatedAt = now, UpdatedAt = now };
        seed.OeProjects.Add(project);
        await seed.SaveChangesAsync();
        var build = new OeProjectBuild
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            Status = ProjectBuildStatus.Ready,
            StartedAt = now,
            FinishedAt = now,
        };
        var artifact = new OeProjectBuildArtifact
        {
            OrganizationId = organizationId,
            FileName = "CRONUS_CRONUS Base_1.4.0.0.app",
            AppName = "CRONUS Base",
            AppVersion = "1.4.0.0",
            SizeBytes = content.LongLength,
            Content = content,
            CreatedAt = now,
        };
        build.Artifacts.Add(artifact);
        seed.OeProjectBuilds.Add(build);
        await seed.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task<string?> AppIdOfAsync(int artifactId, int organizationId = TestDb.DefaultOrgId)
    {
        _db.OrgContext.CurrentOrganizationId = organizationId;
        try
        {
            await using var read = _db.NewContext();
            return await read.OeProjectBuildArtifacts.AsNoTracking()
                .Where(a => a.Id == artifactId)
                .Select(a => a.AppId)
                .SingleAsync();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }
}
