using ALDevToolbox.Services.ObjectExplorer.Bc;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Parsing of the Microsoft API JSON envelopes the BC delivery clients consume: the
/// Admin Center <c>environments</c> list and the automation <c>companies</c> list.
/// Pure functions (no DB / no HTTP), so they pin the response shapes the live calls
/// depend on. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcClientParsingTests
{
    [Fact]
    public void ParseEnvironments_reads_name_and_type()
    {
        const string json = """
        { "value": [
            { "name": "Production", "type": "Production", "aadTenantId": "x" },
            { "name": "Sandbox", "type": "Sandbox" }
        ] }
        """;

        var envs = BcAdminClient.ParseEnvironments(json);

        envs.Should().HaveCount(2);
        envs.Should().Contain(e => e.Name == "Production" && e.Type == "Production");
        envs.Should().Contain(e => e.Name == "Sandbox" && e.Type == "Sandbox");
    }

    [Fact]
    public void ParseEnvironments_skips_entries_without_a_name()
    {
        const string json = """{ "value": [ { "type": "Sandbox" }, { "name": "Prod", "type": "Production" } ] }""";

        var envs = BcAdminClient.ParseEnvironments(json);

        envs.Should().ContainSingle().Which.Name.Should().Be("Prod");
    }

    [Fact]
    public void ParseEnvironments_tolerates_a_missing_value_array()
    {
        BcAdminClient.ParseEnvironments("{}").Should().BeEmpty();
    }

    [Fact]
    public void ParseCompanies_prefers_display_name_and_parses_the_id()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        { "value": [ { "id": "{{id}}", "name": "CRONUS-INT", "displayName": "CRONUS A/S" } ] }
        """;

        var companies = BcAutomationClient.ParseCompanies(json);

        var company = companies.Should().ContainSingle().Subject;
        company.Id.Should().Be(id);
        company.Name.Should().Be("CRONUS A/S", "the human display name is preferred over the technical name");
    }

    [Fact]
    public void ParseCompanies_falls_back_to_name_when_no_display_name()
    {
        var id = Guid.NewGuid();
        var json = $$"""{ "value": [ { "id": "{{id}}", "name": "CRONUS" } ] }""";

        BcAutomationClient.ParseCompanies(json).Single().Name.Should().Be("CRONUS");
    }

    [Fact]
    public void ParseCompanies_skips_entries_without_a_valid_id()
    {
        const string json = """{ "value": [ { "name": "No Id" }, { "id": "not-a-guid", "name": "Bad" } ] }""";

        BcAutomationClient.ParseCompanies(json).Should().BeEmpty();
    }

    [Fact]
    public void ParseExtensionUpload_reads_the_system_id()
    {
        const string json = """{ "systemId": "11111111-2222-3333-4444-555555555555", "schedule": "Current Version" }""";

        BcAutomationClient.ParseExtensionUpload(json).SystemId.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void ParseExtensionUpload_falls_back_to_id_when_no_system_id()
    {
        const string json = """{ "id": "abc-123" }""";

        BcAutomationClient.ParseExtensionUpload(json).SystemId.Should().Be("abc-123");
    }

    [Fact]
    public void ParseExtensionUpload_throws_when_no_id_is_returned()
    {
        var act = () => BcAutomationClient.ParseExtensionUpload("""{ "schedule": "Current Version" }""");

        act.Should().Throw<BcApiException>();
    }

    [Fact]
    public void ParseDeploymentStatus_reads_name_version_and_status()
    {
        const string json = """
        { "value": [
            { "name": "CRONUS Core", "appVersion": "1.0.0.0", "status": "Completed" },
            { "name": "CRONUS Sales", "appVersion": "2.0.0.0", "status": "InProgress" }
        ] }
        """;

        var rows = BcAutomationClient.ParseDeploymentStatus(json);

        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.Name == "CRONUS Core" && r.AppVersion == "1.0.0.0" && r.Status == "Completed");
        rows.Should().Contain(r => r.Name == "CRONUS Sales" && r.Status == "InProgress");
    }

    [Fact]
    public void ParseDeploymentStatus_tolerates_a_missing_value_array()
    {
        BcAutomationClient.ParseDeploymentStatus("{}").Should().BeEmpty();
    }

    /// <summary>
    /// The automation URL must carry the tenant id. Microsoft's tenant-less "common
    /// endpoint" form answers a bare 401 for an S2S application token, so dropping the
    /// segment breaks every publish with no diagnosable error.
    /// </summary>
    [Fact]
    public void AutomationBase_addresses_tenant_and_environment()
    {
        var tenant = Guid.Parse("4f07994b-2a2e-4d0d-a17b-9e1b97244f93");
        var url = string.Format(BcConstants.AutomationBaseFormat, tenant, "Test");

        url.Should().Be(
            "https://api.businesscentral.dynamics.com/v2.0/4f07994b-2a2e-4d0d-a17b-9e1b97244f93/Test"
            + "/api/microsoft/automation/v2.0");
    }

    /// <summary>
    /// A denial's cause is in the response body, not the status line. Discarding it is
    /// what left a 401 ("the app isn't on BC's authorized-apps list") and a 403
    /// ("the app lacks permission") indistinguishable in the logs.
    /// </summary>
    [Theory]
    // The Admin Center shape: code and message at the root.
    [InlineData("""{ "code": "Unauthorized", "message": "Application not authorized." }""",
        "Unauthorized: Application not authorized.")]
    // The automation/OData shape: the same two fields nested under "error".
    [InlineData("""{ "error": { "code": "Authentication_InvalidCredentials", "message": "Denied." } }""",
        "Authentication_InvalidCredentials: Denied.")]
    [InlineData("""{ "message": "Just a message." }""", "Just a message.")]
    public void ExtractError_reads_both_envelope_shapes(string json, string expected)
    {
        BcAdminClient.ExtractError(json).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ExtractError_is_empty_when_there_is_nothing_to_report(string body)
    {
        BcAdminClient.ExtractError(body).Should().BeEmpty();
    }

    // ── Full environment record ───────────────────────────────────────────────

    /// <summary>
    /// A list entry with every documented field, including <c>versionDetails</c> and the
    /// soft-delete trio. Shaped from the Microsoft documentation for the environments
    /// endpoint; swap in a captured tenant payload when one is available.
    /// </summary>
    private const string FullEnvironmentPayload = """
    { "value": [ {
        "aadTenantId": "1c3f5a8e-2b47-4e1c-9d6f-8a0b1c2d3e4f",
        "applicationFamily": "BusinessCentral",
        "type": "Production",
        "name": "PROD",
        "friendlyName": "CRONUS Production",
        "countryCode": "DK",
        "webClientLoginUrl": "https://businesscentral.dynamics.com/1c3f5a8e-2b47-4e1c-9d6f-8a0b1c2d3e4f/PROD",
        "webServiceUrl": "https://api.businesscentral.dynamics.com/v2.0/PROD/api/v2.0",
        "status": "Active",
        "locationName": "West Europe",
        "geoName": "Europe",
        "ringName": "PROD",
        "appInsightsKey": "must-not-be-persisted",
        "appSourceAppsUpdateCadence": "UpdatedOnRelease",
        "versionDetails": { "version": "27.5.5.15", "isNewestSupportedVersion": true },
        "gracePeriodStartDate": "2026-09-01T00:00:00Z",
        "enforcedUpdatePeriodStartDate": "2026-10-01T00:00:00Z",
        "softDeletedOn": "2026-08-20T09:30:00Z",
        "hardDeletePendingOn": "2026-09-19T09:30:00Z",
        "deleteReason": "UserRequested"
    } ] }
    """;

    [Fact]
    public void ParseEnvironments_reads_the_whole_environment_record()
    {
        var env = BcAdminClient.ParseEnvironments(FullEnvironmentPayload).Should().ContainSingle().Subject;

        env.Name.Should().Be("PROD");
        env.Type.Should().Be("Production");
        env.FriendlyName.Should().Be("CRONUS Production");
        env.ApplicationFamily.Should().Be("BusinessCentral", "the family is kept verbatim - Microsoft's casing varies per endpoint");
        env.Status.Should().Be("Active");
        env.CountryCode.Should().Be("DK");
        env.AadTenantId.Should().Be(Guid.Parse("1c3f5a8e-2b47-4e1c-9d6f-8a0b1c2d3e4f"));
        env.WebClientLoginUrl.Should().Be("https://businesscentral.dynamics.com/1c3f5a8e-2b47-4e1c-9d6f-8a0b1c2d3e4f/PROD");
        env.LocationName.Should().Be("West Europe");
        env.GeoName.Should().Be("Europe");
        env.RingName.Should().Be("PROD");
        env.AppSourceAppsUpdateCadence.Should().Be("UpdatedOnRelease");
        env.Version.Should().Be("27.5.5.15", "the version comes out of versionDetails");
        env.GracePeriodStartDate.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        env.EnforcedUpdatePeriodStartDate.Should().Be(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        env.SoftDeletedOn.Should().Be(new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc));
        env.HardDeletePendingOn.Should().Be(new DateTime(2026, 9, 19, 9, 30, 0, DateTimeKind.Utc));
        env.DeleteReason.Should().Be("UserRequested");
    }

    [Fact]
    public void ParseEnvironments_tolerates_a_payload_with_the_optional_fields_absent()
    {
        // A null versionDetails is the one that used to be a NullReferenceException
        // waiting to happen, so it's spelled out rather than merely omitted.
        const string json = """
        { "value": [ { "name": "Sandbox", "type": "Sandbox", "versionDetails": null } ] }
        """;

        var env = BcAdminClient.ParseEnvironments(json).Should().ContainSingle().Subject;

        env.Name.Should().Be("Sandbox");
        env.Type.Should().Be("Sandbox");
        env.Status.Should().BeNull();
        env.Version.Should().BeNull();
        env.ApplicationFamily.Should().BeNull();
        env.AadTenantId.Should().BeNull();
        env.SoftDeletedOn.Should().BeNull();
    }

    [Fact]
    public void ParseEnvironment_reads_the_by_name_response_without_a_geo_name()
    {
        // The by-name endpoint returns the object directly and omits geoName.
        const string json = """
        { "name": "PROD", "type": "Production", "status": "Upgrading", "versionDetails": { "version": "27.5.5.15" } }
        """;

        var env = BcAdminClient.ParseEnvironment(json);

        env.Should().NotBeNull();
        env!.Name.Should().Be("PROD");
        env.Status.Should().Be("Upgrading");
        env.Version.Should().Be("27.5.5.15");
        env.GeoName.Should().BeNull("the by-name response doesn't carry it");
    }

    [Fact]
    public void ParseEnvironment_also_accepts_the_list_envelope()
    {
        BcAdminClient.ParseEnvironment("""{ "value": [ { "name": "PROD", "type": "Production" } ] }""")!
            .Name.Should().Be("PROD");
    }

    [Theory]
    [InlineData("Active", BcEnvironmentReadiness.Ready)]
    [InlineData("active", BcEnvironmentReadiness.Ready)]
    [InlineData("Upgrading", BcEnvironmentReadiness.Busy)]
    [InlineData("Preparing", BcEnvironmentReadiness.Busy)]
    [InlineData("NotReady", BcEnvironmentReadiness.Busy)]
    [InlineData("Recovering", BcEnvironmentReadiness.Busy)]
    [InlineData("Removing", BcEnvironmentReadiness.Deleting)]
    [InlineData("SoftDeleting", BcEnvironmentReadiness.Deleting)]
    [InlineData("SoftDeleted", BcEnvironmentReadiness.Deleting)]
    [InlineData("PreparingFailed", BcEnvironmentReadiness.Failed)]
    [InlineData("upgradingfailed", BcEnvironmentReadiness.Failed)]
    [InlineData("", BcEnvironmentReadiness.Unknown)]
    [InlineData(null, BcEnvironmentReadiness.Unknown)]
    [InlineData("SomethingMicrosoftAddedLater", BcEnvironmentReadiness.Unknown)]
    public void Classify_maps_the_status_strings_case_insensitively(string? status, BcEnvironmentReadiness expected)
    {
        BcEnvironmentStatus.Classify(status).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_or_absent_status_does_not_block_publishing()
    {
        BcEnvironmentStatus.CanPublish(null).Should().BeTrue("rows fetched before the status was captured must still be releasable");
        BcEnvironmentStatus.CanPublish("SomethingMicrosoftAddedLater").Should().BeTrue();
        BcEnvironmentStatus.CanPublish("Upgrading").Should().BeFalse();
    }

    [Fact]
    public void The_refusal_message_names_the_environment_and_the_status()
    {
        BcEnvironmentStatus.RefusalMessage("PROD", "Upgrading").Should().Contain("PROD").And.Contain("Upgrading");
        BcEnvironmentStatus.RefusalMessage("PROD", "UpgradingFailed").Should().Contain("failed state in Business Central");
        BcEnvironmentStatus.RefusalMessage("PROD", "Active").Should().BeNull();
    }
}
