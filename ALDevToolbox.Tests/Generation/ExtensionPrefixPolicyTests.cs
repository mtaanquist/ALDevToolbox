using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// The one rule that decides what <c>{{extension_prefix}}</c> renders to, which
/// the New Workspace form, the generation endpoint and the
/// <c>generate_workspace</c> MCP tool all resolve through. Specified in
/// <c>.design/customer-naming.md</c>.
/// </summary>
public sealed class ExtensionPrefixPolicyTests
{
    private static OrganizationSettings Settings(ExtensionPrefixMode mode, string? orgPrefix = null) =>
        new() { ExtensionPrefixMode = mode, ExtensionPrefix = orgPrefix };

    [Fact]
    public void Hidden_uses_the_short_name_and_ignores_what_was_typed()
    {
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.Hidden, "PARTNER"), "TYPED", "JM", "Jørgensen Møbler")
            .Should().Be("JM");
    }

    [Fact]
    public void Hidden_falls_back_to_the_customer_name_when_there_is_no_short_name()
    {
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.Hidden), null, null, "Jørgensen Møbler")
            .Should().Be("Jørgensen Møbler");
    }

    [Fact]
    public void Fixed_uses_the_organisations_value_whatever_the_caller_passed()
    {
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.Fixed, "PARTNER"), "TYPED", "JM", "Jørgensen Møbler")
            .Should().Be("PARTNER");
    }

    [Fact]
    public void Fixed_without_a_value_still_beats_an_empty_prefix()
    {
        // An organisation cannot save Fixed with no value, but a row written
        // before the setting existed can look like this - and " Core" is not a
        // name anyone wants.
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.Fixed), "TYPED", "JM", "Jørgensen Møbler")
            .Should().Be("JM");
    }

    [Fact]
    public void PerWorkspace_takes_what_was_typed()
    {
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.PerWorkspace, "PARTNER"), " TYPED ", "JM", "Jørgensen Møbler")
            .Should().Be("TYPED");
    }

    [Fact]
    public void PerWorkspace_falls_back_to_the_organisations_value_then_the_short_name()
    {
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.PerWorkspace, "PARTNER"), null, "JM", "Jørgensen Møbler")
            .Should().Be("PARTNER");
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.PerWorkspace), "  ", "JM", "Jørgensen Møbler")
            .Should().Be("JM");
        ExtensionPrefixPolicy
            .Resolve(Settings(ExtensionPrefixMode.PerWorkspace), null, null, "Jørgensen Møbler")
            .Should().Be("Jørgensen Møbler");
    }

    [Theory]
    [InlineData(ExtensionPrefixMode.Hidden, false)]
    [InlineData(ExtensionPrefixMode.Fixed, false)]
    [InlineData(ExtensionPrefixMode.PerWorkspace, true)]
    public void Only_the_per_workspace_policy_puts_the_field_on_the_form(ExtensionPrefixMode mode, bool asked)
    {
        ExtensionPrefixPolicy.IsAskedFor(mode).Should().Be(asked);
    }
}
