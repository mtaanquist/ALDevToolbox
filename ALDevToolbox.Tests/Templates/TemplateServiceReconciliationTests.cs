using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Exercises <see cref="TemplateService.UpdateAsync"/> end-to-end: every code
/// path inside ReconcileFolders / ReconcileFiles / ReconcileModuleFolders /
/// ReconcileDefaultModules has a test. These reconcilers mutate the persisted
/// graph in place so unchanged rows keep stable primary keys, which keeps the
/// audit log tight — a regression here is invisible without a test.
/// </summary>
public sealed class TemplateServiceReconciliationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Update_reorders_folders_in_place_without_replacing_primary_keys()
    {
        int templateId;
        int folderAId, folderBId;

        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-reorder")
                .WithCoreFolder("Source", ("A.al", "// a"))
                .WithCoreFolder("Test", ("B.al", "// b"));
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
            folderAId = template.Folders.Single(f => f.Path == "Source").Id;
            folderBId = template.Folders.Single(f => f.Path == "Test").Id;
        }

        // Swap the order; both folders still present, same paths, new ordering.
        var input = TemplateInputFor(
            "runtime-reorder",
            new TemplateFolderInput("Test", new[] { new TemplateFileInput("B.al", "// b") }),
            new TemplateFolderInput("Source", new[] { new TemplateFileInput("A.al", "// a") }));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var folders = await verify.RuntimeTemplates
                .Where(t => t.Id == templateId)
                .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .Select(t => t.Folders.ToList())
                .SingleAsync();

            folders.Select(f => f.Path).Should().Equal("Test", "Source");
            // Reconciler keeps the same primary keys — it mutates row[0] to
            // become "Test" and row[1] to become "Source". The audit log relies
            // on this so an unchanged update doesn't churn rows.
            folders[0].Id.Should().Be(folderAId);
            folders[1].Id.Should().Be(folderBId);
        }
    }

    [Fact]
    public async Task Update_removes_folders_dropped_from_the_input_list()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-shrink")
                .WithCoreFolder("Source", ("A.al", "// a"))
                .WithCoreFolder("Test", ("B.al", "// b"));
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
        }

        var input = TemplateInputFor(
            "runtime-shrink",
            new TemplateFolderInput("Source", new[] { new TemplateFileInput("A.al", "// a") }));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var folders = await verify.TemplateFolders
                .Where(f => f.TemplateId == templateId)
                .ToListAsync();
            folders.Should().HaveCount(1);
            folders[0].Path.Should().Be("Source");
        }
    }

    [Fact]
    public async Task Update_appends_new_folders_at_the_end_of_the_input_list()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-grow")
                .WithCoreFolder("Source", ("A.al", "// a"));
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
        }

        var input = TemplateInputFor(
            "runtime-grow",
            new TemplateFolderInput("Source", new[] { new TemplateFileInput("A.al", "// a") }),
            new TemplateFolderInput("Test", new[] { new TemplateFileInput("B.al", "// b") }));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var folders = await verify.TemplateFolders
                .Where(f => f.TemplateId == templateId)
                .OrderBy(f => f.Ordering)
                .ToListAsync();
            folders.Select(f => f.Path).Should().Equal("Source", "Test");
            folders[1].Ordering.Should().Be(1);
        }
    }

    [Fact]
    public async Task Update_persists_file_content_changes_in_place()
    {
        int templateId, fileId;
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-edit-file")
                .WithCoreFolder("Source", ("Sample.al", "// original"));
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
            fileId = template.Folders.Single().Files.Single().Id;
        }

        var input = TemplateInputFor(
            "runtime-edit-file",
            new TemplateFolderInput("Source", new[] { new TemplateFileInput("Sample.al", "// edited") }));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var file = await verify.TemplateFiles.SingleAsync(f => f.Id == fileId);
            file.Content.Should().Be("// edited");
        }
    }

    [Fact]
    public async Task Update_appends_a_new_default_module_to_the_template()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            ctx.Modules.AddRange(
                ModuleBuilder.Default("alpha"),
                ModuleBuilder.Default("bravo"));
            var template = TemplateBuilder.Default("runtime-defaults-grow");
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;

            var alphaId = ctx.Modules.Single(m => m.Key == "alpha").Id;
            ctx.Set<RuntimeTemplateDefaultModule>().Add(
                new RuntimeTemplateDefaultModule
                {
                    OrganizationId = template.OrganizationId,
                    RuntimeTemplateId = templateId,
                    ModuleId = alphaId,
                    Ordering = 0,
                });
            await ctx.SaveChangesAsync();
        }

        var input = TemplateInputFor(
            "runtime-defaults-grow",
            defaultModuleKeys: new[] { "alpha", "bravo" });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var defaults = await verify.Set<RuntimeTemplateDefaultModule>()
                .Where(d => d.RuntimeTemplateId == templateId)
                .Include(d => d.Module!)
                .OrderBy(d => d.Ordering)
                .ToListAsync();
            defaults.Select(d => d.Module!.Key).Should().Equal("alpha", "bravo");
            defaults.Select(d => d.Ordering).Should().Equal(0, 1);
        }
    }

    [Fact]
    public async Task Update_removes_a_default_module_dropped_from_the_input_list()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            ctx.Modules.AddRange(
                ModuleBuilder.Default("alpha"),
                ModuleBuilder.Default("bravo"));
            var template = TemplateBuilder.Default("runtime-defaults-shrink");
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;

            var alphaId = ctx.Modules.Single(m => m.Key == "alpha").Id;
            var bravoId = ctx.Modules.Single(m => m.Key == "bravo").Id;
            ctx.Set<RuntimeTemplateDefaultModule>().AddRange(
                new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, RuntimeTemplateId = templateId, ModuleId = alphaId, Ordering = 0 },
                new RuntimeTemplateDefaultModule { OrganizationId = template.OrganizationId, RuntimeTemplateId = templateId, ModuleId = bravoId, Ordering = 1 });
            await ctx.SaveChangesAsync();
        }

        var input = TemplateInputFor(
            "runtime-defaults-shrink",
            defaultModuleKeys: new[] { "alpha" });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var defaults = await verify.Set<RuntimeTemplateDefaultModule>()
                .Where(d => d.RuntimeTemplateId == templateId)
                .Include(d => d.Module!)
                .ToListAsync();
            defaults.Should().ContainSingle();
            defaults[0].Module!.Key.Should().Be("alpha");
        }
    }

    [Fact]
    public async Task Update_rejects_a_default_module_key_that_does_not_resolve_to_an_active_module()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-missing-dep");
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
        }

        var input = TemplateInputFor(
            "runtime-missing-dep",
            defaultModuleKeys: new[] { "no-such-module" });

        await using var verify = _db.NewContext();
        var act = () => NewService(verify).UpdateAsync(templateId, input);
        await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>();
    }

    [Fact]
    public async Task Update_keeps_the_immutable_key_even_when_input_supplies_a_different_one()
    {
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-immutable-key");
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
        }

        // Supply a different (otherwise-valid) key. UpdateAsync should ignore it.
        var input = TemplateInputFor("totally-different-key");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(templateId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var key = await verify.RuntimeTemplates
                .Where(t => t.Id == templateId)
                .Select(t => t.Key)
                .SingleAsync();
            key.Should().Be("runtime-immutable-key");
        }
    }

    private TemplateService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext);

    private static TemplateInput TemplateInputFor(
        string key,
        params TemplateFolderInput[] folders) =>
        TemplateInputFor(key, folders, Array.Empty<string>());

    private static TemplateInput TemplateInputFor(
        string key,
        IReadOnlyList<string> defaultModuleKeys) =>
        TemplateInputFor(key, Array.Empty<TemplateFolderInput>(), defaultModuleKeys);

    private static TemplateInput TemplateInputFor(
        string key,
        IReadOnlyList<TemplateFolderInput> folders,
        IReadOnlyList<string> defaultModuleKeys) =>
        new(
            Key: key,
            Runtime: "15",
            Name: "Test Runtime",
            Description: "Synthetic template used in tests.",
            DefaultApplication: "24.0.0.0",
            DefaultPlatform: "1.0.0.0",
            DefaultsJson: "{}",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999,
            ModuleIdRangeStart: 91000,
            ModuleIdRangeSize: 200,
            Deprecated: false,
            DefaultModuleKeys: defaultModuleKeys,
            Folders: folders,
            ModuleFolders: Array.Empty<TemplateFolderInput>(),
            CodeWorkspaceContent: GenerationService.DefaultCodeWorkspaceContent);
}
