using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Verifies generation reads from the acting user's organisation_settings,
/// organization_files and organization_assets — never another tenant's
/// (Milestone P3.14 done-when bullet 4).
/// </summary>
public sealed class CrossOrgConfigIsolationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Generation_uses_only_the_acting_orgs_logo_and_files()
    {
        // Two organisations, each with a different logo + always-included file.
        // The Default org generates a workspace; the resulting ZIP must carry
        // the Default org's bytes, not Other's.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationAssets.AddRange(
                new OrganizationAsset
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Kind = OrganizationAssetKind.Logo,
                    ContentType = "image/png",
                    Content = new byte[] { 0xDE, 0xFA, 0x01 },
                    UpdatedAt = DateTime.UtcNow,
                },
                new OrganizationAsset
                {
                    OrganizationId = TestDb.OtherOrgId,
                    Kind = OrganizationAssetKind.Logo,
                    ContentType = "image/png",
                    Content = new byte[] { 0x07, 0x77, 0x07 },
                    UpdatedAt = DateTime.UtcNow,
                });
            ctx.OrganizationFiles.AddRange(
                new OrganizationFile
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Path = ".editorconfig",
                    Content = "default-org-content",
                    Ordering = 0,
                    UpdatedAt = DateTime.UtcNow,
                },
                new OrganizationFile
                {
                    OrganizationId = TestDb.OtherOrgId,
                    Path = ".editorconfig",
                    Content = "other-org-content",
                    Ordering = 0,
                    UpdatedAt = DateTime.UtcNow,
                });
            ctx.RuntimeTemplates.Add(
                TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId));
            await ctx.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var plan = PlanBuilder.WorkspacePlan(templateKey: "runtime-default");

        await using var genCtx = _db.NewContext();
        var gen = new GenerationService(
            genCtx,
            new WorkspaceConfigService(genCtx),
            _db.NewOrganizationConfigService(genCtx),
            _db.OrgContext,
            NullLogger<GenerationService>.Instance);

        var archive = await gen.GenerateWorkspaceAsync(plan);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        var logoEntry = zip.GetEntry("AcmeCustomer/.assets/images/logo.png");
        logoEntry.Should().NotBeNull("the workspace pulls the acting org's logo");
        using (var ms = new MemoryStream())
        {
            await using (var s = logoEntry!.Open()) await s.CopyToAsync(ms);
            ms.ToArray().Should().Equal(new byte[] { 0xDE, 0xFA, 0x01 });
        }

        var fileEntry = zip.GetEntry("AcmeCustomer/.editorconfig");
        fileEntry.Should().NotBeNull("the always-included file is written at the workspace root");
        using (var reader = new StreamReader(fileEntry!.Open()))
        {
            (await reader.ReadToEndAsync()).Should().Be("default-org-content");
        }
    }
}
