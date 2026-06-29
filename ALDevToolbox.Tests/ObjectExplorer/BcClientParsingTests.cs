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
}
