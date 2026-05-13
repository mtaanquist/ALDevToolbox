using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Issue #61: the <c>.code-workspace</c> file's <c>settings</c> block is
/// admin-editable per organisation, with mustache substitution. The generator
/// overlays a computed <c>folders</c> array onto the admin's JSON template
/// regardless of what they pasted. These tests drive
/// <see cref="GenerationService.GenerateWorkspaceAsync"/> directly so the
/// overlay and substitution contract stays honoured under refactors.
/// </summary>
public sealed class CodeWorkspaceJsonTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Default_admin_json_produces_today_settings_block()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // No SaveSettings call — the organisation_settings row stays
        // un-persisted and the service's transient fallback supplies the
        // OrganizationDefaults seed.

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        entry.Should().NotBeNull();

        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");
        settings.GetProperty("editor.formatOnSave").GetBoolean().Should().BeTrue();
        settings.GetProperty("al.enableCodeAnalysis").GetBoolean().Should().BeTrue();
        settings.GetProperty("al.ruleSetPath").GetString()
            .Should().Be("../.assets/rulesets/Company.ruleset.json");
    }

    [Fact]
    public async Task Admin_json_substitutes_mustache_variables()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        await SetCodeWorkspaceJsonAsync("""
            {
              "settings": {
                "al.ruleSetPath": "../.assets/rulesets/{{publisher}}.ruleset.json",
                "al.workspace.short_name": "{{shortName}}"
              }
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var settings = doc.RootElement.GetProperty("settings");

        // {{publisher}} resolves to the TemplateBuilder.Default() publisher
        // (the org's default publisher is empty in these tests; generation
        // pulls the publisher from the template defaults).
        settings.GetProperty("al.ruleSetPath").GetString()
            .Should().Contain("Acme");
        settings.GetProperty("al.workspace.short_name").GetString()
            .Should().Be("AcmeCustomer");
    }

    [Fact]
    public async Task Generator_overwrites_admin_supplied_folders_array()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // Admin put a folders key in — the generator must replace it with the
        // computed list. Without this guarantee the workspace would refer to
        // folders that aren't in the ZIP.
        await SetCodeWorkspaceJsonAsync("""
            {
              "folders": [
                { "path": "BogusUserChoice" }
              ],
              "settings": {}
            }
            """);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        using var doc = JsonDocument.Parse(ReadEntry(entry!));
        var paths = doc.RootElement.GetProperty("folders")
            .EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();
        paths.Should().Equal("Core");
        paths.Should().NotContain("BogusUserChoice");
    }

    [Fact]
    public async Task Generation_throws_when_admin_json_is_not_a_json_object()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());
        // Save bypasses the form validator by writing the column directly —
        // proves the generator's own guard catches a malformed row that
        // somehow slipped past validation.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "Acme",
                DefaultIdRangeFrom = 50000,
                DefaultIdRangeTo = 50999,
                CodeWorkspaceJson = "this is not json",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var service = NewService();
        var act = () => service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("codeWorkspaceJson");
    }

    // ===== helpers =====

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        return new GenerationService(
            ctx,
            new WorkspaceConfigService(ctx),
            _db.NewOrganizationConfigService(ctx),
            _db.OrgContext,
            NullLogger<GenerationService>.Instance);
    }

    private async Task SeedTemplateAsync(RuntimeTemplate template, params Module[] modules)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        if (modules.Length > 0) ctx.Modules.AddRange(modules);
        await ctx.SaveChangesAsync();
    }

    private async Task SetCodeWorkspaceJsonAsync(string json)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveCodeWorkspaceJsonAsync(json);
    }

    private async Task<ZipArchive> GenerateAsync(ProjectPlan plan)
    {
        var archive = await NewService().GenerateWorkspaceAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
