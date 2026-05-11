using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Validation;

/// <summary>
/// Pins down <see cref="ValidationPatterns.Key"/>. Three services (templates,
/// modules, application versions) and three admin form HTML <c>pattern</c>
/// attributes all reference it; a regression here would break every key
/// validator at once.
/// </summary>
public sealed class ValidationPatternsTests
{
    [Theory]
    [InlineData("template")]
    [InlineData("acme-customer")]
    [InlineData("runtime-15")]
    [InlineData("a-b-c-1-2-3")]
    [InlineData("0")]
    public void Key_accepts_lowercase_alphanumeric_with_hyphens(string value)
    {
        ValidationPatterns.Key.IsMatch(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("dot.notation")]
    [InlineData("trailing-newline\n")]
    public void Key_rejects_anything_outside_lowercase_alphanumeric_and_hyphen(string value)
    {
        ValidationPatterns.Key.IsMatch(value).Should().BeFalse();
    }
}
