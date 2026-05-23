using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Validation;

/// <summary>
/// Covers field-keyed validation through <see cref="GenerationService"/>: every
/// rule the form layer mirrors must surface a <see cref="PlanValidationException"/>
/// with the corresponding field name as the dictionary key, so the form UI can
/// render the message inline next to the input. The form-post handler in
/// <c>Program.cs</c> writes those keys verbatim into a "<c>  - Key: Value</c>"
/// body — keep these field names stable.
/// </summary>
public sealed class PlanValidationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Workspace_plan_with_missing_required_fields_surfaces_each_field_key()
    {
        var service = NewService();
        var plan = new ProjectPlan(
            TemplateKey: string.Empty,
            WorkspaceName: string.Empty,
            ExtensionPrefix: string.Empty,
            Brief: string.Empty,
            Description: string.Empty,
            ApplicationVersion: string.Empty,
            RuntimeVersion: string.Empty,
            CoreIdRangeFrom: 0,
            CoreIdRangeTo: 0,
            IncludeExamples: false,
            SelectedExtensionPaths: Array.Empty<string>(),
            SelectedModuleKeys: Array.Empty<string>());

        var act = () => service.GenerateWorkspaceAsync(plan);

        var ex = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
        ex.Errors.Should().ContainKeys(
            nameof(plan.TemplateKey),
            nameof(plan.WorkspaceName),
            nameof(plan.CoreIdRangeFrom),
            nameof(plan.CoreIdRangeTo),
            nameof(plan.ApplicationVersion),
            nameof(plan.RuntimeVersion));
    }

    [Fact]
    public async Task Workspace_plan_with_invalid_workspace_name_keys_the_error_under_workspace_name()
    {
        var service = NewService();
        // Invalid: starts with a digit. Pattern allows letters/digits/spaces but
        // must lead with a letter.
        var plan = PlanBuilder.WorkspacePlan(workspaceName: "9Bad");

        var ex = (await service.Invoking(s => s.GenerateWorkspaceAsync(plan))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey(nameof(plan.WorkspaceName));
        ex.Errors[nameof(plan.WorkspaceName)].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Workspace_plan_with_to_not_greater_than_from_keys_the_error_under_to()
    {
        // The "from <= to" invariant the milestone calls out — a reviewer who
        // removes this check should see a red test before they see a red
        // generation in the UI.
        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(coreFrom: 90000, coreTo: 89000);

        var ex = (await service.Invoking(s => s.GenerateWorkspaceAsync(plan))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey(nameof(plan.CoreIdRangeTo));
    }

    [Fact]
    public async Task Workspace_plan_with_unknown_template_key_surfaces_template_key_error()
    {
        var service = NewService();
        // Plan is valid in shape but the template doesn't exist — the service
        // throws the same PlanValidationException so the UI can pin the error
        // to the template selector.
        var plan = PlanBuilder.WorkspacePlan(templateKey: "no-such-template");

        var ex = (await service.Invoking(s => s.GenerateWorkspaceAsync(plan))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey(nameof(plan.TemplateKey));
    }

    [Fact]
    public async Task Extension_plan_with_invalid_extension_name_keys_the_error_under_extension_name()
    {
        var service = NewService();
        // Extension names disallow spaces (unlike workspace names).
        var plan = PlanBuilder.ExtensionPlan(extensionName: "Has Space");

        var ex = (await service.Invoking(s => s.GenerateExtensionAsync(plan))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey(nameof(plan.ExtensionName));
    }

    [Fact]
    public async Task Extension_plan_with_duplicate_dependency_ids_emits_indexed_error_keys()
    {
        // The picker prevents duplicates client-side; a direct POST could still
        // ship two rows with the same GUID. The service surfaces the duplicate
        // under "Dependencies[i].DepId" so the UI can highlight the offending
        // row, mirroring ModuleService.
        var service = NewService();
        var dup = "00000000-0000-0000-0000-000000000001";
        var plan = PlanBuilder.ExtensionPlan(dependencies: new[]
        {
            new DependencyEntry(dup, "Base", "Microsoft", "1.0.0.0"),
            new DependencyEntry(dup, "Base Again", "Microsoft", "1.0.0.0"),
        });

        var ex = (await service.Invoking(s => s.GenerateExtensionAsync(plan))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Dependencies[1].DepId");
    }

    [Fact]
    public void Plan_validation_exception_exposes_the_full_error_dictionary()
    {
        // A defensive check: the form-post handler iterates Errors directly to
        // render each "  - Key: Value" line. Anything that loses the dictionary
        // shape (e.g. flattening to a single message string) breaks the inline
        // form rendering — keep the contract obvious.
        var errors = new Dictionary<string, string>
        {
            ["FieldA"] = "First problem.",
            ["FieldB"] = "Second problem.",
        };

        var ex = new PlanValidationException(errors);

        ex.Errors.Should().HaveCount(2);
        ex.Errors["FieldA"].Should().Be("First problem.");
        ex.Errors["FieldB"].Should().Be("Second problem.");
        ex.Message.Should().Contain("2 validation error(s)");
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
}
