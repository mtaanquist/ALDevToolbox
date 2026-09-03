using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Guards the CI configuration itself. The eight <c>[PgToolFact]</c> backup
/// tests skip when <c>pg_dump</c>/<c>pg_restore</c> are older than
/// <see cref="PgToolAvailability.MinimumMajorVersion"/>, which is the right
/// behaviour on a developer machine — but on CI it meant they never ran at
/// all (#666). Skipping stays opt-out locally; in CI a missing or old tool is
/// a failure, so a dropped apt step is loud instead of silent.
/// </summary>
public class PgToolAvailabilityTests
{
    [Fact]
    public void When_running_in_CI_the_pg_client_tools_are_present_and_current()
    {
        // ALDT_TEST_POSTGRES_CONNECTION is only set by the CI workflow (the
        // service-container connection string); locally the fixture starts a
        // Testcontainer instead and this test is a no-op.
        var ciConnection = Environment.GetEnvironmentVariable("ALDT_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(ciConnection))
        {
            return;
        }

        PgToolAvailability.MissingToolReason.Should().BeNull(
            $"CI must install postgresql-client-{PgToolAvailability.MinimumMajorVersion} and prepend it to PATH " +
            "so the backup tests actually execute — see the 'Install PostgreSQL 18 client tools' " +
            "step in .github/workflows/build.yml");
    }
}
