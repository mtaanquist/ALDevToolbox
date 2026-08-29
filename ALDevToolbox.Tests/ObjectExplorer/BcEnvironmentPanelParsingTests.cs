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
