using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/admin/users</c>. Pins the three-state contract on the
/// two list sections that have an empty-state copy (invites + pending
/// signups) and the populated render for active users. The "Active &amp;
/// disabled" section deliberately has no empty-state today — a real admin
/// will always see at least themselves — so we don't pin one.
/// </summary>
public sealed class AdminUsersTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminUsersTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<UserAdministrationService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_org_renders_empty_state_copy_for_invites_and_pending_signups()
    {
        var cut = _ctx.RenderComponent<AdminUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No pending invites");
            cut.Markup.Should().Contain("No pending signups",
                "the pending-signups section has its own three-state contract — "
                + "admins should not see a phantom empty table");
        });
    }

    [Fact]
    public async Task Active_users_section_renders_one_row_per_active_user()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "alice@example.com",
                DisplayName = "Alice",
                PasswordHash = "x",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "bob@example.com",
                DisplayName = "Bob",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Disabled,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alice@example.com");
            cut.Markup.Should().Contain("bob@example.com");
            cut.Markup.Should().Contain("Active &amp; disabled (2)",
                "the section header counter must reflect the rendered rows");
        });
    }
}
