using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Covers the validate-only entry points the generator pages call before they
/// let their native POST through (#546).
///
/// The load-bearing property is <em>parity</em>: whatever
/// <c>GenerateWorkspaceAsync</c> / <c>GenerateExtensionAsync</c> would refuse,
/// the matching <c>Validate…Async</c> must report — and whatever they accept,
/// it must wave through. If the two ever disagree the page either blocks a
/// legal plan or waves through an illegal one, and the second case lands the
/// user on exactly the error page inline validation exists to avoid. Each
/// rejection test below therefore asserts *both* sides rather than trusting
/// that they share a code path.
/// </summary>
public sealed class ValidateOnlyTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ===== workspace =====

    [Fact]
    public async Task A_good_workspace_plan_reports_nothing()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "WorkspaceName")]
    [InlineData("9Lives", "WorkspaceName")]          // must start with a letter
    [InlineData("Bad-Name", "WorkspaceName")]        // no punctuation
    public async Task A_bad_workspace_name_is_reported_and_refused(string name, string expectedKey)
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(workspaceName: name);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey(expectedKey);
        await AssertGenerateRefusesAsync(plan, expectedKey);
    }

    [Fact]
    public async Task An_inverted_core_id_range_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(coreFrom: 90999, coreTo: 90000);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("CoreIdRangeTo");
        await AssertGenerateRefusesAsync(plan, "CoreIdRangeTo");
    }

    [Fact]
    public async Task A_template_that_does_not_exist_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(templateKey: "no-such-template");

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("TemplateKey");
        await AssertGenerateRefusesAsync(plan, "TemplateKey");
    }

    /// <summary>
    /// The rule the page could not mirror in HTML and the reason this entry
    /// point has to load the template rather than just check the plan's shape:
    /// the overlap is between *resolved* ranges, which only exist once the
    /// template's extensions and the selected modules have been walked.
    /// </summary>
    [Fact]
    public async Task Overlapping_id_ranges_are_reported_and_refused()
    {
        // Two template extensions with explicit ranges that collide. Nothing
        // the plan carries can cause or avoid this - it is a property of the
        // template plus the selection, which is exactly why the check needs
        // the template loaded.
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
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }
        var plan = PlanBuilder.WorkspacePlan();

        var errors = await NewService().ValidateWorkspaceAsync(plan);

        errors.Should().NotBeEmpty();
        errors.Keys.Should().Contain(k => k.Contains("IdRange"));
        await AssertGenerateRefusesAsync(plan, expectedKeyFragment: "IdRange");
    }

    // ===== standalone extension =====

    [Fact]
    public async Task A_good_extension_plan_reports_nothing()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateExtensionAsync(PlanBuilder.ExtensionPlan());

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task An_extension_name_with_a_space_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(extensionName: "My Addon");

        (await NewService().ValidateExtensionAsync(plan)).Should().ContainKey("ExtensionName");
        await AssertGenerateRefusesAsync(plan, "ExtensionName");
    }

    /// <summary>
    /// Publisher is not a form field — it comes from the org's defaults — so a
    /// blank one used to reject the submit with "Required." next to nothing at
    /// all. The message has to say where to go instead.
    /// </summary>
    [Fact]
    public async Task A_missing_org_publisher_is_reported_with_somewhere_to_go()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(publisher: "");

        var errors = await NewService().ValidateExtensionAsync(plan);

        errors.Should().ContainKey("Publisher");
        errors["Publisher"].Should().Contain("Administration").And.Contain("Defaults");
        errors["Publisher"].Should().NotBe("Required.");
    }

    [Fact]
    public async Task A_duplicate_dependency_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var dep = new DependencyEntry("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "Microsoft", "24.0.0.0");
        var plan = PlanBuilder.ExtensionPlan(dependencies: new[] { dep, dep });

        var errors = await NewService().ValidateExtensionAsync(plan);

        errors.Keys.Should().Contain(k => k.StartsWith("Dependencies["));
        await AssertGenerateRefusesAsync(plan, expectedKeyFragment: "Dependencies[");
    }

    // ===== helpers =====

    private async Task AssertGenerateRefusesAsync(ProjectPlan plan, string expectedKeyFragment)
    {
        var act = async () => await NewService().GenerateWorkspaceAsync(plan);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains(expectedKeyFragment));
    }

    private async Task AssertGenerateRefusesAsync(StandaloneExtensionPlan plan, string expectedKeyFragment)
    {
        var act = async () => await NewService().GenerateExtensionAsync(plan);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains(expectedKeyFragment));
    }

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        return new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
    }

    private async Task SeedTemplateAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default());
        ctx.Modules.Add(ModuleBuilder.Default());
        await ctx.SaveChangesAsync();
    }
}
