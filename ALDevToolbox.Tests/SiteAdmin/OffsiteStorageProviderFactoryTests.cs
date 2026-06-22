using ALDevToolbox.Services;
using ALDevToolbox.Services.Offsite;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Pins the off-site provider selection: the <c>offsite_provider</c>
/// discriminator routes to the right <see cref="IOffsiteStorageProvider"/>,
/// and anything unrecognised falls back to S3 (matching the column default).
/// Neither SDK opens a network connection at construction, so these run with
/// dummy-but-well-formed credentials and no live storage.
/// </summary>
public sealed class OffsiteStorageProviderFactoryTests
{
    private static readonly OffsiteStorageProviderFactory Factory =
        new(NullLoggerFactory.Instance);

    private static ResolvedOffsiteSettings Settings(string provider) => new(
        Provider: provider,
        Endpoint: null,
        Region: "eu-west-1",
        Bucket: "container-or-bucket",
        Prefix: "aldevtoolbox/",
        // For Azure these are account name + account key; for S3, access/secret.
        AccessKey: "devstoreaccount1",
        SecretKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
        ForcePathStyle: false,
        RetentionDays: 90);

    [Fact]
    public void Azure_discriminator_creates_azure_provider()
    {
        Factory.Create(Settings("azure-blob")).Should().BeOfType<AzureBlobProvider>();
    }

    [Fact]
    public void S3_discriminator_creates_s3_provider()
    {
        Factory.Create(Settings("s3")).Should().BeOfType<S3Provider>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("gcs")]
    [InlineData("unknown")]
    public void Unrecognised_discriminator_falls_back_to_s3(string provider)
    {
        Factory.Create(Settings(provider)).Should().BeOfType<S3Provider>();
    }
}
