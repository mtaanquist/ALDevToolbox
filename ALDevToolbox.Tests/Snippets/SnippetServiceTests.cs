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
/// Happy-path and validation coverage for <see cref="SnippetService"/>:
/// create, update (file reconcile), soft-delete/restore, deprecate, and the
/// validation rules that protect the API contract.
/// </summary>
public sealed class SnippetServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_persists_snippet_and_files_in_order()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var input = new SnippetInput(
            Title: "Doc Attachment Factbox",
            Description: "Expose the standard factbox on an unsupported table.",
            Keywords: "  Factbox Attachment Subscriber ",
            Deprecated: false,
            Files: new[]
            {
                new SnippetFileInput("EventSub.Codeunit.al", "// subscriber"),
                new SnippetFileInput("MyPageExt.PageExt.al", "// page ext"),
            });

        var snippet = await svc.CreateAsync(input);

        await using var read = _db.NewContext();
        var persisted = await read.Snippets
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .SingleAsync(s => s.Id == snippet.Id);
        persisted.Title.Should().Be("Doc Attachment Factbox");
        persisted.Keywords.Should().Be("factbox attachment subscriber", "keywords are normalised to lower-case");
        persisted.Files.Select(f => f.FileName).Should().Equal(
            "EventSub.Codeunit.al", "MyPageExt.PageExt.al");
        persisted.Files.Select(f => f.Ordering).Should().Equal(0, 1);
    }

    [Fact]
    public async Task Update_reconciles_files_in_place_and_preserves_ids()
    {
        int snippetId;
        int fileAId, fileBId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Reorder Me")
                .WithFile("A.al", "// a")
                .WithFile("B.al", "// b");
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
            fileAId = s.Files.Single(f => f.FileName == "A.al").Id;
            fileBId = s.Files.Single(f => f.FileName == "B.al").Id;
        }

        var input = new SnippetInput(
            Title: "Reorder Me",
            Description: "Synthetic snippet used in tests.",
            Keywords: "test",
            Deprecated: false,
            Files: new[]
            {
                new SnippetFileInput("B.al", "// b edited"),
                new SnippetFileInput("A.al", "// a"),
            });

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(snippetId, input);
        }

        await using var verify = _db.NewContext();
        var files = await verify.SnippetFiles
            .Where(f => f.SnippetId == snippetId)
            .OrderBy(f => f.Ordering)
            .ToListAsync();
        files.Select(f => f.FileName).Should().Equal("B.al", "A.al");
        files[0].Id.Should().Be(fileAId, "reconciler mutates row[0] in place");
        files[0].Content.Should().Be("// b edited");
        files[1].Id.Should().Be(fileBId);
    }

    [Fact]
    public async Task Update_appends_and_removes_files_when_list_shape_changes()
    {
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Resize Me")
                .WithFile("A.al", "// a")
                .WithFile("B.al", "// b");
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        // Drop B, add C.
        var input = new SnippetInput(
            "Resize Me", "Synthetic snippet used in tests.", "test", false,
            new[]
            {
                new SnippetFileInput("A.al", "// a"),
                new SnippetFileInput("C.al", "// c"),
            });
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(snippetId, input);
        }

        await using var verify = _db.NewContext();
        var names = await verify.SnippetFiles
            .Where(f => f.SnippetId == snippetId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.FileName)
            .ToListAsync();
        names.Should().Equal("A.al", "C.al");
    }

    [Fact]
    public async Task SoftDelete_then_restore_round_trips_DeletedAt()
    {
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Delete Me").WithFile("A.al", "// a");
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SoftDeleteAsync(snippetId);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.Snippets.SingleAsync(s => s.Id == snippetId)).DeletedAt.Should().NotBeNull();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RestoreAsync(snippetId);
        }
        await using (var verify = _db.NewContext())
        {
            (await verify.Snippets.SingleAsync(s => s.Id == snippetId)).DeletedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task SetDeprecated_flips_the_flag()
    {
        int snippetId;
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Deprecate Me").WithFile("A.al", "// a");
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
            snippetId = s.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SetDeprecatedAsync(snippetId, deprecated: true);
        }
        await using var verify = _db.NewContext();
        (await verify.Snippets.SingleAsync(s => s.Id == snippetId)).Deprecated.Should().BeTrue();
    }

    [Fact]
    public async Task Create_rejects_missing_title_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new SnippetInput(
            Title: "  ",
            Description: "Whatever",
            Keywords: "",
            Deprecated: false,
            Files: new[] { new SnippetFileInput("A.al", "// a") });

        Func<Task> act = () => svc.CreateAsync(input);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task Create_rejects_duplicate_title_within_org()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Taken").WithFile("A.al", "// a"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        Func<Task> act = () => svc.CreateAsync(new SnippetInput(
            Title: "Taken",
            Description: "Different body, same title.",
            Keywords: "",
            Deprecated: false,
            Files: new[] { new SnippetFileInput("B.al", "// b") }));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task Create_rejects_file_names_with_slashes_or_dot_dot()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new SnippetInput(
            Title: "Bad Names",
            Description: "Bad file names should fail.",
            Keywords: "",
            Deprecated: false,
            Files: new[]
            {
                new SnippetFileInput("src/A.al", "// nope"),
                new SnippetFileInput("..\\B.al", "// nope"),
            });

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files[0].FileName");
        ex.Errors.Should().ContainKey("Files[1].FileName");
    }

    [Fact]
    public async Task Create_rejects_when_no_files_supplied()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new SnippetInput(
            "Empty", "No files at all.", "", false, Array.Empty<SnippetFileInput>());

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files");
    }

    [Fact]
    public async Task Create_rejects_duplicate_file_names_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var input = new SnippetInput(
            "Dupes", "Two files with the same name.", "", false,
            new[]
            {
                new SnippetFileInput("Sample.al", "// 1"),
                new SnippetFileInput("sample.AL", "// 2"),
            });

        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.CreateAsync(input));
        ex.Errors.Should().ContainKey("Files[1].FileName");
    }

    [Fact]
    public async Task SoftDelete_unknown_id_raises_validation_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() => svc.SoftDeleteAsync(9999));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task Cross_org_data_is_invisible()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Org1", organizationId: TestDb.DefaultOrgId)
                .WithFile("A.al", "// a"));
            ctx.Snippets.Add(SnippetBuilder.Default("Org2", organizationId: TestDb.OtherOrgId)
                .WithFile("B.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        var rows = await svc.SearchAsync(query: null);
        rows.Should().HaveCount(1);
        rows[0].Title.Should().Be("Org1");
    }

    private SnippetService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<SnippetService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx));
}
