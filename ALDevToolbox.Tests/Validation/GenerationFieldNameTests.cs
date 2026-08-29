using System.Text.RegularExpressions;
using ALDevToolbox.Endpoints;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Validation;

/// <summary>
/// Guards the last-resort validation page's field labels (#546).
///
/// The generator forms validate inline and almost never reach that page, which
/// is exactly why it rots: a key added to <c>GenerationService</c>'s validators
/// falls through <c>FriendlyFieldName</c> to its raw C# property name, and the
/// one user who ever sees it reads "CoreIdRangeFrom — Must be greater than
/// zero." Four keys had drifted that way before this test existed.
///
/// The key set is read from the <b>validator</b> rather than restated here, so
/// adding a rule fails this test until its field has a name a user would
/// recognise.
/// </summary>
public sealed class GenerationFieldNameTests
{
    [Fact]
    public void Every_key_the_plan_validators_can_throw_has_a_friendly_name()
    {
        var keys = ValidatorKeys();
        keys.Should().Contain(["WorkspaceName", "TemplateKey", "CoreIdRangeFrom", "RuntimeVersion"],
            because: "these are the rules the two validators are built around; " +
                     "finding none of them means the source shape changed, not that the rules went away");

        // Not "the label differs from the key" -- Publisher is genuinely the word
        // the form shows, and an identity mapping there is correct. The property
        // that matters is that someone DECIDED, i.e. the key has its own arm.
        var mapped = MappedKeys();
        foreach (var key in keys)
        {
            mapped.Should().Contain(key,
                because: $"\"{key}\" is a C# property name reaching a user; give it an arm in " +
                         "FriendlyFieldName, even if the label is the same word");
        }
    }

    /// <summary>The keys FriendlyFieldName's switch names explicitly.</summary>
    private static string[] MappedKeys()
    {
        var source = File.ReadAllText(Path.Combine(Root(),
            "ALDevToolbox", "Endpoints", "EndpointHelpers.cs"));
        var body = source[source.IndexOf("string FriendlyFieldName", StringComparison.Ordinal)..];
        return Regex.Matches(body, @"""(\w+)""\s*=>")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Per-dependency rules key on an index, so they cannot be listed literally.
    /// The label has to name the row the user should go and look at.
    /// </summary>
    [Theory]
    [InlineData("Dependencies[0].DepId", "Dependency 1, ID")]
    [InlineData("Dependencies[2].DepId", "Dependency 3, ID")]
    [InlineData("Dependencies[1].DepVersion", "Dependency 2, version")]
    public void Indexed_dependency_keys_name_the_row_one_based(string key, string expected) =>
        EndpointHelpers.FriendlyFieldName(key).Should().Be(expected,
            because: "the plan indexes dependencies from zero and the form lists them from one");

    /// <summary>
    /// Every <c>errors[nameof(plan.X)]</c> in the two plan validators. Reads the
    /// source because the alternative — provoking each rule through the public
    /// method — needs a full plan per rule and would still miss a new one.
    /// </summary>
    private static string[] ValidatorKeys()
    {
        var source = File.ReadAllText(Path.Combine(Root(),
            "ALDevToolbox", "Services", "GenerationService.cs"));
        return Regex.Matches(source, @"errors\[nameof\(plan\.(\w+)\)\]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the tests run from inside the repo");
        return dir!.FullName;
    }
}
