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
/// picked company; and the owner-or-admin gate guards every mutation. The BC HTTP
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
    }

    private sealed class FakeAutomationClient : IBcAutomationClient
    {
        public Func<string, IReadOnlyList<BcCompany>> OnList = _ => Array.Empty<BcCompany>();
        public Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(string accessToken, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnList(environmentName));
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
        IBcAdminClient? admin = null,
        IBcAutomationClient? automation = null)
        => new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), tokens,
            admin ?? new FakeAdminClient(), automation ?? new FakeAutomationClient(),
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

    [Fact]
    public async Task TestConnection_flags_missing_gdap_when_admin_denies()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => throw new BcApiException(HttpStatusCode.Forbidden, "forbidden"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Result.Should().Be(BcConnectionResult.GdapMissing);
        await using var verify = _db.NewContext();
        (await verify.OeProjects.Where(p => p.Id == id).Select(p => p.BcConnectionVerifiedAt).SingleAsync())
            .Should().BeNull("a denied environments call doesn't count as verified");
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
    public async Task Refresh_is_a_stable_upsert_preserving_id_and_company()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        // Pre-seed an environment with a picked company and one that will vanish.
        int prodId;
        var company = Guid.NewGuid();
        await using (var seed = _db.NewContext())
        {
            var prod = new ProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "Production",
                Type = "Production", CompanyId = company, CompanyName = "CRONUS", FetchedAt = DateTime.UtcNow.AddDays(-1),
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
        prodRow.CompanyId.Should().Be(company, "the picked company survives a refresh");
        prodRow.MissingSince.Should().BeNull();

        rows.Should().Contain(e => e.Name == "NewSandbox" && e.MissingSince == null);
        rows.Single(e => e.Name == "OldSandbox").MissingSince
            .Should().NotBeNull("an environment the customer removed is flagged, not deleted");
    }

    [Fact]
    public async Task PickCompany_records_the_selection()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        int envId;
        await using (var seed = _db.NewContext())
        {
            var env = new ProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "Production", Type = "Production", FetchedAt = DateTime.UtcNow,
            };
            seed.OeProjectEnvironments.Add(env);
            await seed.SaveChangesAsync();
            envId = env.Id;
        }

        var company = Guid.NewGuid();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).PickCompanyAsync(id, envId, company, "CRONUS");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.CompanyId.Should().Be(company);
        row.CompanyName.Should().Be("CRONUS");
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
