using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins <see cref="ReleaseComparisonService.FindObjectFileInReleaseAsync"/> -
/// the counterpart lookup behind a results row's "Compare with..." jumping
/// straight into the side-by-side file diff. Matching is by object identity
/// ((Kind, ObjectId), name fallback for id-less objects), NOT by module/path,
/// so two independently imported C/AL releases still line up.
/// </summary>
public sealed class FindObjectFileInReleaseTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Finds_the_counterpart_by_kind_and_object_id()
    {
        var (releaseId, fileId) = await SeedReleaseWithObjectAsync(
            "CRONUS objects 2016", kind: "table", objectId: 18, name: "Customer");
        // Same id, different kind - must not match a table lookup.
        await SeedReleaseWithObjectAsync("Noise", kind: "page", objectId: 18, name: "Customer Card");

        await using var read = _db.NewContext();
        var svc = NewService(read);

        (await svc.FindObjectFileInReleaseAsync(releaseId, "table", 18, "Renamed Customer"))
            .Should().Be(fileId, "id-keyed objects match on (kind, id), not name");
        (await svc.FindObjectFileInReleaseAsync(releaseId, "page", 18, "Customer"))
            .Should().BeNull();
        (await svc.FindObjectFileInReleaseAsync(releaseId, "table", 19, "Customer"))
            .Should().BeNull();
    }

    [Fact]
    public async Task Falls_back_to_case_insensitive_name_for_id_less_objects()
    {
        var (releaseId, fileId) = await SeedReleaseWithObjectAsync(
            "CRONUS interfaces", kind: "interface", objectId: null, name: "IPriceCalculation");

        await using var read = _db.NewContext();
        var svc = NewService(read);

        (await svc.FindObjectFileInReleaseAsync(releaseId, "interface", null, "ipricecalculation"))
            .Should().Be(fileId);
        (await svc.FindObjectFileInReleaseAsync(releaseId, "interface", null, "IOtherInterface"))
            .Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_object_has_no_ingested_source()
    {
        var (releaseId, _) = await SeedReleaseWithObjectAsync(
            "CRONUS objects 2016", kind: "table", objectId: 18, name: "Customer", withFile: false);

        await using var read = _db.NewContext();
        (await NewService(read).FindObjectFileInReleaseAsync(releaseId, "table", 18, "Customer"))
            .Should().BeNull();
    }

    [Fact]
    public void IsOlderRelease_prefers_bc_version_and_falls_back_to_import_time()
    {
        var older = new DateTime(2026, 1, 1);
        var newer = new DateTime(2026, 6, 1);

        // Both versions parse - version wins, even against a later import.
        ReleaseComparisonService.IsOlderRelease("25.0.0.0", newer, "26.0.0.0", older)
            .Should().BeTrue();
        ReleaseComparisonService.IsOlderRelease("26.0.0.0", older, "25.0.0.0", newer)
            .Should().BeFalse();

        // Version missing on either side (C/AL exports) - import time decides.
        ReleaseComparisonService.IsOlderRelease(null, older, null, newer).Should().BeTrue();
        ReleaseComparisonService.IsOlderRelease(null, newer, "26.0", older).Should().BeFalse();

        // Equal versions - import time breaks the tie.
        ReleaseComparisonService.IsOlderRelease("26.0.0.0", older, "26.0.0.0", newer)
            .Should().BeTrue();
    }

    private static ReleaseComparisonService NewService(AppDbContext ctx) =>
        new(ctx, NullLogger<ReleaseComparisonService>.Instance);

    /// <summary>Seeds a ready release with one module and one object; returns
    /// (releaseId, sourceFileId). With <paramref name="withFile"/> false the
    /// object is stored without an ingested source file.</summary>
    private async Task<(int ReleaseId, long? FileId)> SeedReleaseWithObjectAsync(
        string label, string kind, int? objectId, string name, bool withFile = true)
    {
        await using var write = _db.NewContext();
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "cal",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        write.OeReleases.Add(release);
        await write.SaveChangesAsync();

        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = label,
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = DateTime.UtcNow,
            DependencyCount = 0,
        };
        write.OeModules.Add(module);
        await write.SaveChangesAsync();

        long? fileId = null;
        if (withFile)
        {
            var content = $"{kind} {objectId?.ToString() ?? ""} \"{name}\"";
            var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            write.OeFileContents.Add(new FileContent
            {
                ContentHash = hash,
                Content = content,
                ContentLength = content.Length,
                LineCount = 1,
            });
            var file = new ModuleFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = module.Id,
                Path = $"src/{name}.al",
                ContentHash = hash,
                LineCount = 1,
            };
            write.OeModuleFiles.Add(file);
            await write.SaveChangesAsync();
            fileId = file.Id;
        }

        write.OeModuleObjects.Add(new ModuleObject
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Kind = kind,
            ObjectId = objectId,
            Name = name,
            LineNumber = 1,
            SourceFileId = fileId,
        });
        await write.SaveChangesAsync();
        return (release.Id, fileId);
    }
}
