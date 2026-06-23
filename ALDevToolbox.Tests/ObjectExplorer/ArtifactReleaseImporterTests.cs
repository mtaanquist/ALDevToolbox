using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="ArtifactReleaseImporter"/> against the shared
/// <see cref="TestDb"/> fixture with a stubbed artifact index: the dedup rule
/// the daily scheduler and the Artifacts tab both rely on (a version whose
/// "Business Central {Major}.{Minor} ({CC})" label already exists is skipped, no
/// import is queued) and the happy path (a new label creates an ingesting
/// release and enqueues a BcArtifact job).
/// </summary>
public sealed class ArtifactReleaseImporterTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string CountryJson = """[ { "Version": "28.2.50931.51727" } ]""";
    private const string PlatformJson = """[ { "Version": "28.2.50931.51727" } ]""";

    private ArtifactReleaseImporter NewImporter(Data.AppDbContext ctx, ReleaseImportQueue queue)
    {
        var factory = new StubHttpClientFactory(new Dictionary<string, string>
        {
            ["/indexes/dk.json"] = CountryJson,
            ["/indexes/platform.json"] = PlatformJson,
        });
        var artifacts = new BcArtifactService(factory, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance);
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations, NullLogger<ReleaseImportService>.Instance);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        return new ArtifactReleaseImporter(
            artifacts, importer, queue, persistedJobs, ctx, _db.OrgContext,
            NullLogger<ArtifactReleaseImporter>.Instance);
    }

    [Fact]
    public async Task ImportAsync_skips_a_version_whose_label_is_already_in_the_catalogue()
    {
        await using var ctx = _db.NewContext();
        var existing = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(existing);
        await ctx.SaveChangesAsync();

        var queue = new ReleaseImportQueue();
        var outcome = await NewImporter(ctx, queue).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.AlreadyImported);
        outcome.ReleaseId.Should().Be(existing.Id);
        queue.Reader.TryRead(out _).Should().BeFalse("nothing should be enqueued for a dedup hit");

        await using var read = _db.NewContext();
        (await read.OeReleases.CountAsync(r => r.Label == "Business Central 28.2 (DK)")).Should().Be(1);
    }

    [Fact]
    public async Task ImportAsync_creates_an_ingesting_release_and_enqueues_a_bc_artifact_job()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var outcome = await NewImporter(ctx, queue).ImportAsync("dk", version: null);

        outcome.Status.Should().Be(ArtifactImportStatus.Queued);
        outcome.Label.Should().Be("Business Central 28.2 (DK)");
        outcome.ReleaseId.Should().NotBeNull();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.SingleAsync(r => r.Id == outcome.ReleaseId);
        release.Label.Should().Be("Business Central 28.2 (DK)");
        release.Kind.Should().Be("first_party");
        release.Status.Should().Be("ingesting");

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(outcome.ReleaseId);
        job.Source.Should().BeOfType<ReleaseImportSource.BcArtifact>()
            .Which.ApplicationUrl.Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");

        var jobRow = await read.OeImportJobs.SingleAsync(j => j.ReleaseId == outcome.ReleaseId);
        jobRow.Kind.Should().Be("bc_artifact");
    }
}
