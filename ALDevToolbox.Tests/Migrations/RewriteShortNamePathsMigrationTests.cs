using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the <c>{{short_name}}</c> to <c>{{workspace_folder}}</c> rewrite. The
/// fixture's MigrateAsync already ran it against an empty database (a no-op),
/// so this test seeds rows in the shape an organisation that forked the
/// canonical content would have and replays
/// <see cref="RewriteShortNamePaths.RewriteSql"/>.
///
/// <para>The load-bearing part is what it leaves alone: a file <em>path</em> is
/// always a path, but the same variable inside a file's content can be prose
/// ("Customizations made for {{short_name}}"), and both readings of that are
/// what the author meant. See <c>.design/customer-naming.md</c>.</para>
/// </summary>
public sealed class RewriteShortNamePathsMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Replaying_the_rewrite_fixes_paths_and_leaves_prose_alone()
    {
        const int org = TestDb.DefaultOrgId;
        int fileId, templateId;

        await using (var seed = _db.NewContext())
        {
            // The .code-workspace file is two layers - the organisation's base
            // JSON and the per-template overlay merged on top - so both carry
            // the same folder paths and both have to be rewritten.
            var template = TemplateBuilder.Default();
            template.CodeWorkspaceJson = """{ "folders": [ { "path": "{{short_name}}/Core" } ] }""";
            seed.RuntimeTemplates.Add(template);
            seed.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = org,
                CodeWorkspaceJson = """{ "folders": [ { "path": "{{shortName}}" } ] }""",
                UpdatedAt = DateTime.UtcNow,
            });
            var file = new OrganizationFile
            {
                OrganizationId = org,
                Path = "{{short_name}}/README.md",
                Content = "Customizations made for {{short_name}}.",
                MustacheEnabled = true,
                Scope = OrganizationFileScope.WorkspaceRoot,
                Ordering = 5000,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OrganizationFiles.Add(file);
            await seed.SaveChangesAsync();
            fileId = file.Id;
            templateId = template.Id;
        }

        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync(RewriteShortNamePaths.RewriteSql);
            // A second run must change nothing more (idempotency).
            await run.Database.ExecuteSqlRawAsync(RewriteShortNamePaths.RewriteSql);
        }

        await using var read = _db.NewContext();

        var settings = await read.OrganizationSettings.AsNoTracking()
            .SingleAsync(s => s.OrganizationId == org);
        settings.CodeWorkspaceJson.Should().Be("""{ "folders": [ { "path": "{{workspace_folder}}" } ] }""");

        var overlay = await read.RuntimeTemplates.AsNoTracking().SingleAsync(t => t.Id == templateId);
        overlay.CodeWorkspaceJson.Should().Be(
            """{ "folders": [ { "path": "{{workspace_folder}}/Core" } ] }""",
            "the overlay names the same folders the base layer does");

        var rewritten = await read.OrganizationFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
        rewritten.Path.Should().Be("{{workspace_folder}}/README.md", "a path can only ever have meant the folder");
        rewritten.Content.Should().Be(
            "Customizations made for {{short_name}}.",
            "prose about the customer reads correctly either way, so it is left as written");
    }
}
