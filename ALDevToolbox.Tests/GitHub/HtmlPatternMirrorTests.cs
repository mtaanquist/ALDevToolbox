using System.Text.RegularExpressions;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The regexes this milestone renders into an HTML <c>pattern</c> attribute
/// have to compile in a browser as well as in .NET, which is not the same
/// grammar.
///
/// <para>Browsers compile <c>pattern</c> with the RegExp <c>v</c> flag (Chrome
/// 112+, Firefox 116+, Safari 17+). Under <c>v</c> a bare <c>-</c> inside a
/// character class is a <c>SyntaxError</c> wherever it sits, trailing included,
/// and a pattern that does not compile is <em>discarded</em> - the field then
/// reports itself valid for input the server refuses. That failure is silent,
/// which is why it needs a test: the .NET <see cref="Regex"/> checks elsewhere
/// accept the same string happily.</para>
///
/// <para>Only the two constants this milestone owns are checked. The same
/// defect exists on <c>main</c> in three unrelated pages and is being tracked
/// separately - widening this test would fail the build for work that is not
/// this one's to do.</para>
/// </summary>
public sealed class HtmlPatternMirrorTests
{
    public static TheoryData<string, string> MirroredPatterns() => new()
    {
        { nameof(GitHubWorkspaceRepositoryService.NamePattern), GitHubWorkspaceRepositoryService.NamePattern },
        { nameof(SystemSettingsService.GitHubAppSlugPattern), SystemSettingsService.GitHubAppSlugPattern },
        { nameof(SystemSettingsService.GitHubAppIdPattern), SystemSettingsService.GitHubAppIdPattern },
    };

    [Theory]
    [MemberData(nameof(MirroredPatterns))]
    public void A_pattern_the_form_mirrors_compiles_in_a_browser_too(string name, string pattern)
    {
        UnicodeSetsRegex.Reject(pattern)
            .Should().BeNull($"{name} is rendered into a pattern= attribute and a browser silently drops one it cannot compile");
    }

    [Theory]
    [MemberData(nameof(MirroredPatterns))]
    public void A_pattern_the_form_mirrors_is_also_a_valid_dotnet_regex(string name, string pattern)
    {
        var act = () => _ = new Regex($"^{pattern.Trim('^', '$')}$");
        act.Should().NotThrow($"{name} is the server rule as well as the browser rule");
    }

    // --- the validator's own tests -----------------------------------------
    //
    // A checker nobody has seen fail is not evidence of anything, so the shapes
    // it has to catch are pinned here - starting with the two constants as they
    // were written before this fix.

    [Theory]
    [InlineData(@"^(?!\.{1,2}$)[A-Za-z0-9._-]{1,100}$")]   // the repository-name rule as it shipped
    [InlineData("[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?")] // the app-slug rule as it shipped
    [InlineData("[-abc]")]
    [InlineData("[a-z-0]")]
    [InlineData("[abc(]")]
    public void The_checker_catches_what_a_browser_would_refuse(string pattern)
    {
        UnicodeSetsRegex.Reject(pattern).Should().NotBeNull();
    }

    [Theory]
    [InlineData(@"[A-Za-z0-9._\-]")]
    [InlineData(@"[\-abc]")]
    [InlineData("[^A-Za-z]")]
    [InlineData(@"^[0-9]{1,4}-[0-9]{1,4}$")] // outside a class a hyphen needs nothing
    [InlineData(@"[\]\[]")]
    public void The_checker_accepts_what_a_browser_compiles(string pattern)
    {
        UnicodeSetsRegex.Reject(pattern).Should().BeNull();
    }
}

/// <summary>
/// The half of the RegExp <c>v</c>-flag grammar that catches the mistake we
/// actually make: inside a character class, <c>v</c> reserves the punctuation
/// that could start a set operation, so every one of them has to be escaped -
/// a <c>-</c> unless it separates two members of a range, and <c>( ) [ { } / |</c>
/// always.
///
/// <para>Deliberately not a regex engine. It reads one character class at a
/// time and reports the first character a browser would refuse, which is enough
/// to keep a <c>pattern</c> attribute honest without pretending to implement
/// ECMA-262.</para>
/// </summary>
internal static class UnicodeSetsRegex
{
    /// <summary>The reason a browser would refuse <paramref name="pattern"/>, or null when it would compile it.</summary>
    internal static string? Reject(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (c != '[') continue;

            var reason = RejectClass(pattern, ref i);
            if (reason is not null) return reason;
        }
        return null;
    }

    /// <summary>
    /// Reads the class starting at <paramref name="i"/> (which points at its
    /// <c>[</c>) and leaves <paramref name="i"/> on its <c>]</c>.
    /// </summary>
    private static string? RejectClass(string pattern, ref int i)
    {
        var start = ++i;
        if (i < pattern.Length && pattern[i] == '^') i++;

        while (i < pattern.Length && pattern[i] != ']')
        {
            // An atom: an escape, or one literal character that v allows to be one.
            var c = pattern[i];
            if (Reserved.Contains(c))
            {
                return $"'{c}' at index {i} has to be escaped inside a character class under the v flag "
                    + $"(in '{pattern[start..]}')";
            }
            Consume(pattern, ref i);

            // A range: the hyphen is legal only between two atoms, so it and the
            // atom after it are read here rather than being met on their own at
            // the top of the loop - which is exactly where a bare one lands.
            if (i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']' && pattern[i + 1] != '-')
            {
                i++;
                Consume(pattern, ref i);
            }
        }
        return null;
    }

    /// <summary>Steps past one atom: two characters for an escape, one otherwise.</summary>
    private static void Consume(string pattern, ref int i) => i += pattern[i] == '\\' ? 2 : 1;

    /// <summary>
    /// The characters <c>v</c> refuses to read as literals inside a class. The
    /// hyphen is in the list because everything legal about it is consumed as a
    /// range above; anything that reaches the atom position is a bare one.
    /// </summary>
    private static readonly HashSet<char> Reserved = ['-', '(', ')', '[', '{', '}', '/', '|'];
}
