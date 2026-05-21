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
/// Approval workflow for <see cref="SnippetSuggestionService"/>: submit creates
/// a pending row, approve promotes it to a real <see cref="Snippet"/>
/// atomically with the decision columns, reject closes it with an optional
/// note. The title-clash guard on approval prevents a duplicate snippet
/// landing if another suggestion was approved with the same title in between.
/// </summary>
public sealed class SnippetSuggestionWorkflowTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedUserAsync(int userId = 700, string display = "Submitter", UserRole role = UserRole.User)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{userId}@example.com",
            PasswordHash = "x",
            DisplayName = display,
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Submit_creates_pending_suggestion_with_files()
    {
        var user = await SeedUserAsync();
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        var svc = NewSuggestionService(ctx);
        var id = await svc.SubmitAsync(new SnippetSuggestionInput(
            Title: "Idea",
            Description: "A useful pattern.",
            Keywords: "alpha beta",
            Files: new[]
            {
                new SnippetFileInput("Sub.Codeunit.al", "// sub"),
                new SnippetFileInput("Ext.PageExt.al", "// ext"),
            }));

        await using var read = _db.NewContext();
        var row = await read.SnippetSuggestions
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == id);
        row.Decision.Should().Be(SnippetSuggestionDecision.Pending);
        row.SuggestedByUserId.Should().Be(user.Id);
        row.DecidedAt.Should().BeNull();
        row.ApprovedSnippetId.Should().BeNull();
        row.Files.Select(f => f.FileName).Should().Equal("Sub.Codeunit.al", "Ext.PageExt.al");
    }

    [Fact]
    public async Task Submit_rejects_when_no_files_supplied()
    {
        var user = await SeedUserAsync(userId: 701);
        _db.OrgContext.CurrentUserId = user.Id;

        await using var ctx = _db.NewContext();
        var svc = NewSuggestionService(ctx);
        Func<Task> act = () => svc.SubmitAsync(new SnippetSuggestionInput(
            "Idea", "Body.", "", Array.Empty<SnippetFileInput>()));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Files");
    }

    [Fact]
    public async Task Approve_promotes_suggestion_to_snippet_and_stamps_decision()
    {
        var user = await SeedUserAsync(userId: 702);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new SnippetSuggestionInput(
                Title: "Approve Me",
                Description: "Pending body.",
                Keywords: "approve",
                Files: new[] { new SnippetFileInput("Sample.al", "// content") }));
        }

        var admin = await SeedUserAsync(userId: 703, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        Snippet snippet;
        await using (var ctx = _db.NewContext())
        {
            snippet = await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var verify = _db.NewContext();
        var persistedSnippet = await verify.Snippets
            .Include(s => s.Files)
            .SingleAsync(s => s.Id == snippet.Id);
        persistedSnippet.Title.Should().Be("Approve Me");
        persistedSnippet.Files.Should().ContainSingle(f => f.FileName == "Sample.al" && f.Content == "// content");

        var persistedSuggestion = await verify.SnippetSuggestions.SingleAsync(s => s.Id == suggestionId);
        persistedSuggestion.Decision.Should().Be(SnippetSuggestionDecision.Approved);
        persistedSuggestion.ApprovedSnippetId.Should().Be(snippet.Id);
        persistedSuggestion.DecidedByUserId.Should().Be(admin.Id);
        persistedSuggestion.DecidedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Approve_refuses_when_title_already_taken_by_existing_snippet()
    {
        var user = await SeedUserAsync(userId: 704);
        _db.OrgContext.CurrentUserId = user.Id;

        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Taken").WithFile("A.al", "// existing"));
            await ctx.SaveChangesAsync();
        }

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new SnippetSuggestionInput(
                Title: "Taken",
                Description: "Same name, different body.",
                Keywords: "",
                Files: new[] { new SnippetFileInput("B.al", "// new") }));
        }

        var admin = await SeedUserAsync(userId: 705, display: "Admin", role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).ApproveAsync(suggestionId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");

        // Suggestion remains pending — no snippet was created from it.
        await using var verify = _db.NewContext();
        (await verify.SnippetSuggestions.SingleAsync(s => s.Id == suggestionId))
            .Decision.Should().Be(SnippetSuggestionDecision.Pending);
    }

    [Fact]
    public async Task Reject_records_decision_and_optional_note()
    {
        var user = await SeedUserAsync(userId: 706);
        _db.OrgContext.CurrentUserId = user.Id;

        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new SnippetSuggestionInput(
                "Drop Me", "Body.", "", new[] { new SnippetFileInput("A.al", "// a") }));
        }

        var admin = await SeedUserAsync(userId: 707, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).RejectAsync(suggestionId, " Too narrow. ");
        }

        await using var verify = _db.NewContext();
        var row = await verify.SnippetSuggestions.SingleAsync(s => s.Id == suggestionId);
        row.Decision.Should().Be(SnippetSuggestionDecision.Rejected);
        row.DecidedByUserId.Should().Be(admin.Id);
        row.DecisionNote.Should().Be("Too narrow.");
        row.ApprovedSnippetId.Should().BeNull();

        (await verify.Snippets.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Approve_carries_instructions_and_minimum_application_version_to_snippet()
    {
        ApplicationVersion version;
        await using (var ctx = _db.NewContext())
        {
            version = new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc28",
                Name = "BC 2026 Wave 1",
                Application = "28.0.0.0",
                Runtime = "28.0",
                Ordering = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.ApplicationVersions.Add(version);
            await ctx.SaveChangesAsync();
        }

        var user = await SeedUserAsync(userId: 720);
        _db.OrgContext.CurrentUserId = user.Id;
        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new SnippetSuggestionInput(
                Title: "Carry metadata",
                Description: "Body.",
                Keywords: "",
                Files: new[] { new SnippetFileInput("A.al", "// a") },
                Instructions: "## Setup\n\nDrop into `src/`.",
                MinimumApplicationVersionId: version.Id));
        }

        var admin = await SeedUserAsync(userId: 721, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;
        Snippet snippet;
        await using (var ctx = _db.NewContext())
        {
            snippet = await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var verify = _db.NewContext();
        var promoted = await verify.Snippets
            .Include(s => s.MinimumApplicationVersion)
            .SingleAsync(s => s.Id == snippet.Id);
        promoted.Instructions.Should().Be("## Setup\n\nDrop into `src/`.");
        promoted.MinimumApplicationVersionId.Should().Be(version.Id);
        promoted.MinimumApplicationVersion!.Name.Should().Be("BC 2026 Wave 1");
    }

    [Fact]
    public async Task Approve_refuses_when_already_decided()
    {
        var user = await SeedUserAsync(userId: 708);
        _db.OrgContext.CurrentUserId = user.Id;
        int suggestionId;
        await using (var ctx = _db.NewContext())
        {
            suggestionId = await NewSuggestionService(ctx).SubmitAsync(new SnippetSuggestionInput(
                "Once", "Body.", "", new[] { new SnippetFileInput("A.al", "// a") }));
        }

        var admin = await SeedUserAsync(userId: 709, role: UserRole.Admin);
        _db.OrgContext.CurrentUserId = admin.Id;

        await using (var ctx = _db.NewContext())
        {
            await NewSuggestionService(ctx).ApproveAsync(suggestionId);
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewSuggestionService(ctx2).ApproveAsync(suggestionId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    private SnippetSuggestionService NewSuggestionService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<SnippetSuggestionService>.Instance, _db.OrgContext);
}
