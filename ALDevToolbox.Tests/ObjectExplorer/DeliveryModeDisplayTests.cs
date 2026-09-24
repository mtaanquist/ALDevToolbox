using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The schedule wording on a deployment pipeline's card and list row names its trigger,
/// so "Right away" can't read as "as soon as a build lands" (maintainer, 2026-09-24).
/// The short wording stays for the Deploy dialogs, which supply the verb themselves.
/// </summary>
public sealed class DeliveryModeDisplayTests
{
    [Theory]
    [InlineData(BcDeploymentSchedule.Immediate, "Installs right away")]
    [InlineData(BcDeploymentSchedule.OurDeliveryWindow, "Installs in the delivery window")]
    [InlineData(BcDeploymentSchedule.UpdateWindow, "Installs in the Business Central update window")]
    [InlineData(BcDeploymentSchedule.NextMinorUpdate, "Installs with the next minor Business Central update")]
    [InlineData(BcDeploymentSchedule.NextMajorUpdate, "Installs with the next major Business Central update")]
    public void Sentence_names_the_trigger(string stored, string expected) =>
        DeliveryModeDisplay.ScheduleSentence(stored).Should().Be(expected);

    [Fact]
    public void Sentence_shows_an_unknown_value_rather_than_blank() =>
        DeliveryModeDisplay.ScheduleSentence("Someday").Should().Be("Someday");

    [Fact]
    public void Short_wording_is_unchanged_for_the_dialogs() =>
        DeliveryModeDisplay.Schedule(BcDeploymentSchedule.Immediate).Should().Be("Right away");
}
