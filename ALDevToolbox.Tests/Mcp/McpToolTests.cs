using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// Behaviour for the MCP tool wrappers under <see cref="Services.Mcp.Tools"/>.
/// The tools are thin shims over existing services — the tests guard the
/// contract at the MCP boundary: input mapping, error surfacing as
/// <see cref="McpException"/> instead of raw <see cref="PlanValidationException"/>,
/// the inline-base64 ZIP, and the snippet org filter.
/// </summary>
public sealed class McpToolTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly IOptions<McpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new McpOptions());

    public void Dispose() => _db.Dispose();

    // ---- SnippetTools ------------------------------------------------------

    [Fact]
    public async Task SearchSnippets_returns_summaries_for_active_rows_in_caller_org()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Posting Validation Override")
                .WithFile("Codeunit.al", "// override")
                .WithFile("Test.al", "// test"));
            ctx.Snippets.Add(SnippetBuilder.Default("Item Lookup Helper"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = new SnippetTools(new SnippetService(ctx2, NullLogger<SnippetService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx2)), ctx2);

        var rows = await tools.SearchAsync(query: "posting");

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Posting Validation Override");
        rows[0].FileCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSnippet_returns_file_payload_when_visible()
    {
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Sample").WithFile("a.al", "// hello");
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = new SnippetTools(new SnippetService(ctx2, NullLogger<SnippetService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx2)), ctx2);

        var detail = await tools.GetAsync(snippetId);
        detail.Title.Should().Be("Sample");
        detail.Files.Should().HaveCount(1);
        detail.Files[0].Path.Should().Be("a.al");
        detail.Files[0].Content.Should().Be("// hello");
    }

    [Fact]
    public async Task GetSnippet_throws_McpException_for_unknown_id()
    {
        await using var ctx = _db.NewContext();
        var tools = new SnippetTools(new SnippetService(ctx, NullLogger<SnippetService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx)), ctx);

        await FluentActions.Awaiting(() => tools.GetAsync(999_999))
            .Should().ThrowAsync<McpException>();
    }

    // ---- WorkspaceTools ----------------------------------------------------

    [Fact]
    public async Task ListTemplates_excludes_deprecated_by_default()
    {
        await using (var ctx = _db.NewContext())
        {
            var activeTpl = TemplateBuilder.Default("runtime-15");
            activeTpl.IsDefault = true;
            var deprecated = TemplateBuilder.Default("runtime-old");
            deprecated.Deprecated = true;
            ctx.RuntimeTemplates.Add(activeTpl);
            ctx.RuntimeTemplates.Add(deprecated);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var templates = new TemplateService(ctx2, NullLogger<TemplateService>.Instance, _db.OrgContext);
        var modules = new ModuleService(ctx2, NullLogger<ModuleService>.Instance, _db.OrgContext);
        var catalog = new CatalogService(ctx2, NullLogger<CatalogService>.Instance, _db.OrgContext);
        var tools = new WorkspaceTools(templates, modules, catalog, generation: null!, Options);

        var active = await tools.ListTemplatesAsync(includeDeprecated: false);
        active.Select(t => t.Key).Should().Equal("runtime-15");

        var all = await tools.ListTemplatesAsync(includeDeprecated: true);
        all.Select(t => t.Key).Should().BeEquivalentTo(new[] { "runtime-15", "runtime-old" });
    }

    [Fact]
    public async Task GenerateWorkspace_returns_inline_base64_zip_with_sha256()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        var result = await tools.GenerateWorkspaceAsync(new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "Acme Customer",
            ExtensionPrefix: "ACME",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999));

        result.FileName.Should().EndWith(".zip");
        result.ContentBase64.Should().NotBeNullOrEmpty();
        result.SizeBytes.Should().BeGreaterThan(0);
        result.Sha256.Should().HaveLength(64);

        // Round-trip: base64 → bytes → ZipArchive → recognisable entry.
        var bytes = Convert.FromBase64String(result.ContentBase64);
        bytes.Length.Should().Be(result.SizeBytes);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName.EndsWith(".code-workspace"));
    }

    [Fact]
    public async Task GenerateWorkspace_surfaces_PlanValidationException_as_McpException()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        var tools = NewWorkspaceTools(ctx);

        // Empty workspace name fails validation in GenerationService.
        var input = new ProjectPlanInput(
            TemplateKey: "runtime-test",
            WorkspaceName: "   ",
            ExtensionPrefix: "ACME",
            Brief: "Brief",
            Description: "Description",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15",
            CoreIdRangeFrom: 90000,
            CoreIdRangeTo: 90999);

        await FluentActions.Awaiting(() => tools.GenerateWorkspaceAsync(input))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("Validation failed"));
    }

    [Fact]
    public async Task GenerateWorkspace_refuses_payloads_over_the_size_cap()
    {
        await SeedDefaultTemplateAsync();

        var ctx = _db.NewContext();
        // 1-byte cap means every real generation trips the guard.
        var tinyOptions = Microsoft.Extensions.Options.Options.Create(new McpOptions { MaxWorkspaceBytes = 1 });
        var tools = NewWorkspaceTools(ctx, tinyOptions);

        await FluentActions.Awaiting(() => tools.GenerateWorkspaceAsync(new ProjectPlanInput(
                TemplateKey: "runtime-test",
                WorkspaceName: "Acme Customer",
                ExtensionPrefix: "ACME",
                Brief: "Brief",
                Description: "Description",
                ApplicationVersion: "24.0.0.0",
                RuntimeVersion: "15",
                CoreIdRangeFrom: 90000,
                CoreIdRangeTo: 90999)))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("MaxWorkspaceBytes"));
    }

    // ---- ObjectExplorerTools ----------------------------------------------

    private static readonly string OeFixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private async Task<int> SeedDkCoreReleaseAsync()
    {
        await using var ctx = _db.NewContext();
        var importer = new ALDevToolbox.Services.ObjectExplorer.ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new ALDevToolbox.Services.ObjectExplorer.TranslationImportService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.ObjectExplorer.TranslationImportService>.Instance),
            NullLogger<ALDevToolbox.Services.ObjectExplorer.ReleaseImportService>.Instance);
        await using var s1 = File.OpenRead(Path.Combine(OeFixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await importer.ImportReleaseAsync(
            new ALDevToolbox.Services.ObjectExplorer.ReleaseImportRequest(
                Label: "MCP DK Core",
                Kind: "first_party",
                ParentReleaseId: null,
                ApplicationVersionId: null,
                Uploads: new[]
                {
                    new ALDevToolbox.Services.ObjectExplorer.AppFileUpload("dk.app", s1, SourceZipStream: null),
                }));
        return summary.ReleaseId;
    }

    private ObjectExplorerTools NewOeTools(Data.AppDbContext ctx)
    {
        var explorer = new ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService(
            ctx, NullLogger<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService>.Instance);
        return new ObjectExplorerTools(explorer, ctx);
    }

    [Fact]
    public async Task GetObjectOutline_returns_symbol_rows_with_ids_and_line_numbers()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Pick any codeunit in DK Core to outline. We discover the name
        // via the same path the agent would — search_objects.
        var found = await tools.SearchObjectsAsync(releaseId.ToString(), namePattern: "", kind: "codeunit");
        found.Should().NotBeEmpty(because: "DK Core ships at least one codeunit");
        var target = found.First();

        var outline = await tools.GetObjectOutlineAsync(releaseId.ToString(), target.Name, "codeunit");
        outline.Name.Should().Be(target.Name);
        outline.Symbols.Should().NotBeEmpty(
            because: "every imported codeunit has at least one procedure / trigger row");
        outline.Symbols.Should().AllSatisfy(s => s.Id.Should().BeGreaterThan(0));
        outline.Symbols.Should().BeInAscendingOrder(s => s.LineNumber);
    }

    [Fact]
    public async Task GetObjectOutline_throws_McpException_for_unknown_object()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        await FluentActions.Awaiting(() => tools.GetObjectOutlineAsync(
                releaseId.ToString(), "Does Not Exist", "codeunit"))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("search_objects"));
    }

    [Fact]
    public async Task GetProcedureSource_returns_body_slice_by_symbolId()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Pick a body-bearing symbol from any codeunit. We resolve it
        // via the database directly to avoid coupling the test to a
        // specific DK Core procedure name.
        var symbolId = await ctx.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId)
            .Where(s => s.Kind == "procedure" || s.Kind == "trigger"
                     || s.Kind == "internal_procedure" || s.Kind == "local_procedure")
            .Where(s => s.EndLine != null)
            .Select(s => s.Id)
            .FirstOrDefaultAsync();
        symbolId.Should().NotBe(0, because: "the importer should have stamped EndLine on at least one procedure");

        var source = await tools.GetProcedureSourceAsync(releaseId.ToString(), symbolId: symbolId);
        source.SymbolId.Should().Be(symbolId);
        source.Source.Should().NotBeNullOrEmpty();
        source.EndLine.Should().BeGreaterOrEqualTo(source.StartLine);
    }

    [Fact]
    public async Task GetProcedureSource_throws_McpException_when_neither_symbolId_nor_name_supplied()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        await FluentActions.Awaiting(() => tools.GetProcedureSourceAsync(releaseId.ToString()))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("symbolId"));
    }

    [Fact]
    public async Task ListProcedureCalls_returns_outgoing_references_for_a_procedure()
    {
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        // Find a procedure that has at least one outgoing reference
        // stamped (source_symbol_id set). Most procedures in DK Core
        // emit a method_call / field_access into the Base App.
        var symbolId = await ctx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId)
            .Where(r => r.SourceSymbolId != null)
            .Select(r => r.SourceSymbolId!.Value)
            .FirstOrDefaultAsync();
        symbolId.Should().NotBe(0,
            because: "the importer should have stamped source_symbol_id on at least one method_call / field_access row");

        var calls = await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId);
        calls.Should().NotBeEmpty();
        calls.Should().AllSatisfy(c => c.TargetObjectName.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task ListProcedureCalls_falls_back_to_line_range_when_source_symbol_id_is_null_on_legacy_rows()
    {
        // Simulates a release imported before #181 landed: end_line is
        // set on the symbol (so the bound is known), but every
        // reference row from that body has source_symbol_id NULL.
        // The tool should fall back to the (source_object, line range)
        // scan and still return the calls.
        var releaseId = await SeedDkCoreReleaseAsync();
        await using var ctx = _db.NewContext();
        var tools = NewOeTools(ctx);

        var symbolId = await ctx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId && r.SourceSymbolId != null)
            .Select(r => r.SourceSymbolId!.Value)
            .FirstAsync();

        // Strip source_symbol_id on every reference belonging to this
        // procedure to simulate the pre-#181 ingest. Bulk update via
        // ExecuteSqlInterpolated so we don't have to load + track each
        // row individually.
        var affected = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE oe_module_references SET source_symbol_id = NULL WHERE source_symbol_id = {symbolId}");
        affected.Should().BeGreaterThan(0,
            because: "this fixture procedure has stamped rows we just nulled");

        var calls = await tools.ListProcedureCallsAsync(releaseId.ToString(), symbolId: symbolId);
        calls.Should().NotBeEmpty(
            because: "the line-range fallback should recover the same rows once the FK column is empty");
    }

    // ---- helpers -----------------------------------------------------------

    private async Task SeedDefaultTemplateAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-test"));
        await ctx.SaveChangesAsync();
    }

    private WorkspaceTools NewWorkspaceTools(Data.AppDbContext ctx, IOptions<McpOptions>? options = null)
    {
        var templates = new TemplateService(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext);
        var modules = new ModuleService(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext);
        var catalog = new CatalogService(ctx, NullLogger<CatalogService>.Instance, _db.OrgContext);
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        var generation = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            templates,
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
        return new WorkspaceTools(templates, modules, catalog, generation, options ?? Options);
    }
}
