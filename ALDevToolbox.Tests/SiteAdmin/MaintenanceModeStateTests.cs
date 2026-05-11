using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// <see cref="MaintenanceModeState"/> is a tiny singleton, but the
/// middleware in Program.cs reads it on every request — pin the toggle
/// contract so a regression there (e.g. forgetting to lift the flag on
/// a restore failure) doesn't ship silently.
/// </summary>
public sealed class MaintenanceModeStateTests
{
    [Fact]
    public void Enter_sets_reason_and_marks_active()
    {
        var state = new MaintenanceModeState();
        state.IsActive.Should().BeFalse();

        state.Enter("Restoring backup x");

        state.IsActive.Should().BeTrue();
        state.Reason.Should().Be("Restoring backup x");
        state.StartedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Exit_clears_reason()
    {
        var state = new MaintenanceModeState();
        state.Enter("Restoring");

        state.Exit();

        state.IsActive.Should().BeFalse();
        state.Reason.Should().BeNull();
    }
}
