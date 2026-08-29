using System.Runtime.CompilerServices;
using Bunit;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Process-wide bUnit settings, applied before any test class constructs its
/// <see cref="BunitContext"/>.
///
/// bUnit's <c>WaitForAssertion</c> / <c>WaitForElement</c> give up after one
/// second by default. That is generous for a component that renders from
/// memory and far too tight for the ones here that render from the
/// Testcontainers Postgres: on a busy CI runner the dashboard's audit query
/// alone can take longer than that, and the wait then fails with "render
/// count 1" - the data render simply had not happened yet. It bit
/// <c>AdminDashboardTests.A_recent_change_names_the_person_who_made_it</c>
/// on a green commit (#619's rebase run) while three builds shared the
/// runners; the same SHA passed on the re-run.
///
/// Ten seconds was not enough either. #653's pull_request run failed
/// <c>AdminTemplateEditTests.Save_with_valid_edits_persists_to_the_database_and_clears_FieldErrors</c>
/// waiting for the save banner, while the identical commit passed on the
/// push run minutes earlier. The difference is contention, not code: a
/// pull_request event also runs <c>migration-forward-compat</c>, so three
/// heavy jobs share the runners instead of two - the same shape as the #619
/// case above, one job further along.
///
/// Thirty seconds costs nothing on the happy path - the wait returns the
/// moment the assertion passes - and only changes how long a genuinely
/// failing test takes to report. That is the right trade for a wait whose
/// job is to absorb a slow runner: too short turns a busy CI box into a red
/// build, while too long is paid only by tests that were going to fail.
/// </summary>
internal static class BunitDefaults
{
    [ModuleInitializer]
    internal static void Apply() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(30);
}
