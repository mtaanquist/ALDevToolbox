using ALDevToolbox.Services;
using ALDevToolbox.Services.Offsite;
using FluentAssertions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Guards the single trickiest detail of the Azure provider: the deployment
/// fingerprint key <c>deployment-id</c> is illegal as an Azure metadata name
/// (hyphens aren't allowed — names must be valid C# identifiers), so it's
/// mapped to <c>deploymentid</c> on write and back on read. If that mapping
/// breaks, the cross-deployment restore guard silently degrades to treating
/// every Azure dump as a "legacy" (unstamped) upload.
/// </summary>
public sealed class AzureBlobMetadataTests
{
    [Fact]
    public void Deployment_id_is_rewritten_to_an_identifier_legal_name_on_write()
    {
        var canonical = new Dictionary<string, string>
        {
            [OffsiteBackupService.DeploymentMetadataKey] = "deploy-123",
        };

        var azure = AzureBlobProvider.MapMetadataToAzure(canonical);

        azure.Should().ContainKey(AzureBlobProvider.DeploymentMetadataAzure)
            .WhoseValue.Should().Be("deploy-123");
        azure.Should().NotContainKey(OffsiteBackupService.DeploymentMetadataKey);
    }

    [Fact]
    public void Deployment_id_is_restored_to_the_canonical_name_on_read()
    {
        var azure = new Dictionary<string, string>
        {
            [AzureBlobProvider.DeploymentMetadataAzure] = "deploy-123",
        };

        var canonical = AzureBlobProvider.MapMetadataFromAzure(azure);

        canonical.Should().ContainKey(OffsiteBackupService.DeploymentMetadataKey)
            .WhoseValue.Should().Be("deploy-123");
    }

    [Fact]
    public void Round_trip_preserves_the_deployment_id_the_service_looks_up()
    {
        var canonical = new Dictionary<string, string>
        {
            [OffsiteBackupService.DeploymentMetadataKey] = "abc-def-ghi",
        };

        var restored = AzureBlobProvider.MapMetadataFromAzure(
            AzureBlobProvider.MapMetadataToAzure(canonical));

        restored[OffsiteBackupService.DeploymentMetadataKey].Should().Be("abc-def-ghi");
    }

    [Fact]
    public void Unmapped_keys_pass_through_unchanged()
    {
        var canonical = new Dictionary<string, string> { ["other"] = "value" };

        AzureBlobProvider.MapMetadataToAzure(canonical)["other"].Should().Be("value");
        AzureBlobProvider.MapMetadataFromAzure(canonical)["other"].Should().Be("value");
    }

    [Fact]
    public void Canonical_and_azure_metadata_names_match_the_service_contract()
    {
        // The canonical name must equal the one the service stamps, and the
        // Azure form must be identifier-legal (no hyphen).
        AzureBlobProvider.DeploymentMetadataCanonical
            .Should().Be(OffsiteBackupService.DeploymentMetadataKey);
        AzureBlobProvider.DeploymentMetadataAzure.Should().NotContain("-");
    }
}
