using System.Net;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Contract for <see cref="ProjectConnectionService"/> (the BC SaaS delivery
/// connection): the client secret is encrypted on write and never returned;
/// validation rejects missing credentials; Test connection persists the fetched
/// environments and stamps "verified"; a missing GDAP and rejected credentials are
/// classified distinctly; refresh is a stable upsert that preserves a row's id and
/// per-environment settings; and the owner-or-admin gate guards every mutation. The BC HTTP
/// surfaces are faked (the same seam reason <c>IProcessRunner</c> exists), and the
/// OAuth token call runs against a stub <see cref="IHttpClientFactory"/>.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ProjectConnectionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9400;

    public ProjectConnectionServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class FakeAdminClient : IBcAdminClient
    {
        public Func<IReadOnlyList<BcEnvironment>> OnList = () => Array.Empty<BcEnvironment>();
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(OnList());

        // The by-name read is the delivery gate's surface, not the connection page's.
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();

        /// <summary>Microsoft's update window per environment name. Throwing here stands in for a per-environment API failure.</summary>
        public Func<string, BcUpdateSettings?> OnUpdateSettings = _ => null;
        public List<string> UpdateSettingsRequested { get; } = new();

        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            UpdateSettingsRequested.Add(environmentName);
            return Task.FromResult(OnUpdateSettings(environmentName));
        }

        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>A token service whose login round-trip returns a fixed 200 token.</summary>
    private BcTokenService TokenOk() =>
        new(new StubFactory(new StubHandler(HttpStatusCode.OK, "{\"access_token\":\"tok\",\"expires_in\":3600}")),
            NullLogger<BcTokenService>.Instance);

    /// <summary>A token service whose login round-trip is rejected (bad creds).</summary>
    private BcTokenService TokenRejected() =>
        new(new StubFactory(new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")),
            NullLogger<BcTokenService>.Instance);

    private ProjectConnectionService Svc(
        ALDevToolbox.Data.AppDbContext ctx,
        BcTokenService tokens,
        IBcAdminClient? admin = null)
        => new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), tokens,
            admin ?? new FakeAdminClient(),
            _db.DataProtectionProvider, NullLogger<ProjectConnectionService>.Instance);

    private async Task<int> SeedProjectAsync()
    {
        await using var ctx = _db.NewContext();
        var p = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private BcConnectionInput ValidConnection(string secret = "s3cr3t") =>
        new(Guid.NewGuid(), "client-abc", secret, DateTime.UtcNow.AddYears(1), "Europe/Copenhagen");

    // ── Secret handling ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveConnection_encrypts_secret_and_never_returns_it()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection("plaintext-secret"));
        }

        // The status view exposes presence, never the secret.
        await using (var ctx = _db.NewContext())
        {
            var status = await Svc(ctx, TokenOk()).GetConnectionAsync(id);
            status!.HasSecret.Should().BeTrue();
            status.IsConfigured.Should().BeTrue();
        }

        // The stored column is ciphertext that round-trips only through the protector.
        await using (var verify = _db.NewContext())
        {
            var stored = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => p.BcClientSecretEncrypted).SingleAsync();
            stored.Should().NotBeNullOrEmpty();
            stored.Should().NotBe("plaintext-secret", "the secret is stored encrypted, never as plaintext");
            _db.DataProtectionProvider
                .CreateProtector(ProjectConnectionService.SecretProtectionPurpose)
                .Unprotect(stored!).Should().Be("plaintext-secret");
        }
    }

    [Fact]
    public async Task SaveConnection_keeps_existing_secret_on_blank()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection("keep-me"));

        string? before;
        await using (var ctx = _db.NewContext())
            before = await ctx.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => p.BcClientSecretEncrypted).SingleAsync();

        // Re-save with a blank secret but a changed timezone.
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), "client-abc", ClientSecret: "", SecretExpiresAt: null, TimeZone: "UTC"));

        await using (var verify = _db.NewContext())
        {
            var after = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new { p.BcClientSecretEncrypted, p.BcTimeZone }).SingleAsync();
            after.BcClientSecretEncrypted.Should().Be(before, "a blank secret leaves the stored one untouched");
            after.BcTimeZone.Should().Be("UTC", "other fields still update");
        }
    }

    [Fact]
    public async Task SaveConnection_rejects_missing_credentials()
    {
        var id = await SeedProjectAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx, TokenOk()).SaveConnectionAsync(id,
            new BcConnectionInput(TenantId: null, ClientId: "", ClientSecret: null, SecretExpiresAt: null, TimeZone: null));

        var ex = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
        ex.Errors.Should().ContainKey("BcTenantId");
        ex.Errors.Should().ContainKey("BcClientId");
        ex.Errors.Should().ContainKey("BcClientSecret");
    }

    [Fact]
    public async Task SaveConnection_requires_expiry_when_setting_a_secret()
    {
        var id = await SeedProjectAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx, TokenOk()).SaveConnectionAsync(id,
            new BcConnectionInput(Guid.NewGuid(), "client-abc", "secret", SecretExpiresAt: null, TimeZone: null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("BcClientSecretExpiresAt");
    }

    // ── Environment listing ───────────────────────────────────────────────

    /// <summary>
    /// Production sorts above sandboxes regardless of name — a customer with several
    /// sandboxes would otherwise bury the environment that matters most under
    /// alphabetical order ("Dev", "Preview", "Test" all precede "Production").
    /// </summary>
    [Fact]
    public async Task ListEnvironments_puts_production_before_sandboxes()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("Dev", "Sandbox"),
                new BcEnvironment("Live", "Production"),
                new BcEnvironment("Test", "Sandbox"),
                new BcEnvironment("Backup", "Production"),
            },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        IReadOnlyList<ProjectEnvironmentRow> rows;
        await using (var ctx = _db.NewContext())
            rows = await Svc(ctx, TokenOk(), admin).ListEnvironmentsAsync(id);

        rows.Select(r => r.Name).Should().Equal("Backup", "Live", "Dev", "Test");
    }

    // ── Test connection ───────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_persists_environments_and_marks_verified()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("Sandbox", "Sandbox") },
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Result.Should().Be(BcConnectionResult.Success);
        result.EnvironmentCount.Should().Be(2);

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjectEnvironments.CountAsync(e => e.ProjectId == id)).Should().Be(2);
            (await verify.OeProjects.Where(p => p.Id == id).Select(p => p.BcConnectionVerifiedAt).SingleAsync())
                .Should().NotBeNull("a successful test stamps the verified time");
        }
    }

    /// <summary>
    /// 401 and 403 from the Admin Center API are different failures with different
    /// fixes — the app missing from BC's authorized-apps list vs. the app lacking
    /// permission — and collapsing them into one "GDAP is missing" message sent a
    /// real user hunting a GDAP relationship that their own-tenant setup never needed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BcConnectionResult.AppNotAuthorized)]
    [InlineData(HttpStatusCode.Forbidden, BcConnectionResult.AccessDenied)]
    public async Task TestConnection_distinguishes_unauthorized_from_forbidden(
        HttpStatusCode status, BcConnectionResult expected)
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => throw new BcApiException(status, "denied"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Result.Should().Be(expected);
        result.IsSuccess.Should().BeFalse();
        await using var verify = _db.NewContext();
        (await verify.OeProjects.Where(p => p.Id == id).Select(p => p.BcConnectionVerifiedAt).SingleAsync())
            .Should().BeNull("a denied environments call doesn't count as verified");
    }

    /// <summary>
    /// The 401 message has to name the fix, because the thing to change isn't in the
    /// Entra portal the user was just looking at — it's BC's own authorized-apps list.
    /// </summary>
    [Fact]
    public async Task TestConnection_401_message_points_at_the_authorized_apps_list()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => throw new BcApiException(HttpStatusCode.Unauthorized, "denied"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Message.Should().Contain("Authorized Microsoft Entra apps");
        result.Message.Should().NotContain("GDAP", "a 401 is not evidence of a missing GDAP relationship");
    }

    [Fact]
    public async Task TestConnection_reports_auth_failure_when_credentials_rejected()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenRejected()).TestConnectionAsync(id);

        result.Result.Should().Be(BcConnectionResult.AuthFailed);
    }

    [Fact]
    public async Task Refresh_is_a_stable_upsert_preserving_id_and_settings()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        // Pre-seed an environment carrying a setting of its own, and one that will vanish.
        int prodId;
        await using (var seed = _db.NewContext())
        {
            var prod = new ProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "Production",
                Type = "Production", FetchedAt = DateTime.UtcNow.AddDays(-1),
                UpdateWindowStart = new TimeOnly(22, 0), UpdateWindowEnd = new TimeOnly(6, 0),
            };
            seed.OeProjectEnvironments.Add(prod);
            seed.OeProjectEnvironments.Add(new ProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "OldSandbox",
                Type = "Sandbox", FetchedAt = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
            prodId = prod.Id;
        }

        var admin = new FakeAdminClient
        {
            // Production still present (type unchanged), a brand-new Sandbox, OldSandbox gone.
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("NewSandbox", "Sandbox") },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == id).ToListAsync();

        var prodRow = rows.Single(e => e.Name == "Production");
        prodRow.Id.Should().Be(prodId, "the row identity is preserved across a refresh");
        prodRow.UpdateWindowStart.Should().Be(new TimeOnly(22, 0), "a setting made on the row survives a refresh");
        prodRow.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
        prodRow.MissingSince.Should().BeNull();

        rows.Should().Contain(e => e.Name == "NewSandbox" && e.MissingSince == null);
        rows.Single(e => e.Name == "OldSandbox").MissingSince
            .Should().NotBeNull("an environment the customer removed is flagged, not deleted");
    }

    [Fact]
    public async Task Refresh_updates_the_fetched_detail_and_leaves_the_users_own_settings_alone()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        await using (var seed = _db.NewContext())
        {
            seed.OeProjectEnvironments.Add(new ProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "PROD", Type = "Production",
                UpdateWindowStart = new TimeOnly(22, 0), UpdateWindowEnd = new TimeOnly(6, 0),
                Status = "Active", Version = "27.4.0.0", FetchedAt = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        var tenant = Guid.NewGuid();
        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("PROD", "Production")
                {
                    FriendlyName = "CRONUS Production",
                    ApplicationFamily = "BusinessCentral",
                    Status = "Upgrading",
                    CountryCode = "DK",
                    AadTenantId = tenant,
                    WebClientLoginUrl = "https://businesscentral.dynamics.com/x/PROD",
                    Version = "27.5.5.15",
                },
            },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id && e.Name == "PROD");

        // Fetched fields move to what the API just said...
        row.Status.Should().Be("Upgrading");
        row.StatusFetchedAt.Should().NotBeNull();
        row.Version.Should().Be("27.5.5.15");
        row.FriendlyName.Should().Be("CRONUS Production");
        row.ApplicationFamily.Should().Be("BusinessCentral");
        row.CountryCode.Should().Be("DK");
        row.AadTenantId.Should().Be(tenant);
        row.WebClientLoginUrl.Should().Be("https://businesscentral.dynamics.com/x/PROD");

        // ...and the user's own settings on the same row are untouched.
        row.UpdateWindowStart.Should().Be(new TimeOnly(22, 0));
        row.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
    }

    [Fact]
    public async Task Refresh_mirrors_each_environments_business_central_update_window()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnUpdateSettings = _ => new BcUpdateSettings(new TimeOnly(2, 0), new TimeOnly(6, 0), "Romance Standard Time"),
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.BcUpdateWindowStart.Should().Be(new TimeOnly(2, 0));
        row.BcUpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
        row.BcUpdateWindowTimeZoneId.Should().Be("Romance Standard Time", "the Windows id is what a write takes back");
        row.BcUpdateWindowTimeZoneIana.Should().Be("Europe/Paris", "display maths needs the IANA form on Linux");
        row.BcUpdateWindowFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_survives_an_environment_whose_update_window_cannot_be_read()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("Sandbox", "Sandbox") },
            // One environment answers, the other refuses. The refusal must not cost us
            // the environment list, which is what the Refresh is actually for.
            OnUpdateSettings = name => name == "Sandbox"
                ? throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "denied")
                : new BcUpdateSettings(new TimeOnly(1, 0), new TimeOnly(5, 0), "UTC"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        result.IsSuccess.Should().BeTrue("one environment's settings call is not the refresh");
        result.EnvironmentCount.Should().Be(2);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking().Where(e => e.ProjectId == id).ToListAsync();
        rows.Single(e => e.Name == "Production").BcUpdateWindowFetchedAt.Should().NotBeNull();
        rows.Single(e => e.Name == "Sandbox").BcUpdateWindowFetchedAt
            .Should().BeNull("a failed read leaves the row alone rather than stamping a time it never got");
    }

    // ── Access control ────────────────────────────────────────────────────

    [Fact]
    public async Task Mutations_are_blocked_for_a_non_owner_non_admin()
    {
        var id = await SeedProjectAsync();

        const int strangerId = 9500;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = strangerId, OrganizationId = TestDb.DefaultOrgId, Email = "stranger@example.com",
                PasswordHash = "x", DisplayName = "Stranger", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentUserId = strangerId;
        try
        {
            await using var ctx = _db.NewContext();
            var svc = Svc(ctx, TokenOk());

            await ((Func<Task>)(() => svc.SaveConnectionAsync(id, ValidConnection())))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => svc.TestConnectionAsync(id)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }
}
