using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Covers the mustache substitution path in <c>GenerationService</c>: the known
/// keys, the no-examples toggle, the namespace-from-folder rule, the unknown-key
/// behaviour, and the <c>.al</c>-only restriction. Driven through the public
/// <c>GenerateWorkspaceAsync</c> API and verified against the ZIP contents so a
/// refactor of the private helper still has to keep the contract.
/// </summary>
public sealed class MustacheSubstitutionTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Known_variables_substitute_into_al_files()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder(
                "Source/Foundation",
                ("Sample.al",
                    "publisher={{publisher}};prefix={{prefix}};name={{name}};" +
                    "moduleName={{moduleName}};short={{shortName}};workspace={{workspaceName}};" +
                    "ns={{namespace}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(workspaceName: "Acme Customer"),
            "AcmeCustomer/Core/Source/Foundation/Sample.al");

        // {{name}}, {{moduleName}} and {{namespace}} all derive from per-folder
        // context — namespace replaces forward slashes with dots, the rest come
        // from the plan + template defaults verbatim.
        content.Should().Contain("publisher=Acme");
        content.Should().Contain("prefix=ACME");
        content.Should().Contain("name=Acme Customer Core");
        content.Should().Contain("moduleName=Acme Customer Core");
        content.Should().Contain("short=AcmeCustomer");
        content.Should().Contain("workspace=Acme Customer");
        content.Should().Contain("ns=Source.Foundation");
    }

    [Fact]
    public async Task Prefix_variable_emits_only_when_affix_type_is_prefix()
    {
        var template = TemplateBuilder.Default();
        template.Defaults.Affix = "ACME";
        template.Defaults.AffixType = AffixType.Prefix;
        template = template.WithCoreFolder(
            "Source",
            ("Mix.al", "prefix={{prefix}};suffix={{suffix}};affix={{affix}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/Mix.al");

        content.Should().Be("prefix=ACME;suffix=;affix=ACME");
    }

    [Fact]
    public async Task Suffix_variable_emits_only_when_affix_type_is_suffix()
    {
        var template = TemplateBuilder.Default();
        template.Defaults.Affix = "ACME";
        template.Defaults.AffixType = AffixType.Suffix;
        template = template.WithCoreFolder(
            "Source",
            ("Mix.al", "prefix={{prefix}};suffix={{suffix}};affix={{affix}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/Mix.al");

        content.Should().Be("prefix=;suffix=ACME;affix=ACME");
    }

    [Fact]
    public async Task Root_folder_files_emit_at_extension_root()
    {
        // A TemplateFolder with an empty Path writes its files directly
        // next to app.json. AppSourceCop.json is the canonical example.
        var template = TemplateBuilder.Default();
        template.Folders.Add(new TemplateFolder
        {
            OrganizationId = template.OrganizationId,
            Path = string.Empty,
            Ordering = -1,
            Files = new List<TemplateFile>
            {
                new()
                {
                    OrganizationId = template.OrganizationId,
                    Path = "AppSourceCop.json",
                    Content = "{\"mandatoryPrefix\":\"ACME\"}",
                },
            },
        });
        await SeedTemplateAsync(template);

        var service = NewService();
        var archive = await service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        var entry = zip.GetEntry("AcmeCustomer/Core/AppSourceCop.json");
        entry.Should().NotBeNull();
        using var reader = new StreamReader(entry!.Open());
        (await reader.ReadToEndAsync()).Should().Be("{\"mandatoryPrefix\":\"ACME\"}");
    }

    [Fact]
    public async Task Generator_no_longer_emits_app_source_cop_unconditionally()
    {
        // The old code path wrote AppSourceCop.json next to app.json for
        // every extension regardless of template. After the refactor a
        // template with no root-folder declaration produces only app.json
        // at the extension root.
        var template = TemplateBuilder.Default();
        await SeedTemplateAsync(template);

        var service = NewService();
        var archive = await service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        zip.GetEntry("AcmeCustomer/Core/app.json").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/AppSourceCop.json").Should().BeNull();
    }

    [Fact]
    public async Task Guid_variable_emits_a_fresh_guid()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source", ("Ids.al", "id1={{guid}};id2={{guid}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/Ids.al");

        var parts = content.Split(';');
        parts.Should().HaveCount(2);
        var first = Guid.Parse(parts[0]["id1=".Length..]);
        var second = Guid.Parse(parts[1]["id2=".Length..]);
        // Two substitutions in the same file should produce two different GUIDs.
        // Catches a future "cache the value per file" optimisation that would
        // collapse them into one.
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task Unknown_keys_are_left_intact()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source", ("Sample.al", "known={{publisher}};unknown={{nope}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/Sample.al");

        content.Should().Be("known=Acme;unknown={{nope}}");
    }

    [Fact]
    public async Task Non_al_files_pass_through_without_substitution()
    {
        // The substitution check is case-insensitive on the .al suffix, so a
        // .json file with mustache markers must come through untouched.
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source", ("notes.json", "{\"publisher\":\"{{publisher}}\"}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/notes.json");

        content.Should().Be("{\"publisher\":\"{{publisher}}\"}");
    }

    [Fact]
    public async Task Examples_off_replaces_example_files_with_a_single_gitkeep()
    {
        // Per-file IsExample filter: when the end user clears "Include
        // example AL files", every file whose IsExample flag is true is
        // skipped. A folder whose remaining files are all examples falls
        // back to a .gitkeep so the directory survives.
        var template = TemplateBuilder.Default()
            .WithCoreExampleFolder("Source/Foundation",
                ("AppInstall.al", "codeunit {{prefix}} install {}"),
                ("AppUpgrade.al", "codeunit {{prefix}} upgrade {}"));
        await SeedTemplateAsync(template);

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(includeExamples: false);

        var archive = await service.GenerateWorkspaceAsync(plan);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/.gitkeep").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/AppInstall.al").Should().BeNull();
        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/AppUpgrade.al").Should().BeNull();
    }

    [Fact]
    public async Task Examples_off_keeps_non_example_files()
    {
        // Mirror of the above: a folder that mixes example and non-example
        // files emits only the non-example ones when the checkbox is off.
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source/Foundation",
                ("Keep.al", "codeunit {{prefix}} keep {}"))
            .WithCoreExampleFolder("Source/Foundation/Examples",
                ("AppInstall.al", "codeunit {{prefix}} install {}"));
        await SeedTemplateAsync(template);

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(includeExamples: false);

        var archive = await service.GenerateWorkspaceAsync(plan);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);

        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/Keep.al").Should().NotBeNull();
        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/Examples/AppInstall.al").Should().BeNull();
        zip.GetEntry("AcmeCustomer/Core/Source/Foundation/Examples/.gitkeep").Should().NotBeNull();
    }

    [Fact]
    public async Task Nested_folder_paths_become_dotted_namespaces()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source/Finance/Posting", ("Codeunit.al", "ns={{namespace}}"));
        await SeedTemplateAsync(template);

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(),
            "AcmeCustomer/Core/Source/Finance/Posting/Codeunit.al");

        content.Should().Be("ns=Source.Finance.Posting");
    }

    [Fact]
    public async Task Module_extension_uses_module_name_in_substitution()
    {
        // moduleName + name must reflect the module being emitted, not Core.
        var template = TemplateBuilder.Default()
            .WithModuleFolder("Source", ("Mod.al", "name={{name}};moduleName={{moduleName}}"));
        await SeedTemplateAsync(template, ModuleBuilder.Default("alpha", "Alpha"));

        var content = await GenerateAndReadAsync(
            PlanBuilder.WorkspacePlan(selectedModules: new[] { "alpha" }),
            "AcmeCustomer/Alpha/Source/Mod.al");

        content.Should().Be("name=Acme Customer Alpha;moduleName=Alpha");
    }

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

    private async Task<string> GenerateAndReadAsync(ProjectPlan plan, string entryPath)
    {
        var archive = await NewService().GenerateWorkspaceAsync(plan);
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"ZIP entry '{entryPath}' not found.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
