using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// A pipeline build's Release is stored with <c>kind = project</c>, and the product
/// calls the thing a Solution. The Solutions rename (#725) rewrote the literal in three
/// pages to <c>"solution"</c>; it compiled, matched no row, and the build results, the
/// rebuild and retry actions and the "Pipeline build" wording all went quiet for weeks.
/// A search-and-replace will be tempted again, so the comparison goes through
/// <see cref="OeRelease.ProjectBuildKind"/> and this test refuses the literal.
/// </summary>
public class ReleaseKindLiteralTests
{
    private static readonly Regex KindAgainstSolution = new(
        @"[Kk]ind\s*(==|!=|:)\s*""solutions?""|^\s*""solutions?""\s*=>",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void The_stored_kind_is_project()
    {
        OeRelease.ProjectBuildKind.Should().Be("project");
    }

    [Fact]
    public void No_source_file_compares_a_kind_with_the_word_solution()
    {
        var app = RepoRoot.Combine("ALDevToolbox");
        var offenders = Directory
            .EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => KindAgainstSolution.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(app, f))
            .ToList();

        offenders.Should().BeEmpty(
            "a Release's kind is stored as \"project\" - compare with OeRelease.ProjectBuildKind");
    }
}
