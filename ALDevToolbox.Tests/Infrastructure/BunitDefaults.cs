using System.Runtime.CompilerServices;
using Bunit;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Process-wide bUnit settings, applied before any test class constructs its
/// <see cref="TestContext"/>.
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
/// Ten seconds costs nothing on the happy path - the wait returns the moment
/// the assertion passes - and only changes how long a genuinely failing test
/// takes to report.
/// </summary>
internal static class BunitDefaults
{
    [ModuleInitializer]
    internal static void Apply() => TestContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}
