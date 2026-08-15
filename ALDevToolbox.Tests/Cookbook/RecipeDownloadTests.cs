using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Coverage for the customer download tracking added with the Cookbook
/// improvements: <see cref="RecipeService.RecordDownloadAsync"/>,
/// <see cref="RecipeService.GetDownloadsAsync"/>, and
/// <see cref="RecipeService.GetCustomerNamesAsync"/>. The point is tracing a
/// later bug in a recipe to whoever received it -- so copies count too, and the
/// customer name is asked for but not required. See issue #539.
/// </summary>
public sealed class RecipeDownloadTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedRecipeAsync(string title = "Downloadable", int orgId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default(title, organizationId: orgId).WithFile("A.al", "// a");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<int> SeedUserAsync(int userId = 700)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{userId}@example.com",
            PasswordHash = "x",
            DisplayName = "Downloader",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task RecordDownload_inserts_row_scoped_to_org()
    {
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "  Acme A/S  ", userId);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.CustomerName.Should().Be("Acme A/S", "the customer name is trimmed");
        row.DownloadedByUserId.Should().Be(userId);
        row.DownloadedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetDownloads_loads_the_downloading_user_nav()
    {
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "Acme", userId);
        }

        await using var read = _db.NewContext();
        var downloads = await NewService(read).GetDownloadsAsync(recipeId);
        downloads.Should().ContainSingle();
        downloads[0].DownloadedByUser!.Email.Should().Be($"u{userId}@example.com");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RecordDownload_accepts_a_blank_customer_and_stores_null(string? customer)
    {
        // Gating the download on this field collected "test" and "x" from
        // everyone downloading for a demo. Null is the honest answer and the
        // admin panel renders it as "Not recorded". See issue #539.
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, customer, userId: null);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.CustomerName.Should().BeNull();
        row.Source.Should().Be(RecipeUseSource.Download);
    }

    [Fact]
    public async Task RecordDownload_rejects_an_oversized_customer_name()
    {
        var recipeId = await SeedRecipeAsync();
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordDownloadAsync(
                recipeId, new string('x', RecipeService.MaxCustomerNameLength + 1), userId: null));
        ex.Errors.Should().ContainKey("CustomerName");
    }

    [Fact]
    public async Task RecordCopy_records_a_use_with_no_customer()
    {
        // Single-file recipes are almost always taken with Copy, which never
        // opened the download modal -- so the history used to under-count
        // exactly the recipes people used most.
        var recipeId = await SeedRecipeAsync();
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordCopyAsync(recipeId, userId);
        }

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Source.Should().Be(RecipeUseSource.Copy);
        row.CustomerName.Should().BeNull();
        row.DownloadedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task RecordCopy_rejects_unknown_recipe()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordCopyAsync(9999, userId: null));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task GetCustomerNames_skips_uses_with_no_customer()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordDownloadAsync(recipeId, "CRONUS A/S", userId: null);
            await svc.RecordDownloadAsync(recipeId, "  ", userId: null);
            await svc.RecordCopyAsync(recipeId, userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerNamesAsync();
        names.Should().Equal(
            new[] { "CRONUS A/S" },
            "a blank download and a copy contribute no name");
    }

    [Fact]
    public async Task RecordDownload_rejects_unknown_recipe()
    {
        await using var ctx = _db.NewContext();
        var ex = await Assert.ThrowsAsync<PlanValidationException>(() =>
            NewService(ctx).RecordDownloadAsync(9999, "Acme", userId: null));
        ex.Errors.Should().ContainKey("Id");
    }

    [Fact]
    public async Task GetDownloads_returns_newest_first()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            // No sleep: GetDownloadsAsync tiebreaks on the monotonic Id, so the
            // second insert sorts first even within the same timestamp tick. #395
            await svc.RecordDownloadAsync(recipeId, "First", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Second", userId: null);
        }

        await using var read = _db.NewContext();
        var downloads = await NewService(read).GetDownloadsAsync(recipeId);
        downloads.Select(d => d.CustomerName).Should().Equal("Second", "First");
    }

    [Fact]
    public async Task GetCustomerNames_returns_distinct_sorted_names()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            await svc.RecordDownloadAsync(recipeId, "Beta", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Alpha", userId: null);
            await svc.RecordDownloadAsync(recipeId, "Beta", userId: null);
        }

        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerNamesAsync();
        names.Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task Cross_org_download_history_is_invisible()
    {
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RecordDownloadAsync(recipeId, "Acme", userId: null);
        }

        // Switch the ambient org to the other tenant; the filter must hide it.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using var read = _db.NewContext();
        var names = await NewService(read).GetCustomerNamesAsync();
        names.Should().BeEmpty();
    }

    private RecipeService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx));
}
