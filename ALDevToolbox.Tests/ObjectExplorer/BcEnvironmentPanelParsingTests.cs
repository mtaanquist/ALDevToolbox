using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The two read-only endpoints the environment panel adds: available Marketplace app
/// updates, and the platform versions coming to an environment. Both answer with shapes
/// that vary per row — an unreleased BC version carries no schedule block, an app update
/// may or may not carry prerequisites — so the parsers are pinned against both.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcEnvironmentPanelParsingTests
{
    [Fact]
    public void ParseAvailableUpdates_reads_an_entry_and_its_prerequisites()
    {
        const string json = """
        { "value": [
            { "appId": "11111111-1111-1111-1111-111111111111", "name": "Contoso Reports",
              "publisher": "Contoso", "version": "3.1.0.0",
              "requirements": [
                { "appId": "22222222-2222-2222-2222-222222222222", "name": "Contoso Core",
                  "publisher": "Contoso", "version": "3.0.0.0", "type": "Update" }
              ] },
            { "appId": "33333333-3333-3333-3333-333333333333", "name": "Standalone", "publisher": "Vendor", "version": "1.2.0.0" }
        ] }
        """;

        var updates = BcAppManagementClient.ParseAvailableUpdates(json);

        updates.Should().HaveCount(2);
        var first = updates[0];
        first.Name.Should().Be("Contoso Reports");
        first.Version.Should().Be("3.1.0.0");
        first.Requirements.Should().ContainSingle().Which.Name.Should().Be("Contoso Core");
        updates[1].Requirements.Should().BeEmpty("an update with no prerequisites is the normal case");
    }

    [Fact]
    public void ParseAvailableUpdates_tolerates_an_empty_or_missing_envelope()
    {
        BcAppManagementClient.ParseAvailableUpdates("""{ "value": [] }""").Should().BeEmpty();
        BcAppManagementClient.ParseAvailableUpdates("{}").Should().BeEmpty();
    }

    [Fact]
    public void ParseEnvironmentUpdates_reads_a_scheduled_version_and_an_unreleased_one()
    {
        const string json = """
        { "value": [
            { "targetVersion": "27.5", "available": true, "selected": true, "updateStatus": "scheduled",
              "scheduleDetails": {
                  "latestSelectableDateTime": "2026-09-30T00:00:00Z",
                  "selectedDateTime": "2026-09-12T22:00:00Z",
                  "ignoreUpdateWindow": false, "rolloutStatus": "Active" },
              "targetVersionType": "GA" },
            { "targetVersion": "27.6", "available": false, "selected": false,
              "expectedAvailability": { "month": 10, "year": 2026 }, "targetVersionType": "GA" }
        ] }
        """;

        var updates = BcAdminClient.ParseEnvironmentUpdates(json);

        var scheduled = updates.Single(u => u.TargetVersion == "27.5");
        scheduled.Selected.Should().BeTrue();
        scheduled.Available.Should().BeTrue();
        scheduled.SelectedDateTime.Should().NotBeNull();
        scheduled.RolloutStatus.Should().Be("Active");

        var future = updates.Single(u => u.TargetVersion == "27.6");
        future.Available.Should().BeFalse();
        future.SelectedDateTime.Should().BeNull("an unreleased version carries no schedule block");
        future.ExpectedAvailability.Should().Be("October 2026",
            "a consultant asked when the customer gets it, not which month number it is");
    }

    [Fact]
    public void ParseEnvironmentOperations_reads_microsofts_documented_shape()
    {
        // The example from the admin center API's "Get all environment operations".
        const string json = """
            {
              "value": [
                {
                  "id": "552d3cb2-144e-4195-9a92-1043c4f483e9",
                  "type": "environmentAppInstall",
                  "status": "succeeded",
                  "aadTenantId": "aaaabbbb-0000-cccc-1111-dddd2222eeee",
                  "createdOn": "2021-03-22T15:45:46.537Z",
                  "errorMessage": "",
                  "parameters": {
                    "appId": "44445555-eeee-6666-ffff-7777aaaa8888",
                    "targetAppVersion": "17.0.3.0",
                    "allowPreviewVersion": true,
                    "nested": { "ignored": 1 }
                  }
                },
                {
                  "id": "5fe4ac38-a523-4c1f-80db-acd2cf848c09",
                  "type": "environmentRename",
                  "status": "succeeded",
                  "createdOn": "2021-03-16T18:57:36.223Z",
                  "startedOn": "2021-03-16T18:57:39.053Z",
                  "completedOn": "2021-03-16T18:57:47.867Z",
                  "createdBy": "",
                  "errorMessage": "",
                  "parameters": { "oldEnvironmentName": "Production", "newEnvironmentName": "Production-deprecated" }
                },
                { "status": "succeeded" }
              ]
            }
            """;

        var operations = BcAdminClient.ParseEnvironmentOperations(json);

        operations.Should().HaveCount(2, "a row with no type says nothing a person could read");
        operations[0].Type.Should().Be("environmentAppInstall");
        operations[0].StartedOn.Should().BeNull();
        operations[0].Parameter("APPID").Should().Be("44445555-eeee-6666-ffff-7777aaaa8888");
        operations[0].Parameter("allowPreviewVersion").Should().Be("true");
        operations[0].Parameter("nested").Should().BeNull();
        operations[1].CompletedOn.Should().Be(new DateTimeOffset(2021, 3, 16, 18, 57, 47, 867, TimeSpan.Zero));
        BcEnvironmentOperationDisplay.Headline(operations[1]).Should().Be("Renamed to Production-deprecated");
        BcEnvironmentOperationDisplay.Headline(operations[0]).Should().Be("Installed an app 17.0.3.0");
    }

    [Fact]
    public void ParseTenantStorage_reads_each_size_and_the_tenants_total_and_skips_a_size_bc_could_not_work_out()
    {
        const string used = """
            { "value": [
              { "environmentType": "Production", "environmentName": "Production", "applicationFamily": "BusinessCentral", "databaseStorageInKilobytes": 52428800 },
              { "environmentType": "Sandbox", "environmentName": "Sandbox", "applicationFamily": "BusinessCentral", "databaseStorageInKilobytes": -1 },
              { "environmentType": "Sandbox", "environmentName": "Big", "applicationFamily": "BusinessCentral", "databaseStorageInKilobytes": 5000000000 }
            ] }
            """;
        const string quotas = """
            { "environmentsCount": { "production": 1, "sandbox": 3 },
              "storageInKilobytes": { "default": 83886080, "userLicenses": 0, "additionalCapacity": 0, "total": 83886080 } }
            """;

        var storage = BcAdminClient.ParseTenantStorage(used, quotas);

        storage.AllowedKilobytes.Should().Be(83886080);
        storage.DatabaseKilobytesByEnvironment.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["Production"] = 52428800,
            ["Big"] = 5000000000, // past 32 bits: the API moved this to 64 in v2.26
        });
        storage.DatabaseKilobytesByEnvironment.ContainsKey("production").Should().BeTrue("environment names are matched without regard to case");
    }

    [Fact]
    public void ParseTenantStorage_reads_nothing_useful_as_nothing_rather_than_as_zero()
    {
        var storage = BcAdminClient.ParseTenantStorage("{}", "{}");

        storage.AllowedKilobytes.Should().BeNull("an allowance of zero would paint every customer red");
        storage.DatabaseKilobytesByEnvironment.Should().BeEmpty();
    }

    [Fact]
    public void ParseEnvironmentOperations_reads_an_answer_with_no_list_as_empty()
    {
        BcAdminClient.ParseEnvironmentOperations("{}").Should().BeEmpty();
        BcAdminClient.ParseEnvironmentOperations("").Should().BeEmpty();
    }

    [Fact]
    public void ParseEnvironmentUpdates_reads_flags_whatever_their_casing()
    {
        // This host has answered with string booleans before, so neither form may throw.
        const string json = """
        { "value": [ { "targetVersion": "27.5", "available": "TRUE", "selected": "false", "updateStatus": "Scheduled" } ] }
        """;

        var update = BcAdminClient.ParseEnvironmentUpdates(json).Single();

        update.Available.Should().BeTrue();
        update.Selected.Should().BeFalse();
        update.UpdateStatus.Should().Be("Scheduled");
    }

    [Fact]
    public void ParseEnvironmentUpdates_skips_rows_without_a_version_and_tolerates_an_empty_envelope()
    {
        BcAdminClient.ParseEnvironmentUpdates("""{ "value": [ { "available": true } ] }""").Should().BeEmpty();
        BcAdminClient.ParseEnvironmentUpdates("{}").Should().BeEmpty();
        BcAdminClient.ParseEnvironmentUpdates("").Should().BeEmpty();
    }

    [Fact]
    public void ParseEnvironmentUpdates_refuses_a_body_that_is_not_json()
    {
        var act = () => BcAdminClient.ParseEnvironmentUpdates("<html>502</html>");

        act.Should().Throw<BcApiException>();
    }
}
