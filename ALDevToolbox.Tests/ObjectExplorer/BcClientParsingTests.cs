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
}
