using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for the off-site provider discriminator on
/// <see cref="SystemSettingsService"/>: the choice round-trips, blank/unknown
/// values fall back to S3, the access/secret credentials still encrypt at rest
/// regardless of backend, and the "bucket required" validation reuses the
/// Azure "container" wording.
/// </summary>
public sealed class OffsiteSettingsProviderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Azure_provider_round_trips_through_save_and_resolve()
    {
        var svc = NewService();
        await svc.SaveOffsiteAsync(NewInput(provider: "azure-blob"));

        (await svc.GetOffsiteViewAsync()).Provider.Should().Be("azure-blob");
        (await svc.ResolveOffsiteAsync())!.Provider.Should().Be("azure-blob");
    }

    [Fact]
    public async Task Default_provider_is_s3()
    {
        var svc = NewService();
        await svc.SaveOffsiteAsync(NewInput(provider: null));

        (await svc.GetOffsiteViewAsync()).Provider.Should().Be("s3");
    }

    [Fact]
    public async Task Unknown_provider_is_coerced_to_s3()
    {
        var svc = NewService();
        await svc.SaveOffsiteAsync(NewInput(provider: "gcs"));

        (await svc.GetOffsiteViewAsync()).Provider.Should().Be("s3");
    }

    [Fact]
    public async Task Credentials_are_encrypted_at_rest_for_azure()
    {
        var svc = NewService();
        await svc.SaveOffsiteAsync(NewInput(
            provider: "azure-blob", accessKey: "devstoreaccount1", secretKey: "super-secret-account-key"));

        // The view never exposes the keys, only whether they're stored.
        var view = await svc.GetOffsiteViewAsync();
        view.HasAccessKey.Should().BeTrue();
        view.HasSecretKey.Should().BeTrue();

        // And the resolver decrypts them back to plaintext for the provider.
        var resolved = await svc.ResolveOffsiteAsync();
        resolved!.AccessKey.Should().Be("devstoreaccount1");
        resolved.SecretKey.Should().Be("super-secret-account-key");
    }

    [Fact]
    public async Task Enabled_without_bucket_reports_container_wording_for_azure()
    {
        var svc = NewService();
        var act = async () => await svc.SaveOffsiteAsync(NewInput(provider: "azure-blob", bucket: ""));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("OffsiteBucket");
        ex.Which.Errors["OffsiteBucket"].Should().Contain("Container");
    }

    private SystemSettingsService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        return new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
    }

    private static OffsiteSettingsInput NewInput(
        string? provider = "s3",
        string? bucket = "my-bucket",
        string? accessKey = "access",
        string? secretKey = "secret")
        => new(
            Enabled: true,
            Provider: provider,
            Endpoint: null,
            Region: "eu-west-1",
            Bucket: bucket,
            Prefix: "aldevtoolbox/",
            AccessKey: accessKey,
            ClearAccessKey: false,
            SecretKey: secretKey,
            ClearSecretKey: false,
            ForcePathStyle: false,
            RetentionDays: 90);
}
