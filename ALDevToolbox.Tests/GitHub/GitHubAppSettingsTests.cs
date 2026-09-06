using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The deployment-wide GitHub App registration on <c>system_settings</c>
/// (issue #620): its validation rules, the two key-ring-encrypted columns, and
/// the audit interceptor's refusal to record either of them. Mirrors the Entra
/// app-registration tests next door.
/// </summary>
public sealed class GitHubAppSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SystemSettingsService NewService() => _db.NewSystemSettingsService(_db.NewContext());

    private static string NewPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static GitHubAppInput Valid(
        string? appId = "123456",
        string? slug = "al-dev-toolbox",
        string? clientId = null,
        string? clientSecret = null,
        bool clearClientSecret = false,
        string? privateKey = null,
        bool clearPrivateKey = false,
        string? webhookSecret = null,
        bool clearWebhookSecret = false) =>
        new(appId, slug, clientId, clientSecret, clearClientSecret, privateKey, clearPrivateKey,
            webhookSecret, clearWebhookSecret);

    // --- Validation ---------------------------------------------------------

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task Save_rejects_an_app_id_that_is_not_a_positive_number(string appId)
    {
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(appId: appId));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubAppId");
    }

    [Theory]
    [InlineData("https://github.com/apps/al-dev-toolbox")]
    [InlineData("AL Dev Toolbox")]
    [InlineData("-leading-hyphen")]
    public async Task Save_rejects_a_slug_that_is_not_the_last_part_of_the_apps_url(string slug)
    {
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(slug: slug));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubAppSlug");
    }

    [Fact]
    public async Task Save_rejects_an_app_id_with_no_slug_beside_it()
    {
        // Without the slug there is no install URL, so every organisation would
        // get a Connect button that goes nowhere.
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(slug: null));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubAppSlug");
    }

    [Fact]
    public void The_html_patterns_match_the_rules_the_service_enforces()
    {
        // CLAUDE.md asks the browser rules to mirror the server's. They are one
        // string here, so this only has to prove the string is the right one.
        var appId = new System.Text.RegularExpressions.Regex($"^{SystemSettingsService.GitHubAppIdPattern}$");
        appId.IsMatch("123456").Should().BeTrue();
        appId.IsMatch("0").Should().BeFalse();
        appId.IsMatch("12a").Should().BeFalse();

        var slug = new System.Text.RegularExpressions.Regex($"^{SystemSettingsService.GitHubAppSlugPattern}$");
        slug.IsMatch("al-dev-toolbox").Should().BeTrue();
        slug.IsMatch("AL-Dev-Toolbox").Should().BeTrue("the service lowercases before storing");
        slug.IsMatch("a").Should().BeTrue();
        slug.IsMatch("-leading-hyphen").Should().BeFalse();
        slug.IsMatch("https://github.com/apps/al-dev-toolbox").Should().BeFalse();
    }

    [Fact]
    public async Task Save_rejects_a_private_key_the_runtime_cannot_import()
    {
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(privateKey: "-----BEGIN RSA PRIVATE KEY-----\nnope\n-----END RSA PRIVATE KEY-----"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubPrivateKey");
    }

    [Fact]
    public async Task Save_rejects_a_private_key_without_an_app_id()
    {
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(appId: null, privateKey: NewPrivateKey()));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubAppId");
    }

    [Fact]
    public async Task Save_rejects_a_webhook_secret_without_an_app_id()
    {
        // The webhook secret belongs to the app registration; storing one with no
        // app would leave a secret nothing could ever verify against (#627).
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(appId: null, slug: null, webhookSecret: "swordfish"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubAppId");
    }

    [Fact]
    public async Task Save_rejects_a_client_secret_without_a_client_id()
    {
        Func<Task> act = () => NewService().SaveGitHubAppAsync(Valid(clientSecret: "orphan-secret"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubClientSecret");
    }

    // --- Storage ------------------------------------------------------------

    [Fact]
    public async Task Save_stores_both_secrets_encrypted_and_lowercases_the_slug()
    {
        var pem = NewPrivateKey();
        await NewService().SaveGitHubAppAsync(Valid(
            slug: "AL-Dev-Toolbox", clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: pem));

        await using var read = _db.NewContext();
        var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
        row.GitHubAppId.Should().Be(123456);
        row.GitHubAppSlug.Should().Be("al-dev-toolbox");
        row.GitHubPrivateKeyEncrypted.Should().NotBeNullOrEmpty().And.NotBe(pem,
            "the column holds Data-Protection ciphertext, never the key itself");
        row.GitHubClientSecretEncrypted.Should().NotBeNullOrEmpty().And.NotBe("gh-secret");

        _db.DataProtectionProvider
            .CreateProtector(SystemSettingsService.GitHubPrivateKeyProtectionPurpose)
            .Unprotect(row.GitHubPrivateKeyEncrypted!).Should().Be(pem);
        _db.DataProtectionProvider
            .CreateProtector(SystemSettingsService.GitHubClientSecretProtectionPurpose)
            .Unprotect(row.GitHubClientSecretEncrypted!).Should().Be("gh-secret");
    }

    [Fact]
    public async Task Save_with_blank_secrets_keeps_the_stored_ones()
    {
        var pem = NewPrivateKey();
        await NewService().SaveGitHubAppAsync(Valid(clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: pem));

        // The form posts blank when the SiteAdmin doesn't re-type them.
        await NewService().SaveGitHubAppAsync(Valid(appId: "999", clientId: "Iv1.abc"));

        var view = await NewService().GetGitHubAppViewAsync();
        view.AppId.Should().Be(999);
        view.HasPrivateKey.Should().BeTrue();
        view.HasClientSecret.Should().BeTrue();
    }

    [Fact]
    public async Task Save_with_the_clear_flags_forgets_each_secret_on_its_own()
    {
        await NewService().SaveGitHubAppAsync(Valid(
            clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: NewPrivateKey()));

        await NewService().SaveGitHubAppAsync(Valid(clientId: "Iv1.abc", clearClientSecret: true));

        var view = await NewService().GetGitHubAppViewAsync();
        view.HasClientSecret.Should().BeFalse();
        view.HasPrivateKey.Should().BeTrue("only the client secret was cleared");
    }

    [Fact]
    public async Task Clearing_the_app_id_clears_the_slug_client_id_and_both_secrets()
    {
        await NewService().SaveGitHubAppAsync(Valid(
            clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: NewPrivateKey()));

        await NewService().SaveGitHubAppAsync(Valid(appId: null, slug: null, clientId: null));

        var view = await NewService().GetGitHubAppViewAsync();
        view.AppId.Should().BeNull();
        view.AppSlug.Should().BeNull();
        view.ClientId.Should().BeNull();
        view.HasClientSecret.Should().BeFalse();
        view.HasPrivateKey.Should().BeFalse("none of it means anything without the app it belongs to");
        view.IsConfigured.Should().BeFalse();
    }

    // --- Webhook secret (#627) ---------------------------------------------

    [Fact]
    public async Task Save_stores_the_webhook_secret_encrypted_and_resolves_it_back()
    {
        await NewService().SaveGitHubAppAsync(Valid(webhookSecret: "swordfish"));

        await using var read = _db.NewContext();
        var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
        row.GitHubWebhookSecretEncrypted.Should().NotBeNullOrEmpty().And.NotBe("swordfish",
            "the column holds Data-Protection ciphertext, never the secret itself");
        _db.DataProtectionProvider
            .CreateProtector(SystemSettingsService.GitHubWebhookSecretProtectionPurpose)
            .Unprotect(row.GitHubWebhookSecretEncrypted!).Should().Be("swordfish");

        (await NewService().ResolveGitHubWebhookSecretAsync()).Should().Be("swordfish");
        (await NewService().GetGitHubAppViewAsync()).HasWebhookSecret.Should().BeTrue();
    }

    [Fact]
    public async Task Resolving_the_webhook_secret_when_none_is_stored_is_a_no_rather_than_a_throw()
    {
        // Null here means every delivery is refused, which is the safe direction:
        // the endpoint must never treat "we could not check" as "it checked out".
        await NewService().SaveGitHubAppAsync(Valid());

        (await NewService().ResolveGitHubWebhookSecretAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Save_with_a_blank_webhook_secret_keeps_the_stored_one()
    {
        await NewService().SaveGitHubAppAsync(Valid(webhookSecret: "swordfish"));

        await NewService().SaveGitHubAppAsync(Valid(appId: "999"));

        (await NewService().ResolveGitHubWebhookSecretAsync()).Should().Be("swordfish");
    }

    [Fact]
    public async Task Save_with_the_clear_flag_forgets_only_the_webhook_secret()
    {
        await NewService().SaveGitHubAppAsync(Valid(
            clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: NewPrivateKey(), webhookSecret: "swordfish"));

        await NewService().SaveGitHubAppAsync(Valid(clientId: "Iv1.abc", clearWebhookSecret: true));

        var view = await NewService().GetGitHubAppViewAsync();
        view.HasWebhookSecret.Should().BeFalse();
        view.HasClientSecret.Should().BeTrue("only the webhook secret was cleared");
        view.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_the_app_id_forgets_the_webhook_secret_too()
    {
        await NewService().SaveGitHubAppAsync(Valid(webhookSecret: "swordfish"));

        await NewService().SaveGitHubAppAsync(Valid(appId: null, slug: null));

        (await NewService().GetGitHubAppViewAsync()).HasWebhookSecret.Should().BeFalse();
        (await NewService().ResolveGitHubWebhookSecretAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_webhook_secret_has_its_own_protector_purpose()
    {
        // Purposes are what stop ciphertext minted for one field being read as
        // another. Proving they differ is proving the fields are separated.
        await NewService().SaveGitHubAppAsync(Valid(clientId: "Iv1.abc", clientSecret: "gh-secret", webhookSecret: "swordfish"));

        await using var read = _db.NewContext();
        var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
        var wrongProtector = _db.DataProtectionProvider
            .CreateProtector(SystemSettingsService.GitHubClientSecretProtectionPurpose);

        var act = () => wrongProtector.Unprotect(row.GitHubWebhookSecretEncrypted!);
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public async Task View_is_not_configured_until_the_key_is_present_too()
    {
        await NewService().SaveGitHubAppAsync(Valid());
        (await NewService().GetGitHubAppViewAsync()).IsConfigured.Should().BeFalse("nothing can be signed without the key");

        await NewService().SaveGitHubAppAsync(Valid(privateKey: NewPrivateKey()));
        (await NewService().GetGitHubAppViewAsync()).IsConfigured.Should().BeTrue();
    }

    // --- Resolve ------------------------------------------------------------

    [Fact]
    public async Task Resolve_returns_the_plaintext_credentials_for_the_client()
    {
        var pem = NewPrivateKey();
        await NewService().SaveGitHubAppAsync(Valid(clientId: "Iv1.abc", clientSecret: "gh-secret", privateKey: pem));

        var resolved = await NewService().ResolveGitHubAppAsync();

        resolved.Should().NotBeNull();
        resolved!.AppId.Should().Be(123456);
        resolved.AppSlug.Should().Be("al-dev-toolbox");
        resolved.ClientId.Should().Be("Iv1.abc");
        resolved.ClientSecret.Should().Be("gh-secret");
        resolved.PrivateKeyPem.Should().Be(pem);
    }

    [Fact]
    public async Task Resolve_returns_null_when_there_is_no_private_key()
    {
        await NewService().SaveGitHubAppAsync(Valid());

        (await NewService().ResolveGitHubAppAsync()).Should().BeNull(
            "an app id without a key cannot sign anything, and the caller renders that as 'not set up'");
    }

    // --- Audit --------------------------------------------------------------

    [Fact]
    public async Task Changing_the_github_secrets_redacts_them_in_the_audit_snapshot()
    {
        await using (var seed = _db.NewContext())
        {
            // The fixture's migrations insert the singleton row, so seed by
            // updating it rather than inserting a second one.
            var row = await seed.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
            if (row is null)
            {
                row = new SystemSettings { Id = 1, UpdatedAt = DateTime.UtcNow };
                seed.SystemSettings.Add(row);
            }
            row.GitHubAppId = 1;
            row.GitHubClientSecretEncrypted = "cipher-old";
            row.GitHubPrivateKeyEncrypted = "key-cipher-old";
            row.UpdatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var row = await ctx.SystemSettings.FirstAsync(s => s.Id == 1);
            row.GitHubClientSecretEncrypted = "cipher-new";
            row.GitHubPrivateKeyEncrypted = "key-cipher-new";
            row.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var entry = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.SystemSettings && r.Action == AuditAction.Updated)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(entry.SnapshotJson!).RootElement;
        snapshot.GetProperty(nameof(SystemSettings.GitHubClientSecretEncrypted)).GetString().Should().Be("[redacted]");
        snapshot.GetProperty(nameof(SystemSettings.GitHubPrivateKeyEncrypted)).GetString().Should().Be("[redacted]");
    }
}
