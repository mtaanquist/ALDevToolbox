using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Snippets;

/// <summary>
/// Coverage for the Instructions + MinimumApplicationVersion fields added in
/// the snippets-metadata change: round-trip on create/update, validation of
/// missing / soft-deleted application versions, and that
/// <see cref="SnippetService.GetAsync"/> eagerly loads the navigation entity
/// so the badge can render without an extra round-trip.
/// </summary>
public sealed class SnippetMetadataTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<ApplicationVersion> SeedApplicationVersionAsync(
        string key = "bc28",
        string name = "BC 2026 Wave 1",
        string app = "28.0.0.0",
        string runtime = "28.0",
        bool deprecated = false,
        bool deleted = false)
    {
        await using var ctx = _db.NewContext();
        var row = new ApplicationVersion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Key = key,
            Name = name,
            Application = app,
            Runtime = runtime,
            Ordering = 0,
            Deprecated = deprecated,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        ctx.ApplicationVersions.Add(row);
        await ctx.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Create_persists_instructions_and_minimum_application_version()
    {
        var version = await SeedApplicationVersionAsync();

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var snippet = await svc.CreateAsync(new SnippetInput(
            Title: "With metadata",
            Description: "Carries the new fields.",
            Keywords: "meta",
            Deprecated: false,
            Files: new[] { new SnippetFileInput("A.al", "// a") },
            Instructions: "  ## Setup\n\nDrop into `src/`.  ",
            MinimumApplicationVersionId: version.Id));

        await using var verify = _db.NewContext();
        var persisted = await verify.Snippets
            .Include(s => s.MinimumApplicationVersion)
            .SingleAsync(s => s.Id == snippet.Id);
        persisted.Instructions.Should().Be("## Setup\n\nDrop into `src/`.",
            "instructions are trimmed but otherwise preserved verbatim");
        persisted.MinimumApplicationVersionId.Should().Be(version.Id);
        persisted.MinimumApplicationVersion!.Application.Should().Be("28.0.0.0");
    }

    [Fact]
    public async Task Create_treats_whitespace_instructions_as_null()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var snippet = await svc.CreateAsync(new SnippetInput(
            "Empty instructions", "Body.", "", false,
            new[] { new SnippetFileInput("A.al", "// a") },
            Instructions: "   \n  ",
            MinimumApplicationVersionId: null));

        await using var verify = _db.NewContext();
        (await verify.Snippets.SingleAsync(s => s.Id == snippet.Id))
            .Instructions.Should().BeNull();
    }

    [Fact]
    public async Task Update_round_trips_metadata_and_clears_it_back_to_null()
    {
        var version = await SeedApplicationVersionAsync();
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Round Trip").WithFile("A.al", "// a");
            s.Instructions = "first version";
            s.MinimumApplicationVersionId = version.Id;
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        // Clear both fields back to null.
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(snippetId, new SnippetInput(
                "Round Trip", "Body.", "", false,
                new[] { new SnippetFileInput("A.al", "// a") },
                Instructions: null,
                MinimumApplicationVersionId: null));
        }

        await using var verify = _db.NewContext();
        var row = await verify.Snippets.SingleAsync(s => s.Id == snippetId);
        row.Instructions.Should().BeNull();
        row.MinimumApplicationVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Create_rejects_oversized_instructions_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var oversized = new string('x', SnippetService.MaxInstructionsLength + 1);

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new SnippetInput(
                "Too long", "Body.", "", false,
                new[] { new SnippetFileInput("A.al", "// a") },
                Instructions: oversized)));

        ex.Errors.Should().ContainKey("Instructions");
    }

    [Fact]
    public async Task Create_rejects_unknown_minimum_application_version_id()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new SnippetInput(
                "Bad FK", "Body.", "", false,
                new[] { new SnippetFileInput("A.al", "// a") },
                MinimumApplicationVersionId: 9999)));

        ex.Errors.Should().ContainKey("MinimumApplicationVersionId");
    }

    [Fact]
    public async Task Create_rejects_soft_deleted_minimum_application_version()
    {
        var deleted = await SeedApplicationVersionAsync(deleted: true);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            svc.CreateAsync(new SnippetInput(
                "Soft-deleted FK", "Body.", "", false,
                new[] { new SnippetFileInput("A.al", "// a") },
                MinimumApplicationVersionId: deleted.Id)));

        ex.Errors.Should().ContainKey("MinimumApplicationVersionId");
    }

    [Fact]
    public async Task Create_accepts_deprecated_minimum_application_version()
    {
        // Deprecated rows must stay assignable so an existing snippet doesn't
        // break when admins clean up the catalogue.
        var deprecated = await SeedApplicationVersionAsync(deprecated: true);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var snippet = await svc.CreateAsync(new SnippetInput(
            "Deprecated FK is fine", "Body.", "", false,
            new[] { new SnippetFileInput("A.al", "// a") },
            MinimumApplicationVersionId: deprecated.Id));

        snippet.MinimumApplicationVersionId.Should().Be(deprecated.Id);
    }

    [Fact]
    public async Task Get_eagerly_loads_minimum_application_version()
    {
        var version = await SeedApplicationVersionAsync();
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Eager").WithFile("A.al", "// a");
            s.MinimumApplicationVersionId = version.Id;
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        await using var ctx2 = _db.NewContext();
        var loaded = await NewService(ctx2).GetAsync(snippetId);
        loaded.Should().NotBeNull();
        loaded!.MinimumApplicationVersion.Should().NotBeNull(
            "GetAsync includes the navigation so the badge can render without N+1");
        loaded.MinimumApplicationVersion!.Name.Should().Be("BC 2026 Wave 1");
    }

    private SnippetService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<SnippetService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx));
}
