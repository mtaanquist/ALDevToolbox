using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Catalogue;

/// <summary>
/// Field-keyed validation and cross-org isolation for the application-version
/// catalogue. The admin editor renders inline errors keyed by
/// <c>Entries[i].Field</c>; renaming any of those keys breaks the form layer.
/// </summary>
public sealed class ApplicationVersionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SaveAsync_with_missing_required_fields_keys_each_error()
    {
        var svc = NewService();
        var input = new ApplicationVersionInput(
            Id: null, Key: "", Name: "", Application: "", Runtime: "", Deprecated: false);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKeys(
            "Entries[0].Key",
            "Entries[0].Name",
            "Entries[0].Application",
            "Entries[0].Runtime");
    }

    [Fact]
    public async Task SaveAsync_with_bad_application_version_keys_application_error()
    {
        var svc = NewService();
        var input = new ApplicationVersionInput(
            Id: null, Key: "bc24", Name: "BC 24", Application: "twenty-four", Runtime: "15.0", Deprecated: false);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[0].Application");
    }

    [Fact]
    public async Task SaveAsync_with_bad_runtime_keys_runtime_error()
    {
        var svc = NewService();
        var input = new ApplicationVersionInput(
            Id: null, Key: "bc24", Name: "BC 24", Application: "24.0.0.0", Runtime: "fifteen", Deprecated: false);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[0].Runtime");
    }

    [Fact]
    public async Task SaveAsync_pads_short_versions_to_canonical_shape()
    {
        var svc = NewService();
        var input = new ApplicationVersionInput(
            Id: null, Key: "bc28", Name: "BC 28", Application: "28", Runtime: "16", Deprecated: false);

        await svc.SaveAsync(new[] { input });

        await using var ctx = _db.NewContext();
        var row = await ctx.ApplicationVersions.AsNoTracking().SingleAsync();
        row.Application.Should().Be("28.0.0.0");
        row.Runtime.Should().Be("16.0");
    }

    [Fact]
    public async Task SaveAsync_with_duplicate_keys_emits_duplicate_key_error()
    {
        var svc = NewService();
        var inputs = new[]
        {
            new ApplicationVersionInput(null, "bc24", "BC 24", "24.0.0.0", "15.0", false),
            new ApplicationVersionInput(null, "bc24", "BC 24 again", "24.0.0.0", "15.0", false),
        };

        var ex = (await svc.Invoking(s => s.SaveAsync(inputs))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[1].Key");
    }

    [Fact]
    public async Task SaveAsync_with_invalid_key_pattern_keys_the_error()
    {
        var svc = NewService();
        var input = new ApplicationVersionInput(
            Id: null, Key: "BC 24!", Name: "BC 24", Application: "24.0.0.0", Runtime: "15.0", Deprecated: false);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[0].Key");
    }

    [Fact]
    public async Task SaveAsync_blocks_removing_a_row_still_referenced_by_an_active_template()
    {
        var svc = NewService();
        await svc.SaveAsync(new[]
        {
            new ApplicationVersionInput(null, "bc24", "BC 24", "24.0.0.0", "15.0", false),
        });

        int versionId;
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.ApplicationVersions.SingleAsync();
            versionId = row.Id;
            ctx.RuntimeTemplates.Add(new RuntimeTemplate
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "uses-bc24",
                Runtime = "15",
                Name = "Uses BC 24",
                DefaultApplication = "24.0.0.0",
                DefaultPlatform = "15.0",
                DefaultApplicationVersionId = versionId,
                CoreIdRangeFrom = 50000,
                CoreIdRangeTo = 50100,
                ModuleIdRangeStart = 60000,
                ModuleIdRangeSize = 100,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        // Now try to save without the row — should be guarded.
        var ex = (await svc.Invoking(s => s.SaveAsync(Array.Empty<ApplicationVersionInput>()))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Keys.Should().Contain(k => k.StartsWith("InUse.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAllForAdminAsync_excludes_other_org_rows()
    {
        await using (var seed = _db.NewContext())
        {
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "default-bc24", Name = "Default BC 24",
                Application = "24.0.0.0", Runtime = "15.0",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.OtherOrgId,
                Key = "other-bc26", Name = "Other BC 26",
                Application = "26.0.0.0", Runtime = "15.0",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var svc = NewService();
        var rows = await svc.GetAllForAdminAsync();

        rows.Should().HaveCount(1);
        rows[0].Key.Should().Be("default-bc24");
    }

    private ApplicationVersionService NewService()
    {
        var ctx = _db.NewContext();
        return new ApplicationVersionService(
            ctx,
            NullLogger<ApplicationVersionService>.Instance,
            _db.OrgContext);
    }
}
