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
/// the inline-base64 ZIP, and the cookbook org filter.
/// </summary>
public sealed class McpToolTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly IOptions<McpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new McpOptions());

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedSubmitterAsync(int userId = 800)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"mcp{userId}@example.com",
            PasswordHash = "x",
            DisplayName = "MCP Submitter",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = userId;
        return userId;
    }

    // ---- CookbookTools -----------------------------------------------------

    [Fact]
    public async Task SearchRecipes_returns_summaries_for_active_rows_in_caller_org()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Recipes.Add(RecipeBuilder.Default("Posting Validation Override", type: RecipeType.Pattern)
                .WithFile("Codeunit.al", "// override")
                .WithFile("Test.al", "// test"));
            ctx.Recipes.Add(RecipeBuilder.Default("Item Lookup Helper"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var rows = await tools.SearchAsync(query: "posting");

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Posting Validation Override");
        rows[0].Type.Should().Be("Pattern");
        rows[0].FileCount.Should().Be(2);
    }

    [Fact]
    public async Task GetRecipe_returns_file_payload_with_relative_path_joined()
    {
        int recipeId;
        await using (var ctx = _db.NewContext())
        {
            var s = RecipeBuilder.Default("Sample")
                .WithFile("a.al", "// hello")
                .WithFile("b.al", "// nested", relativePath: "sub");
            ctx.Recipes.Add(s);
            await ctx.SaveChangesAsync();
            recipeId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var tools = NewCookbookTools(ctx2);

        var detail = await tools.GetAsync(recipeId);
        detail.Title.Should().Be("Sample");
        detail.Type.Should().Be("Snippet");
        detail.Files.Should().HaveCount(2);
        detail.Files.Select(f => f.Path).Should().Contain(new[] { "a.al", "sub/b.al" });
    }

    [Fact]
    public async Task GetRecipe_throws_McpException_for_unknown_id()
    {
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);

        await FluentActions.Awaiting(() => tools.GetAsync(999_999))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task GetCookbookGuidance_returns_org_text_builtin_type_descriptions_and_a_token()
    {
        // No row yet — built-in copy still surfaces, and we still get a token.
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var defaults = await tools.GetCookbookGuidanceAsync();
            defaults.Guidance.Should().BeEmpty();
            defaults.RecipeTypes.Should().BeEquivalentTo(new[] { "Snippet", "Pattern", "Module" });
            defaults.TypeDescriptions.Should().ContainKey("Pattern");
            defaults.GuidanceToken.Should().NotBeNullOrWhiteSpace(
                "the write tools require a token from this call");
            defaults.GuidanceTokenExpiresInSeconds.Should().BeGreaterThan(0);
        }

        // Saved org guidance comes back verbatim.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "Acme",
                DefaultIdRangeFrom = 50_000,
                DefaultIdRangeTo = 50_999,
                CookbookGuidance = "Use the **ACME** prefix on every object name.",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var withSettings = NewCookbookTools(ctx2);
        var result = await withSettings.GetCookbookGuidanceAsync();
        result.Guidance.Should().Be("Use the **ACME** prefix on every object name.");
    }

    [Fact]
    public async Task SuggestRecipe_creates_pending_row_in_caller_org_when_token_is_valid()
    {
        var userId = await SeedSubmitterAsync();

        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Posting Validation Override",
            Description: "Stub override for posting validation.",
            Keywords: "posting, validation",
            Type: "Pattern",
            Files: new[] { new RecipeFileInputDto("Codeunit.al", "// body", "src") },
            Instructions: "Drop into your project and rename the codeunit.");

        var result = await tools.SuggestAsync(input);

        result.SuggestionId.Should().BeGreaterThan(0);
        result.Message.Should().Contain("/admin/cookbook/suggestions");

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions
            .Include(s => s.Files)
            .FirstAsync(s => s.Id == result.SuggestionId);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Type.Should().Be(RecipeType.Pattern);
        row.OrganizationId.Should().Be(_db.OrgContext.CurrentOrganizationId);
        row.Title.Should().Be("Posting Validation Override");
        row.Files.Should().ContainSingle();
        row.Files[0].FileName.Should().Be("Codeunit.al");
        row.Files[0].RelativePath.Should().Be("src");
        row.Files[0].Content.Should().Be("// body");
        row.SuggestedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_is_missing()
    {
        await SeedSubmitterAsync(userId: 820);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);

        var input = new SuggestRecipeInput(
            GuidanceToken: "",
            Title: "No Token",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("get_cookbook_guidance",
            "the error must name the recovery action so the agent knows what to call next");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_is_tampered()
    {
        await SeedSubmitterAsync(userId: 821);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var realToken = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        // Flip the last character; signed payload must reject.
        var bad = realToken[..^1] + (realToken[^1] == 'A' ? 'B' : 'A');

        var input = new SuggestRecipeInput(
            GuidanceToken: bad,
            Title: "Tampered",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task SuggestRecipe_rejects_when_guidance_token_has_expired()
    {
        await SeedSubmitterAsync(userId: 822);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        // Advance past the token's lifetime.
        _clock.Advance(CookbookTools.GuidanceTokenLifetime + TimeSpan.FromSeconds(1));

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Stale",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_token_issued_for_a_different_organisation()
    {
        await SeedSubmitterAsync(userId: 823);

        // Mint a token under OtherOrgId, then switch back to DefaultOrgId
        // and try to submit. The token's signed org id no longer matches.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        string otherOrgToken;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            otherOrgToken = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        await using var ctx2 = _db.NewContext();
        var defaultTools = NewCookbookTools(ctx2);
        var input = new SuggestRecipeInput(
            GuidanceToken: otherOrgToken,
            Title: "Wrong Org Token",
            Description: "Body.",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        var ex = await FluentActions.Awaiting(() => defaultTools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("different organisation");
    }

    [Fact]
    public async Task SuggestRecipe_returns_McpException_when_title_blank()
    {
        await SeedSubmitterAsync(userId: 801);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "",
            Description: "Anything",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        (await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Title");
    }

    [Fact]
    public async Task SuggestRecipe_rejects_files_with_slashes_in_filename()
    {
        await SeedSubmitterAsync(userId: 802);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Has Slash",
            Description: "Description",
            Keywords: "",
            Type: "Snippet",
            Files: new[] { new RecipeFileInputDto("sub/x.al", "// nope") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task SuggestRecipe_rejects_unknown_type_string()
    {
        await SeedSubmitterAsync(userId: 810);
        await using var ctx = _db.NewContext();
        var tools = NewCookbookTools(ctx);
        var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;

        var input = new SuggestRecipeInput(
            GuidanceToken: token,
            Title: "Bad Type",
            Description: "Body.",
            Keywords: "",
            Type: "Megamodule",
            Files: new[] { new RecipeFileInputDto("a.al", "// hi") });

        await FluentActions.Awaiting(() => tools.SuggestAsync(input))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_round_trips_the_change()
    {
        await SeedSubmitterAsync(userId: 803);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "First Draft",
                Description: "Initial body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v1") }));
            suggestionId = created.SuggestionId;
        }

        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var result = await tools.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: token,
                Title: "Revised Draft",
                Description: "Tightened body.",
                Keywords: "alpha",
                Type: "Pattern",
                Files: new[]
                {
                    new RecipeFileInputDto("a.al", "// v2"),
                    new RecipeFileInputDto("b.al", "// added"),
                }));
            result.SuggestionId.Should().Be(suggestionId);
            result.Message.Should().Contain("Pending");
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeSuggestions
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == suggestionId);
        row.Title.Should().Be("Revised Draft");
        row.Description.Should().Be("Tightened body.");
        row.Type.Should().Be(RecipeType.Pattern);
        row.Decision.Should().Be(RecipeSuggestionDecision.Pending);
        row.Files.Select(f => f.FileName).Should().Equal("a.al", "b.al");
        row.Files[0].Content.Should().Be("// v2");
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_also_rejects_a_missing_guidance_token()
    {
        await SeedSubmitterAsync(userId: 830);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "First Draft",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v1") }));
            suggestionId = created.SuggestionId;
        }

        await using var ctx2 = _db.NewContext();
        var tools2 = NewCookbookTools(ctx2);
        await FluentActions.Awaiting(() => tools2.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: "",
                Title: "Revised",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// v2") })))
            .Should().ThrowAsync<McpException>()
            .Where(ex => ex.Message.Contains("get_cookbook_guidance"));
    }

    [Fact]
    public async Task UpdateRecipeSuggestion_returns_McpException_when_caller_is_not_submitter()
    {
        await SeedSubmitterAsync(userId: 804);

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            var tools = NewCookbookTools(ctx);
            var token = (await tools.GetCookbookGuidanceAsync()).GuidanceToken;
            var created = await tools.SuggestAsync(new SuggestRecipeInput(
                GuidanceToken: token,
                Title: "Owned",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// hi") }));
            suggestionId = created.SuggestionId;
        }

        await SeedSubmitterAsync(userId: 805);

        await using var ctx2 = _db.NewContext();
        var tools2 = NewCookbookTools(ctx2);
        var token2 = (await tools2.GetCookbookGuidanceAsync()).GuidanceToken;

        await FluentActions.Awaiting(() => tools2.UpdateSuggestionAsync(new UpdateRecipeSuggestionInput(
                SuggestionId: suggestionId,
                GuidanceToken: token2,
                Title: "Hijacked",
                Description: "Body.",
                Keywords: "",
                Type: "Snippet",
                Files: new[] { new RecipeFileInputDto("a.al", "// hi") })))
            .Should().ThrowAsync<McpException>();
    }

    /// <summary>
    /// Mutable clock that lets the expiry test fast-forward past
    /// <see cref="CookbookTools.GuidanceTokenLifetime"/> without sleeping.
    /// </summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));

    private CookbookTools NewCookbookTools(ALDevToolbox.Data.AppDbContext ctx) =>
        new(
            new RecipeService(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx)),
            ctx,
            new RecipeSuggestionService(ctx, NullLogger<RecipeSuggestionService>.Instance, _db.OrgContext),
            _db.OrgContext,
            _db.DataProtectionProvider,
            _clock);

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
