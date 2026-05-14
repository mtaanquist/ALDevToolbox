using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Round-trip coverage for <see cref="WorkspaceConfigService"/>: a plan goes
/// <c>Plan → Build → ParseAsync → Plan</c> and every field a user would care
/// about survives the trip. Mirrors the
/// <see cref="TemplateTomlMapperRoundTripTests"/> shape.
/// </summary>
public sealed class WorkspaceConfigServiceRoundTripTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Workspace_round_trip_preserves_every_field_and_module_list()
    {
        await SeedTemplateAndModulesAsync("runtime-15", "payments", "warehouse");

        var plan = new ProjectPlan(
            TemplateKey: "runtime-15",
            WorkspaceName: "Acme Customer",
            ExtensionPrefix: "ACME",
            Brief: "Brief copy",
            Description: "Longer description with [TOML]-unfriendly chars: \" ' \n line two",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            CoreIdRangeFrom: 80000,
            CoreIdRangeTo: 80999,
            IncludeExamples: false,
            SelectedExtensionPaths: new List<string> { "Hotfix", "Reports" },
            SelectedModuleKeys: new List<string> { "payments", "warehouse" });

        var identities = new List<WorkspaceExtensionIdentity>
        {
            new(WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "ACME Core", "Core", "Acme", 80000, 80099),
            new(WorkspaceExtensionIdentity.ModuleKind, "payments", Guid.NewGuid(), "ACME Payments", "Payments", "Acme", 81000, 81099),
        };

        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);
        var toml = svc.BuildWorkspace(plan, identities);

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Kind.Should().Be(WorkspaceConfigService.WorkspaceKind);
        parsed.Extension.Should().BeNull();
        parsed.Workspace.Should().NotBeNull();
        parsed.Workspace!.TemplateKey.Should().Be(plan.TemplateKey);
        parsed.Workspace.WorkspaceName.Should().Be(plan.WorkspaceName);
        parsed.Workspace.ExtensionPrefix.Should().Be(plan.ExtensionPrefix);
        parsed.Workspace.Brief.Should().Be(plan.Brief);
        parsed.Workspace.Description.Should().Be(plan.Description);
        parsed.Workspace.ApplicationVersion.Should().Be(plan.ApplicationVersion);
        parsed.Workspace.RuntimeVersion.Should().Be(plan.RuntimeVersion);
        parsed.Workspace.CoreIdRangeFrom.Should().Be(plan.CoreIdRangeFrom);
        parsed.Workspace.CoreIdRangeTo.Should().Be(plan.CoreIdRangeTo);
        parsed.Workspace.IncludeExamples.Should().Be(plan.IncludeExamples);
        parsed.Workspace.SelectedExtensionPaths.Should().Equal(plan.SelectedExtensionPaths);
        parsed.Workspace.SelectedModuleKeys.Should().Equal(plan.SelectedModuleKeys);

        parsed.Extensions.Should().HaveCount(2);
        parsed.Extensions.Should().BeEquivalentTo(identities);
    }

    [Fact]
    public async Task Extension_round_trip_preserves_every_field_including_dependencies()
    {
        await SeedTemplateAsync("runtime-15");

        var plan = new StandaloneExtensionPlan(
            TemplateKey: "runtime-15",
            ExtensionName: "Acme Hotfix",
            Brief: "One-off bug-fix extension.",
            Description: "Multi-line\r\ndescription",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            IdRangeFrom: 50000,
            IdRangeTo: 50099,
            IncludeExamples: true,
            Publisher: "Acme",
            Dependencies: new List<DependencyEntry>
            {
                new("11111111-1111-1111-1111-111111111111", "Base App", "Microsoft", "24.0.0.0"),
                new("22222222-2222-2222-2222-222222222222", "System App", "Microsoft", "24.0.0.0"),
            });

        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);
        var toml = svc.BuildExtension(plan);

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Kind.Should().Be(WorkspaceConfigService.ExtensionKind);
        parsed.Workspace.Should().BeNull();
        parsed.Extension.Should().NotBeNull();
        parsed.Extension!.TemplateKey.Should().Be(plan.TemplateKey);
        parsed.Extension.ExtensionName.Should().Be(plan.ExtensionName);
        parsed.Extension.Brief.Should().Be(plan.Brief);
        parsed.Extension.Description.Should().Be(plan.Description);
        parsed.Extension.ApplicationVersion.Should().Be(plan.ApplicationVersion);
        parsed.Extension.RuntimeVersion.Should().Be(plan.RuntimeVersion);
        parsed.Extension.IdRangeFrom.Should().Be(plan.IdRangeFrom);
        parsed.Extension.IdRangeTo.Should().Be(plan.IdRangeTo);
        parsed.Extension.IncludeExamples.Should().Be(plan.IncludeExamples);
        parsed.Extension.Publisher.Should().Be(plan.Publisher);
        parsed.Extension.Dependencies.Should().BeEquivalentTo(plan.Dependencies);
    }

    [Fact]
    public async Task Build_emits_documented_header_so_users_recognise_the_file_in_a_diff()
    {
        await SeedTemplateAsync("runtime-15");
        var plan = new ProjectPlan(
            TemplateKey: "runtime-15",
            WorkspaceName: "X",
            ExtensionPrefix: "X",
            Brief: "", Description: "",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            CoreIdRangeFrom: 1, CoreIdRangeTo: 2,
            IncludeExamples: true,
            SelectedExtensionPaths: Array.Empty<string>(),
            SelectedModuleKeys: Array.Empty<string>());
        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);

        var toml = svc.BuildWorkspace(plan, Array.Empty<WorkspaceExtensionIdentity>());

        toml.Should().StartWith("# AL Dev Toolbox project config.");
    }

    private async Task SeedTemplateAsync(string key)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default(key));
        await ctx.SaveChangesAsync();
    }

    private async Task SeedTemplateAndModulesAsync(string templateKey, params string[] moduleKeys)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default(templateKey));
        foreach (var moduleKey in moduleKeys)
        {
            ctx.Modules.Add(ModuleBuilder.Default(moduleKey, moduleKey));
        }
        await ctx.SaveChangesAsync();
    }
}
