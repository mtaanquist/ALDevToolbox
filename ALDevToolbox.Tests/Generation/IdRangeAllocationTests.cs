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
/// Covers <c>GenerationService.AssignModuleRanges</c>: contiguous slices, the
/// per-module size override, and Core's id range passing through verbatim.
/// Tests run end-to-end via <c>GenerateWorkspaceAsync</c> and inspect the
/// resulting <c>app.json</c> files in the ZIP, so a future refactor of the
/// private helper can't slip past silently.
/// </summary>
public sealed class IdRangeAllocationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Modules_get_contiguous_id_ranges_starting_at_template_start()
    {
        await SeedAsync(
            template => template,
            ModuleBuilder.Default("alpha", "Alpha"),
            ModuleBuilder.Default("beta", "Beta"),
            ModuleBuilder.Default("gamma", "Gamma"));

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(selectedModules: new[] { "alpha", "beta", "gamma" });

        using var archive = await ReadArchiveAsync(service, plan);

        // Default ModuleIdRangeStart=91000, ModuleIdRangeSize=200 ⇒
        // alpha [91000..91199], beta [91200..91399], gamma [91400..91599].
        ReadIdRange(archive, "AcmeCustomer/Alpha/app.json").Should().Be((91000, 91199));
        ReadIdRange(archive, "AcmeCustomer/Beta/app.json").Should().Be((91200, 91399));
        ReadIdRange(archive, "AcmeCustomer/Gamma/app.json").Should().Be((91400, 91599));
    }

    [Fact]
    public async Task Per_module_size_override_consumes_only_its_own_slice()
    {
        await SeedAsync(
            template => template,
            ModuleBuilder.Default("alpha", "Alpha"),
            ModuleBuilder.Default("wide", "Wide", idRangeSize: 500),
            ModuleBuilder.Default("gamma", "Gamma"));

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(selectedModules: new[] { "alpha", "wide", "gamma" });

        using var archive = await ReadArchiveAsync(service, plan);

        // alpha 91000..91199 (default 200), wide 91200..91699 (override 500),
        // gamma 91700..91899 (back to default 200). The next-start cursor must
        // walk past the override correctly or gamma collides with wide.
        ReadIdRange(archive, "AcmeCustomer/Alpha/app.json").Should().Be((91000, 91199));
        ReadIdRange(archive, "AcmeCustomer/Wide/app.json").Should().Be((91200, 91699));
        ReadIdRange(archive, "AcmeCustomer/Gamma/app.json").Should().Be((91700, 91899));
    }

    [Fact]
    public async Task Core_id_range_passes_through_from_plan()
    {
        await SeedAsync(template => template);

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(coreFrom: 75000, coreTo: 75500);

        using var archive = await ReadArchiveAsync(service, plan);

        ReadIdRange(archive, "AcmeCustomer/Core/app.json").Should().Be((75000, 75500));
    }

    [Fact]
    public async Task Modules_preserve_user_selected_ordering()
    {
        await SeedAsync(
            template => template,
            ModuleBuilder.Default("alpha", "Alpha"),
            ModuleBuilder.Default("beta", "Beta"));

        var service = NewService();
        var plan = PlanBuilder.WorkspacePlan(selectedModules: new[] { "beta", "alpha" });

        using var archive = await ReadArchiveAsync(service, plan);

        // The user picked beta first, so beta gets 91000..91199, not alpha.
        ReadIdRange(archive, "AcmeCustomer/Beta/app.json").Should().Be((91000, 91199));
        ReadIdRange(archive, "AcmeCustomer/Alpha/app.json").Should().Be((91200, 91399));
    }

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        var config = new WorkspaceConfigService(ctx);
        return new GenerationService(
            ctx,
            config,
            _db.NewOrganizationConfigService(ctx),
            _db.OrgContext,
            NullLogger<GenerationService>.Instance);
    }

    private async Task SeedAsync(Func<RuntimeTemplate, RuntimeTemplate> shapeTemplate, params Module[] modules)
    {
        await using var ctx = _db.NewContext();
        var template = shapeTemplate(TemplateBuilder.Default());
        ctx.RuntimeTemplates.Add(template);
        ctx.Modules.AddRange(modules);
        await ctx.SaveChangesAsync();
    }

    private static async Task<ZipArchive> ReadArchiveAsync(GenerationService service, ProjectPlan plan)
    {
        var archive = await service.GenerateWorkspaceAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static (int From, int To) ReadIdRange(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"ZIP entry '{entryPath}' not found.");
        using var reader = new StreamReader(entry.Open());
        var doc = JsonDocument.Parse(reader.ReadToEnd());
        var range = doc.RootElement.GetProperty("idRanges")[0];
        return (range.GetProperty("from").GetInt32(), range.GetProperty("to").GetInt32());
    }
}
