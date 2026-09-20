using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Parsing of the Microsoft API JSON envelopes the BC delivery clients consume — the
/// Admin Center <c>environments</c> list.
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

    /// <summary>
    /// Microsoft returns the three deletion fields in PascalCase beside camelCase
    /// neighbours. A case-sensitive lookup reads every deleted environment as one with no
    /// deletion dates at all - which is silent: the row still parses, it just loses the
    /// only two dates that say how long there is left to recover it.
    /// </summary>
    [Fact]
    public void ParseEnvironments_reads_the_deletion_fields_in_the_casing_Microsoft_sends()
    {
        const string json = """
        { "value": [
            { "name": "JLE-260911110359", "type": "Sandbox", "status": "SoftDeleted",
              "SoftDeletedOn": "2026-09-20T09:03:59Z",
              "HardDeletePendingOn": "2026-10-04T09:03:59Z",
              "DeleteReason": "Deleted by the customer" }
        ] }
        """;

        var env = BcAdminClient.ParseEnvironments(json).Should().ContainSingle().Subject;

        env.SoftDeletedOn.Should().Be(new DateTime(2026, 9, 20, 9, 3, 59, DateTimeKind.Utc));
        env.HardDeletePendingOn.Should().Be(new DateTime(2026, 10, 4, 9, 3, 59, DateTimeKind.Utc));
        env.DeleteReason.Should().Be("Deleted by the customer");
    }

    /// <summary>
    /// The other casing has to keep working too: this API family has drifted on exactly
    /// this before, so neither spelling is the one we depend on.
    /// </summary>
    [Fact]
    public void ParseEnvironments_reads_the_deletion_fields_in_camel_case_too()
    {
        const string json = """
        { "value": [
            { "name": "JLE", "type": "Sandbox", "status": "softdeleted",
              "softDeletedOn": "2026-09-20T09:03:59Z",
              "hardDeletePendingOn": "2026-10-04T09:03:59Z",
              "deleteReason": "Deleted by the customer" }
        ] }
        """;

        var env = BcAdminClient.ParseEnvironments(json).Should().ContainSingle().Subject;

        env.SoftDeletedOn.Should().Be(new DateTime(2026, 9, 20, 9, 3, 59, DateTimeKind.Utc));
        env.HardDeletePendingOn.Should().Be(new DateTime(2026, 10, 4, 9, 3, 59, DateTimeKind.Utc));
        env.DeleteReason.Should().Be("Deleted by the customer");
        // The status is kept verbatim; every reader of it compares case-insensitively.
        env.Status.Should().Be("softdeleted");
        BcEnvironmentStatus.IsSoftDeleted(env.Status).Should().BeTrue();
    }

    /// <summary>A live environment carries none of the three, and must not invent them.</summary>
    [Fact]
    public void ParseEnvironments_leaves_the_deletion_fields_empty_for_a_live_environment()
    {
        const string json = """{ "value": [ { "name": "Production", "type": "Production", "status": "Active" } ] }""";

        var env = BcAdminClient.ParseEnvironments(json).Should().ContainSingle().Subject;

        env.SoftDeletedOn.Should().BeNull();
        env.HardDeletePendingOn.Should().BeNull();
        env.DeleteReason.Should().BeNull();
    }
}
