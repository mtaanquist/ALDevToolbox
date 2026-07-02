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
            var defaultFile = new OrganizationFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = ".editorconfig",
                Content = "default-org-content",
                Ordering = 0,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OrganizationFiles.AddRange(
                defaultFile,
                new OrganizationFile
                {
                    OrganizationId = TestDb.OtherOrgId,
                    Path = ".editorconfig",
                    Content = "other-org-content",
                    Ordering = 0,
                    UpdatedAt = DateTime.UtcNow,
                });
            // Save the file first so its primary key is populated; the
            // template-included-files join needs an existing FK target.
            await ctx.SaveChangesAsync();

            var template = TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId);
            // Opt the template into the org's .editorconfig — the generator
            // now filters OrganizationFile rows by the per-template join, so
            // unopted files don't land in the ZIP.
            template.IncludedFiles.Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                OrganizationFileId = defaultFile.Id,
                Ordering = 0,
            });
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var plan = PlanBuilder.WorkspacePlan(templateKey: "runtime-default");

        await using var genCtx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        var gen = new GenerationService(
            genCtx,
            _db.NewOrganizationConfigService(genCtx),
            new FolderTreeHydrator(genCtx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(genCtx)),
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

    [Fact]
    public async Task GetForAsync_refuses_a_different_org_while_a_request_is_in_scope()
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        // Its own org: fine.
        var act = () => svc.GetForAsync(TestDb.OtherOrgId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cross-organisation config read*");

        // Same org is allowed (no throw).
        var ownAct = () => svc.GetForAsync(TestDb.DefaultOrgId);
        await ownAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetForAsync_allows_any_org_when_no_request_is_in_scope()
    {
        // Pre-auth / seed / bootstrap callers have no org in scope and may load
        // any org's config to populate it. See #489.
        _db.OrgContext.CurrentOrganizationId = null;
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.GetForAsync(TestDb.OtherOrgId);
        await act.Should().NotThrowAsync();
    }
}
