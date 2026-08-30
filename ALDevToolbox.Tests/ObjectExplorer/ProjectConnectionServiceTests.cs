using System.Net;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
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

        /// <summary>What the last settings write asked for, so a test can pin the payload.</summary>
        public string? Cadence;
        public bool? M365;
        public string? SelectedVersion;
        public string? SelectedVersionType;
        /// <summary>The date and window flag of the last version write, so a test can pin what the PATCH carried.</summary>
        public DateTimeOffset? SelectedDateTime;
        public bool? SelectedIgnoreUpdateWindow;
        /// <summary>How many version writes reached the client, so a refusal can be shown to have sent nothing.</summary>
        public int SelectWrites;
        public BcApiException? WriteThrows;

        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BcTimeZone>>(new[]
            {
                new BcTimeZone("Romance Standard Time", "(UTC+01:00) Brussels, Copenhagen, Madrid, Paris", "+01:00"),
            });

        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            Cadence = cadence;
            return Task.CompletedTask;
        }

        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(M365);

        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            M365 = enabled;
            return Task.CompletedTask;
        }

        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            SelectedVersion = targetVersion;
            SelectedVersionType = targetVersionType;
            SelectedDateTime = selectedDateTime;
            SelectedIgnoreUpdateWindow = ignoreUpdateWindow;
            SelectWrites++;
            return Task.CompletedTask;
        }

        /// <summary>Platform updates per environment name; throwing stands in for a denied read.</summary>
        public Func<string, IReadOnlyList<BcEnvironmentUpdate>> OnEnvironmentUpdates = _ => Array.Empty<BcEnvironmentUpdate>();

        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnEnvironmentUpdates(environmentName));
    }

    /// <summary>
    /// The App Management surface as the environment panel uses it. Each list has its own
    /// hook so a test can deny one section and prove the other three still render.
    /// </summary>
    private sealed class FakeAppManagementClient : IBcAppManagementClient
    {
        public Func<IReadOnlyList<BcInstalledApp>> OnInstalled = Array.Empty<BcInstalledApp>;
        public Func<IReadOnlyList<BcAvailableAppUpdate>> OnAvailable = Array.Empty<BcAvailableAppUpdate>;
        public Func<IReadOnlyList<BcScheduledPteOperation>> OnScheduled = Array.Empty<BcScheduledPteOperation>;

        /// <summary>What a cancel was asked to remove, so a test can pin the three identifying values.</summary>
        public (Guid AppId, string Version, string ScheduleKind)? Removed;
        public BcApiException? RemoveThrows;

        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnInstalled());
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnAvailable());
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnScheduled());

        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
        {
            if (RemoveThrows is not null) throw RemoveThrows;
            Removed = (appId, targetVersion, scheduleKind);
            return Task.FromResult(new BcAppOperation(
                Guid.NewGuid(), appId, "install", BcAppOperationStatus.Canceled, "canceled",
                string.Empty, targetVersion, scheduleKind, string.Empty, string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow));
        }

        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
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
        IBcAdminClient? admin = null,
        IBcAppManagementClient? apps = null)
        => new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), tokens,
            admin ?? new FakeAdminClient(), apps ?? new FakeAppManagementClient(),
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

    // ── The mirrored next platform update (what the fleet page lists) ─────

    /// <summary>One update record, with only the fields a mirror test cares about set.</summary>
    private static BcEnvironmentUpdate Update(
        string version, bool available = true, bool selected = false,
        DateTimeOffset? selectedAt = null, DateTimeOffset? latest = null,
        bool ignoresWindow = false, string status = "Scheduled", string type = "Minor")
        => new(version, available, selected, status, type, selectedAt, latest, ignoresWindow,
            RolloutStatus: "Released", ExpectedMonth: null, ExpectedYear: null);

    private async Task<ProjectEnvironment> RefreshAndReadRowAsync(int projectId, FakeAdminClient admin)
    {
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(projectId);

        await using var verify = _db.NewContext();
        return await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == projectId);
    }

    [Fact]
    public async Task Refresh_mirrors_the_selected_update_even_when_a_newer_one_is_available()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var scheduled = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[]
            {
                Update("27.6", selected: true, selectedAt: scheduled, latest: latest, status: "Scheduled", type: "Minor"),
                Update("28.0", status: "Available", type: "Major"),
            },
        };

        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().Be("27.6", "the customer's chosen slot is the answer, not the newest offer");
        row.BcNextUpdateType.Should().Be("Minor");
        row.BcNextUpdateStatus.Should().Be("Scheduled", "the API's own wording is stored verbatim");
        row.BcNextUpdateDate.Should().Be(scheduled.UtcDateTime);
        row.BcNextUpdateLatestDate.Should().Be(latest.UtcDateTime);
        row.BcNextUpdateIgnoresWindow.Should().BeFalse();
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_mirrors_the_newest_available_update_when_none_is_selected()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            // 10.0 beats 9.9 numerically; a string compare would pick 9.9 and quietly
            // mirror last year's update as the next one.
            OnEnvironmentUpdates = _ => new[]
            {
                Update("9.9"),
                Update("10.0"),
                Update("11.0", available: false, status: "NotAvailable"),
            },
        };

        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().Be("10.0",
            "10.0 is newer than 9.9, and an unavailable version has no date to schedule");
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_clears_the_mirror_and_stamps_it_when_there_is_no_update()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: DateTimeOffset.UtcNow) },
        };
        await RefreshAndReadRowAsync(id, admin);

        // The customer's update ran; the list is now empty.
        admin.OnEnvironmentUpdates = _ => Array.Empty<BcEnvironmentUpdate>();
        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().BeNull();
        row.BcNextUpdateType.Should().BeNull();
        row.BcNextUpdateStatus.Should().BeNull();
        row.BcNextUpdateDate.Should().BeNull();
        row.BcNextUpdateLatestDate.Should().BeNull();
        row.BcNextUpdateIgnoresWindow.Should().BeNull();
        row.BcNextUpdateFetchedAt.Should().NotBeNull(
            "an empty list is a successful read saying 'nothing scheduled', not 'never asked'");
    }

    [Fact]
    public async Task Refresh_leaves_the_mirror_alone_when_the_updates_call_fails()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var scheduled = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: scheduled) },
        };
        var before = await RefreshAndReadRowAsync(id, admin);
        before.BcNextUpdateFetchedAt.Should().NotBeNull();

        admin.OnEnvironmentUpdates = _ => throw new BcApiException(HttpStatusCode.Forbidden, "denied");
        var after = await RefreshAndReadRowAsync(id, admin);

        after.BcNextUpdateVersion.Should().Be("27.6", "a failed read degrades to stale, never to blank");
        after.BcNextUpdateDate.Should().Be(scheduled.UtcDateTime);
        after.BcNextUpdateFetchedAt.Should().Be(before.BcNextUpdateFetchedAt,
            "the age of the answer is the age of the last successful read");
    }

    [Fact]
    public async Task Unattended_refresh_mirrors_without_claiming_the_connection_was_verified()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: DateTimeOffset.UtcNow) },
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsUnattendedAsync(id);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.Name.Should().Be("Production", "the sweep upserts the environment list like a Refresh does");
        row.BcNextUpdateVersion.Should().Be("27.6");

        var verified = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
            .Select(p => p.BcConnectionVerifiedAt).SingleAsync();
        verified.Should().BeNull("a sweep nobody asked for is not the consultant's own connection test");
    }

    // ── The environment panel (live reads, never cached) ──────────────────

    /// <summary>Seeds a project with one environment and returns both ids.</summary>
    private async Task<(int ProjectId, int EnvironmentId)> SeedEnvironmentAsync(string name = "Production")
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        await using var seed = _db.NewContext();
        var env = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = name,
            Type = "Production", ApplicationFamily = "BusinessCentral", FetchedAt = DateTime.UtcNow,
        };
        seed.OeProjectEnvironments.Add(env);
        await seed.SaveChangesAsync();
        return (id, env.Id);
    }

    private static BcInstalledApp App(string name, string appType = "tenant", Guid? appId = null) => new(
        AppId: appId ?? Guid.NewGuid(), Name: name, Publisher: "CRONUS A/S", Version: "1.0.0.0",
        State: "Installed", AppType: appType, CanBeUninstalled: true,
        LastOperationId: null, LastUpdateAttemptResult: string.Empty);

    private static BcScheduledPteOperation Scheduled(Guid appId, string version = "2.0.0.0") => new(
        Id: Guid.NewGuid(), AppId: appId, Type: "Install", Status: BcAppOperationStatus.Scheduled,
        RawStatus: "scheduled", TargetAppVersion: version, ScheduleKind: BcDeploymentSchedule.UpdateWindow,
        Name: "CRONUS Toolbox", Publisher: "CRONUS A/S", SyncMode: BcSyncMode.Add,
        LanguageId: string.Empty, CreatedOn: DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_panel_reads_all_four_sections_live()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Toolbox"), App("Some Marketplace App", "global") },
            OnAvailable = () => new[] { new BcAvailableAppUpdate(Guid.NewGuid(), "Some Marketplace App", "Vendor", "3.0.0.0", Array.Empty<BcAppUpdateRequirement>()) },
            OnScheduled = () => new[] { Scheduled(Guid.NewGuid()) },
        };
        var admin = new FakeAdminClient
        {
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, true, "scheduled", "GA",
                    DateTimeOffset.UtcNow.AddDays(7), null, false, "Active", null, null),
            },
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), admin, apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.EnvironmentName.Should().Be("Production");
        panel.InstalledApps.Should().HaveCount(2);
        panel.AvailableUpdates.Should().ContainSingle();
        panel.ScheduledInstalls.Should().ContainSingle();
        panel.EnvironmentUpdates.Should().ContainSingle();
        panel.InstalledAppsError.Should().BeNull();
    }

    [Fact]
    public async Task One_denied_section_does_not_blank_the_others()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Toolbox") },
            // The available-updates read is denied; the rest must survive it.
            OnAvailable = () => throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "denied"),
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.AvailableUpdatesError.Should().NotBeNull().And.Subject.Should().Contain("Marketplace");
        panel.AvailableUpdates.Should().BeEmpty();
        panel.InstalledApps.Should().ContainSingle("one refusal is not the whole panel");
        panel.InstalledAppsError.Should().BeNull();
    }

    [Fact]
    public async Task The_panel_marks_the_apps_this_toolbox_released_here()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var ours = Guid.NewGuid();
        await SeedDeliveredAppAsync(projectId, "Production", ours);

        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Toolbox", "tenant", ours), App("Someone Else's PTE") },
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.ReleasedAppIds.Should().ContainSingle().Which.Should().Be(ours,
            "the panel can then say which pending install is the consultant's own");
    }

    [Fact]
    public async Task Cancelling_a_scheduled_install_names_the_version_and_schedule()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var appId = Guid.NewGuid();
        var apps = new FakeAppManagementClient();

        await using var ctx = _db.NewContext();
        await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .CancelScheduledInstallAsync(projectId, envId, appId, "2.0.0.0", BcDeploymentSchedule.UpdateWindow);

        // All three identify the entry; Business Central needs every one of them.
        apps.Removed.Should().Be((appId, "2.0.0.0", BcDeploymentSchedule.UpdateWindow));
    }

    [Fact]
    public async Task A_refused_cancel_reads_as_something_a_consultant_can_act_on()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            RemoveThrows = new BcApiException(System.Net.HttpStatusCode.NotFound,
                "The Admin Center API returned 404. ResourceDoesNotExist"),
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .CancelScheduledInstallAsync(projectId, envId, Guid.NewGuid(), "2.0.0.0", BcDeploymentSchedule.UpdateWindow);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Environment"];
        error.Should().Contain("didn't cancel");
    }

    /// <summary>Records a delivery that put <paramref name="appId"/> onto the environment.</summary>
    private async Task SeedDeliveredAppAsync(int projectId, string environmentName, Guid appId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new Pipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        var build = new ProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
        };
        ctx.OeProjectBuilds.Add(build);

        var env = await ctx.OeProjectEnvironments.FirstAsync(e => e.ProjectId == projectId && e.Name == environmentName);
        var releasePipeline = new ReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Rel " + Guid.NewGuid().ToString("N"),
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var delivery = new ProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId,
            ReleasePipelineId = releasePipeline.Id, ProjectBuildId = build.Id,
            EnvironmentName = environmentName, ScheduledFor = DateTime.UtcNow,
            Status = ProjectDeliveryStatus.HandedOff, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        delivery.Results.Add(new ProjectDeliveryResult
        {
            OrganizationId = TestDb.DefaultOrgId, Ordering = 0, AppName = "CRONUS Toolbox",
            AppVersion = "2.0.0.0", AppId = appId.ToString(), Status = ProjectDeliveryResultStatus.Scheduled,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
    }

    // ── Environment settings writes (5b) ──────────────────────────────────

    [Fact]
    public async Task Setting_the_app_cadence_writes_to_bc_and_refreshes_our_cached_value()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SetAppUpdateCadenceAsync(projectId, envId, BcAppUpdateCadence.DuringMajorUpgrade);

        admin.Cadence.Should().Be(BcAppUpdateCadence.DuringMajorUpgrade);
        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.AppSourceAppsUpdateCadence.Should().Be(BcAppUpdateCadence.DuringMajorUpgrade,
            "the page must agree with the tenant straight after the write");
    }

    [Fact]
    public async Task An_unknown_cadence_never_reaches_business_central()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SetAppUpdateCadenceAsync(projectId, envId, "Whenever");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Cadence");
        admin.Cadence.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_setting_write_surfaces_readably()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            WriteThrows = new BcApiException(System.Net.HttpStatusCode.BadRequest,
                "Business Central no longer has this environment. Refresh the environments and try again."),
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SetM365AccessAsync(projectId, envId, true);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["M365Access"];
        error.Should().Contain("Refresh the environments");
    }

    [Fact]
    public async Task Selecting_a_target_version_refuses_one_business_central_has_not_released()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            // 27.7 exists but is not available yet - a stale page must not schedule it.
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, true, "scheduled", "GA", null, null, false, "Active", null, null),
                new BcEnvironmentUpdate("27.7", false, false, "", "GA", null, null, false, "", 12, 2026),
            },
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.7");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["TargetVersion"]
            .Should().Contain("isn't available");
        admin.SelectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task Selecting_an_available_target_version_passes_its_type_through()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, false, "", "GA", null, null, false, "Active", null, null),
            },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.6");

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedVersionType.Should().Be("GA", "preview versions are only valid for sandboxes, so the type travels with the choice");
    }

    // ── Update-date writes (issue #657 Stage 3) ───────────────────────────

    private const int FlagUserId = 9600;
    private const int PlainTeamUserId = 9601;
    private const int OrgAdminUserId = 9602;

    private static readonly DateTimeOffset ScheduledDate = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    private static BcEnvironmentUpdate Update(
        DateTimeOffset? selectedDateTime, DateTimeOffset? latestSelectable, bool selected = true, bool ignoresWindow = false) =>
        new("27.6", true, selected, "scheduled", "GA", selectedDateTime, latestSelectable, ignoresWindow, "Active", null, null);

    private async Task SeedUserAsync(int id, string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x",
            DisplayName = email, Role = role, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Puts the update-flag holder and a plain colleague on one team and assigns it to the
    /// project — the only shape that grants the update-ops axis, since the flag counts
    /// only on a team the project is assigned to.
    /// </summary>
    private async Task SeedUpdateOpsTeamAsync(int projectId)
    {
        await SeedUserAsync(FlagUserId, "upgrade@example.com", UserRole.User);
        await SeedUserAsync(PlainTeamUserId, "colleague@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "Upgrades", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();

        ctx.TeamMembers.Add(new TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = FlagUserId,
            ManagesUpdates = true, CreatedAt = DateTime.UtcNow,
        });
        ctx.TeamMembers.Add(new TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = PlainTeamUserId, CreatedAt = DateTime.UtcNow,
        });
        ctx.OeProjectTeams.Add(new ProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// An updates list that answers the pre-write validation read one way and the
    /// re-mirror read another, so a test can prove the row is refreshed from Business
    /// Central rather than from what we asked for.
    /// </summary>
    private static FakeAdminClient AdminWithUpdates(BcEnvironmentUpdate before, BcEnvironmentUpdate after)
    {
        var admin = new FakeAdminClient();
        admin.OnEnvironmentUpdates = _ => new[] { admin.SelectWrites == 0 ? before : after };
        return admin;
    }

    [Fact]
    public async Task Pushing_the_date_sends_the_latest_selectable_date_and_remirrors_the_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedVersionType.Should().Be("GA");
        admin.SelectedDateTime.Should().Be(Latest);
        admin.SelectedIgnoreUpdateWindow.Should().BeNull("only 'update now' takes the customer's window away");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateDate.Should().Be(Latest.UtcDateTime, "the fleet page must show the new date without waiting for the sweep");
        row.BcNextUpdateVersion.Should().Be("27.6");
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Running_the_update_now_sends_today_and_ignores_the_window()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(
            Update(ScheduledDate, Latest),
            Update(DateTimeOffset.UtcNow, Latest, ignoresWindow: true));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedDateTime.Should().NotBeNull();
        admin.SelectedDateTime!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        admin.SelectedIgnoreUpdateWindow.Should().BeTrue("a customer who agreed a slot wants Microsoft to pick it up now");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateIgnoresWindow.Should().BeTrue();
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Pushing_the_date_records_an_audit_row_naming_the_action_and_the_environment()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        await using var verify = _db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking().SingleAsync();
        entry.EntityType.Should().Be(AuditEntityType.ProjectEnvironment);
        entry.EntityId.Should().Be(envId);
        entry.Action.Should().Be(AuditAction.Updated);
        entry.EntityName.Should().Be("Production");
        entry.ChangedByUserId.Should().Be(FlagUserId);
        entry.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        entry.ChangedBy.Should().Contain("upgrade@example.com",
            "the log names the person, and a circuit has no HttpContext for the interceptor to read");
        // The snapshot is the state before the write, plus the event in plain words -
        // the audit model records rows changing, and these two writes are events.
        entry.SnapshotJson.Should().NotBeNull();
        entry.SnapshotJson!.Should().Contain("Moved the update date out to the latest");
        entry.SnapshotJson.Should().Contain("27.6");
    }

    [Fact]
    public async Task Running_the_update_now_records_its_own_audit_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(
            Update(ScheduledDate, Latest),
            Update(DateTimeOffset.UtcNow, Latest, ignoresWindow: true));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        await using var verify = _db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking().SingleAsync();
        entry.EntityId.Should().Be(envId);
        entry.SnapshotJson.Should().NotBeNull();
        entry.SnapshotJson!.Should().Contain("Started the update now",
            "the two fleet actions must be told apart in the log without opening the diff");
    }

    [Fact]
    public async Task A_refused_push_leaves_no_audit_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(0,
            "a skipped row changed nothing on the customer's tenant");
    }

    [Fact]
    public async Task Pushing_the_date_refuses_when_the_environment_has_nothing_on_offer()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("No update is available to reschedule.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Running_now_refuses_when_the_environment_has_nothing_on_offer()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("No update is available to run.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_business_central_gave_no_last_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, null) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Contain("last possible date");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_already_at_the_latest_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(Latest, Latest) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("This update's date is already the latest Microsoft allows.");
        admin.SelectWrites.Should().Be(0, "a no-op must not touch the customer's tenant");
    }

    [Fact]
    public async Task An_update_the_customer_has_not_picked_yet_is_selected_by_the_date_write()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(null, Latest, selected: false), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(Latest);
        admin.SelectWrites.Should().Be(1, "the same PATCH both picks the version and dates it");
    }

    // ── The two axes: manage and environment updates ──────────────────────

    [Fact]
    public async Task A_plain_member_of_the_projects_team_cannot_move_update_dates()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = PlainTeamUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, Latest) } };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        await ((Func<Task>)(() => svc.PushUpdateDateToLatestAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.RunUpdateNowAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task The_projects_owner_can_pick_the_version_but_not_move_its_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        // The owner manages the project and holds no update flag: the two axes apart.
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(ScheduledDate, Latest));

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        await ((Func<Task>)(() => svc.PushUpdateDateToLatestAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.RunUpdateNowAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();

        await svc.SelectTargetVersionAsync(projectId, envId, "27.6");
        admin.SelectedVersion.Should().Be("27.6", "picking the version stays open to whoever manages the project");
    }

    [Fact]
    public async Task The_update_flag_holder_can_pick_the_version_too()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, Latest) } };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.6");

        admin.SelectedVersion.Should().Be("27.6");
    }

    [Fact]
    public async Task An_org_admin_can_move_update_dates_anywhere_in_the_organisation()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(OrgAdminUserId, "ada@example.com", UserRole.Admin);
        _db.OrgContext.CurrentUserId = OrgAdminUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(Latest);
    }

    [Fact]
    public async Task Picking_the_version_names_both_ways_in_when_it_is_refused()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9604, "outsider@example.com", UserRole.User);
        _db.OrgContext.CurrentUserId = 9604;

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk()).SelectTargetVersionAsync(projectId, envId, "27.6");

        (await act.Should().ThrowAsync<ProjectAccessDeniedException>())
            .Which.Message.Should().Contain("environment updates");
    }

    // ── The audit scope (fence-adjacent: see AuditInterceptor) ────────────

    [Fact]
    public async Task A_cadence_change_is_audited()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        // The audit interceptor is only attached on a context that asks for it, so both
        // halves of this pair have to use one or the assertion proves nothing.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            await Svc(ctx, TokenOk(), new FakeAdminClient()).SetAppUpdateCadenceAsync(projectId, envId, BcAppUpdateCadence.DuringMajorUpgrade);

        await using var verify = _db.NewContext();
        var rows = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment && a.EntityId == envId)
            .ToListAsync();
        rows.Should().ContainSingle("a deliberate change to the customer's tenant belongs in the trail");
    }

    [Fact]
    public async Task Refreshing_the_environments_writes_no_audit_rows()
    {
        // The whole reason the audit scope is column-scoped: a Refresh rewrites status,
        // version, family and the mirrored BC window on every environment. Auditing that
        // would bury the changes that matter under one row per environment per click.
        var (projectId, _) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") { Status = "Active", Version = "27.5.5.15" } },
            OnUpdateSettings = _ => new BcUpdateSettings(new TimeOnly(2, 0), new TimeOnly(6, 0), "Romance Standard Time"),
        };

        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(projectId);

        await using var verify = _db.NewContext();
        var rows = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment)
            .ToListAsync();
        rows.Should().BeEmpty("fetched cache is not an edit");
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
