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
/// End-to-end coverage for the unified-extensions <see cref="GenerationService"/>:
/// required vs. optional extension emission, module-clone path, recursive
/// folder layout, mustache substitution (<c>{{affix}}</c> and
/// <c>{{extension_prefix}}</c>), id-range allocation, and dependency
/// resolution by stable identifier. Drives the public
/// <see cref="GenerationService.GenerateWorkspaceAsync"/> API and reads the
/// ZIP back so a private-helper refactor still has to honour the contract.
/// </summary>
public sealed class WorkspaceGenerationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Required_extension_emits_its_folder_with_app_json_and_fallback_layout()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        zip.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
        // Static fallback folders are emitted under every extension that
        // doesn't already declare them.
        zip.GetEntry("AcmeCustomer/Core/libs/.gitkeep").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/permissionsets/.gitkeep").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/Translations/.gitkeep").Should().NotBeNull();
        // Workspace-root metadata files.
        zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/README.md").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/.gitignore").Should().NotBeNull();
    }

    [Fact]
    public async Task Optional_extension_emits_only_when_user_selected()
    {
        var template = TemplateBuilder.Default();
        // Append an optional Hotfix extension to the template's required Core.
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = false,
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        // Without selection: only Core ships.
        using (var zipDefault = await GenerateAsync(PlanBuilder.WorkspacePlan()))
        {
            zipDefault.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
            zipDefault.GetEntry("AcmeCustomer/Hotfix/app.json").Should().BeNull();
        }

        // With selection: both ship.
        using var zipSelected = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedExtensions: new[] { "Hotfix" }));
        zipSelected.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
        zipSelected.GetEntry("AcmeCustomer/Hotfix/app.json").Should().NotBeNull();
    }

    [Fact]
    public async Task Module_selection_clones_extension_tree_into_workspace()
    {
        var template = TemplateBuilder.Default();
        var module = ModuleBuilder.Default("document-capture", "Document Capture")
            .WithExtensionFolder("src", ("IDocumentSink.al", "interface \"{{affix}} IDocumentSink\" { }"));
        await SeedTemplateAsync(template, module);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedModules: new[] { "document-capture" }));

        // Module key becomes the folder name; its declared file ships under it.
        var entry = zip.GetEntry("AcmeCustomer/document-capture/src/IDocumentSink.al");
        entry.Should().NotBeNull();
        ReadEntry(entry!).Should().Contain("interface \"ACME IDocumentSink\"");

        // Module clone gets an implicit dependency on the required Core extension.
        var moduleAppJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/document-capture/app.json")!));
        var deps = moduleAppJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        deps.Should().Contain(d => d.GetProperty("name").GetString() == "ACME Core");
    }

    [Fact]
    public async Task Recursive_folder_tree_emits_files_at_correct_depth()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src/codeunits", ("AppInstall.Codeunit.al", "codeunit 90000 \"{{affix}} App Install\" { Subtype = Install; }"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var entry = zip.GetEntry("AcmeCustomer/Core/src/codeunits/AppInstall.Codeunit.al");
        entry.Should().NotBeNull();
        ReadEntry(entry!).Should().Contain("ACME App Install");
    }

    [Fact]
    public async Task Example_files_skipped_when_include_examples_is_false()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src",
                ("Real.al", "codeunit Real {}"),
                ("Example.al", "codeunit Example {}"));
        // Flag the second file as an example. The builder's WithCoreFolder
        // doesn't expose IsExample; reach into the tree directly.
        var core = template.WorkspaceExtensions.Single(e => e.Path == "Core");
        var folder = core.Folders.Single(f => f.Path == "src");
        folder.Files.Single(f => f.Path == "Example.al").IsExample = true;
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan(includeExamples: false));

        zip.GetEntry("AcmeCustomer/Core/src/Real.al").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/src/Example.al").Should().BeNull();
    }

    [Fact]
    public async Task Affix_type_none_substitutes_to_empty_string()
    {
        var template = TemplateBuilder.Default();
        template.Defaults.AffixType = AffixType.None;
        template.Defaults.Affix = "ignored";
        template.WorkspaceExtensions.Single().NameTemplate = "Core";
        template.WithCoreFolder("src", ("Sample.al", "codeunit \"{{affix}} Sample\" {}"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var content = ReadEntry(zip.GetEntry("AcmeCustomer/Core/src/Sample.al")!);
        // AffixType.None ⇒ {{affix}} collapses to the empty string regardless
        // of the configured affix value.
        content.Should().Be("codeunit \" Sample\" {}");
    }

    [Fact]
    public async Task Extension_prefix_substitutes_into_extension_name_and_file_content()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src", ("Marker.al", "// brand={{extension_prefix}}"));
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(extensionPrefix: "CRO"));

        // Extension name rendered through {{extension_prefix}}.
        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("CRO Core");
        // {{extension_prefix}} substitutes in file content too.
        ReadEntry(zip.GetEntry("AcmeCustomer/Core/src/Marker.al")!)
            .Should().Be("// brand=CRO");
    }

    [Fact]
    public async Task Extension_dependency_by_path_resolves_to_generated_guid()
    {
        var template = TemplateBuilder.Default();
        // Add a second required extension that depends on Core by stable path.
        var hotfix = new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = true,
            Ordering = 1,
        };
        hotfix.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            RefExtensionPath = "Core",
            Ordering = 0,
        });
        template.WorkspaceExtensions.Add(hotfix);
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var coreId = ReadId(zip.GetEntry("AcmeCustomer/Core/app.json")!);
        var hotfixAppJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Hotfix/app.json")!));
        var deps = hotfixAppJson.RootElement.GetProperty("dependencies").EnumerateArray().ToList();
        deps.Should().HaveCount(1);
        deps[0].GetProperty("id").GetString().Should().Be(coreId);
        deps[0].GetProperty("name").GetString().Should().Be("ACME Core");
    }

    [Fact]
    public async Task Literal_dependency_emits_verbatim()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        core.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
            LitName = "System Application",
            LitPublisher = "Microsoft",
            LitVersion = "27.0.0.0",
            Ordering = 0,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        var dep = appJson.RootElement.GetProperty("dependencies").EnumerateArray().Single();
        dep.GetProperty("id").GetString().Should().Be("63ca2fa4-4f03-4f2b-a480-172fef340d3f");
        dep.GetProperty("name").GetString().Should().Be("System Application");
        dep.GetProperty("publisher").GetString().Should().Be("Microsoft");
        dep.GetProperty("version").GetString().Should().Be("27.0.0.0");
    }

    [Fact]
    public async Task Per_extension_id_range_override_takes_precedence_over_template_defaults()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        core.IdRangeFrom = 70000;
        core.IdRangeTo = 70999;
        await SeedTemplateAsync(template);

        // Plan's CoreIdRange is the usual 90000..90999 but the extension
        // override should win.
        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        var appJson = JsonDocument.Parse(ReadEntry(zip.GetEntry("AcmeCustomer/Core/app.json")!));
        var range = appJson.RootElement.GetProperty("idRanges")[0];
        range.GetProperty("from").GetInt32().Should().Be(70000);
        range.GetProperty("to").GetInt32().Should().Be(70999);
    }

    [Fact]
    public async Task Module_id_range_uses_size_override_and_advances_the_cursor()
    {
        var template = TemplateBuilder.Default();
        var wide = ModuleBuilder.Default("wide", "Wide", idRangeSize: 500);
        var follow = ModuleBuilder.Default("follow", "Follow");
        await SeedTemplateAsync(template, wide, follow);

        using var zip = await GenerateAsync(
            PlanBuilder.WorkspacePlan(selectedModules: new[] { "wide", "follow" }));

        // wide gets 91000..91499 (500 wide), follow gets 91500..91699 (default 200).
        ReadIdRange(zip, "AcmeCustomer/wide/app.json").Should().Be((91000, 91499));
        ReadIdRange(zip, "AcmeCustomer/follow/app.json").Should().Be((91500, 91699));
    }

    [Fact]
    public async Task Auto_allocated_extension_takes_a_module_slice()
    {
        var template = TemplateBuilder.Default();
        // First extension keeps Core's range from the plan (auto).
        // Second extension has no explicit range and no module → falls onto the
        // template's module_id_range_start cursor.
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Auto",
            NameTemplate = "Auto",
            Required = true,
            Ordering = 1,
        });
        await SeedTemplateAsync(template);

        using var zip = await GenerateAsync(PlanBuilder.WorkspacePlan());

        ReadIdRange(zip, "AcmeCustomer/Core/app.json").Should().Be((90000, 90999));
        ReadIdRange(zip, "AcmeCustomer/Auto/app.json").Should().Be((91000, 91199));
    }

    [Fact]
    public async Task Overlapping_id_ranges_surface_a_field_keyed_validation_error()
    {
        // Two extensions with explicit overlapping ranges.
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().IdRangeFrom = 70000;
        template.WorkspaceExtensions.Single().IdRangeTo = 70999;
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Clash",
            NameTemplate = "Clash",
            Required = true,
            Ordering = 1,
            IdRangeFrom = 70500,
            IdRangeTo = 71499,
        });
        await SeedTemplateAsync(template);

        var service = NewService();
        var act = () => service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains("IdRange"));
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

    private static string ReadId(ZipArchiveEntry entry) =>
        JsonDocument.Parse(ReadEntry(entry)).RootElement.GetProperty("id").GetString()!;

    private static (int From, int To) ReadIdRange(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"ZIP entry '{path}' not found.");
        var doc = JsonDocument.Parse(ReadEntry(entry));
        var range = doc.RootElement.GetProperty("idRanges")[0];
        return (range.GetProperty("from").GetInt32(), range.GetProperty("to").GetInt32());
    }
}
