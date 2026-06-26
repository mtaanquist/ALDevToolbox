using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Account;

/// <summary>
/// Round-trip for a user's per-provider repository tokens on
/// <see cref="UserRepositoryTokenService"/>: the PAT is encrypted at rest,
/// resolves back for the owning user, an empty value keeps the stored token,
/// clearing removes it, and the status view never exposes the secret. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public sealed class UserRepositoryTokenServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private UserRepositoryTokenService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider);

    private async Task<int> SeedActingUserAsync()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = $"u{Guid.NewGuid():N}@example.test",
                PasswordHash = "x",
                DisplayName = "Tester",
                Role = UserRole.User,
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            id = user.Id;
        }
        _db.OrgContext.CurrentUserId = id;
        return id;
    }

    [Fact]
    public async Task Save_encrypts_token_and_resolves_back_for_the_owner()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().BeNull("nothing is stored yet");

        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "ghp_secret_value", clear: false);

        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().Be("ghp_secret_value");

        await using var verify = _db.NewContext();
        var stored = await verify.UserRepositoryTokens
            .Select(t => t.TokenEncrypted)
            .FirstAsync();
        stored.Should().NotBeNullOrEmpty();
        stored.Should().NotContain("ghp_secret_value", "the column stores ciphertext, never the plaintext token");
    }

    [Fact]
    public async Task Tokens_are_scoped_per_provider()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "gh-token", clear: false);
        await svc.SaveTokenAsync(RepositoryProvider.AzureDevOps, "az-token", clear: false);

        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().Be("gh-token");
        (await svc.ResolveTokenAsync(RepositoryProvider.AzureDevOps)).Should().Be("az-token");
    }

    [Fact]
    public async Task Resaving_the_same_provider_updates_in_place()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "first", clear: false);
        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "second", clear: false);

        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().Be("second");

        await using var verify = _db.NewContext();
        (await verify.UserRepositoryTokens.CountAsync(t => t.Provider == RepositoryProvider.GitHub))
            .Should().Be(1, "the unique (user, org, provider) key upserts rather than duplicating");
    }

    [Fact]
    public async Task Blank_value_keeps_the_stored_token()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "keep-me", clear: false);

        // The form posts blank to leave the token untouched.
        await svc.SaveTokenAsync(RepositoryProvider.GitHub, null, clear: false);

        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().Be("keep-me");
    }

    [Fact]
    public async Task Clear_removes_the_stored_token()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "gone-soon", clear: false);

        await svc.SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);

        (await svc.HasTokenAsync(RepositoryProvider.GitHub)).Should().BeFalse();
        (await svc.ResolveTokenAsync(RepositoryProvider.GitHub)).Should().BeNull();
    }

    [Fact]
    public async Task Status_view_reports_presence_without_exposing_the_secret()
    {
        await SeedActingUserAsync();
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await svc.SaveTokenAsync(RepositoryProvider.GitHub, "secret", clear: false);

        var status = await svc.GetTokenStatusAsync();
        status.Should().ContainKey(RepositoryProvider.GitHub);
        status.Should().NotContainKey(RepositoryProvider.AzureDevOps);
    }
}
