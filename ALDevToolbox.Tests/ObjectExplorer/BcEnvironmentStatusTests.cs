using ALDevToolbox.Services.ObjectExplorer.Bc;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The environment-status vocabulary shared by the project page, the release-pipeline
/// editor, the release-pipeline validation and the delivery gate. Two properties matter
/// and neither is obvious from the code: a status Microsoft adds later must not block
/// publishing on its own, and it must never reach a screen as a raw camelCase token.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcEnvironmentStatusTests
{
    [Theory]
    [InlineData("Active", BcEnvironmentReadiness.Ready)]
    [InlineData("active", BcEnvironmentReadiness.Ready)]
    [InlineData("Upgrading", BcEnvironmentReadiness.Busy)]
    [InlineData("Preparing", BcEnvironmentReadiness.Busy)]
    [InlineData("SoftDeleted", BcEnvironmentReadiness.Deleting)]
    [InlineData("UpgradingFailed", BcEnvironmentReadiness.Failed)]
    [InlineData("PreparingFailed", BcEnvironmentReadiness.Failed)]
    public void Classify_reads_the_documented_statuses(string status, BcEnvironmentReadiness expected)
    {
        BcEnvironmentStatus.Classify(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomethingMicrosoftAddedLater")]
    public void An_unknown_or_absent_status_does_not_block_publishing(string? status)
    {
        // Rows fetched before the status was captured have none, and a status we don't
        // recognise is not evidence of a problem. Refusing either would ground every
        // release for a reason nobody could act on.
        BcEnvironmentStatus.Classify(status).Should().Be(BcEnvironmentReadiness.Unknown);
        BcEnvironmentStatus.CanPublish(status).Should().BeTrue();
        BcEnvironmentStatus.RefusalMessage("Production", status).Should().BeNull();
    }

    [Theory]
    [InlineData("Upgrading")]
    [InlineData("SoftDeleted")]
    [InlineData("UpgradingFailed")]
    public void A_blocking_status_refuses_by_name(string status)
    {
        var message = BcEnvironmentStatus.RefusalMessage("Production", status);

        message.Should().NotBeNull();
        message.Should().Contain("Production").And.Contain(status,
            "the consultant has to match the refusal against what the admin center shows them");
    }

    [Theory]
    [InlineData("SoftDeleted", "Soft deleted")]
    [InlineData("NotReady", "Not ready")]
    [InlineData("UpgradingFailed", "Upgrading failed")]
    [InlineData("Active", "Active")]
    public void Humanise_splits_the_machine_casing(string status, string expected)
    {
        BcEnvironmentStatus.Humanise(status).Should().Be(expected);
    }

    [Fact]
    public void Humanise_never_leaves_a_new_status_reading_as_a_wire_token()
    {
        // The point of having no translation table: a status Microsoft ships tomorrow
        // still reaches the screen as words, without anyone editing a mapping.
        BcEnvironmentStatus.Humanise("SomethingMicrosoftAddedLater")
            .Should().Be("Something microsoft added later")
            .And.NotContain("dL", "the camel humps are what make it read as a token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Humanise_returns_empty_for_no_status_so_callers_can_say_not_checked_yet(string? status)
    {
        // Both call sites branch on this to show "Not checked yet" rather than a blank.
        BcEnvironmentStatus.Humanise(status).Should().BeEmpty();
    }
}
