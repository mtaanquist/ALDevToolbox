using ALDevToolbox.Endpoints;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Zip-slip guard for the recipe ZIP download: <see cref="CookbookEndpoints.BuildSafeEntryPath"/>
/// must never emit an entry name that escapes the extraction directory on the
/// downloader's machine, even though an Editor controls both the recipe file's
/// RelativePath and FileName. Real folder structure (the intended <c>/</c>
/// separators) still survives. See #481.
/// </summary>
public sealed class CookbookEntryPathTests
{
    [Fact]
    public void Ordinary_path_is_preserved()
    {
        CookbookEndpoints.BuildSafeEntryPath("src/codeunits", "Foo.al")
            .Should().Be("src/codeunits/Foo.al");
    }

    [Fact]
    public void Empty_relative_path_yields_bare_file_name()
    {
        CookbookEndpoints.BuildSafeEntryPath("", "Foo.al").Should().Be("Foo.al");
        CookbookEndpoints.BuildSafeEntryPath(null, "Foo.al").Should().Be("Foo.al");
    }

    [Fact]
    public void Dotdot_segments_are_stripped()
    {
        CookbookEndpoints.BuildSafeEntryPath("../../etc", "passwd")
            .Should().Be("etc/passwd");
    }

    [Fact]
    public void Leading_slash_does_not_produce_an_absolute_entry()
    {
        // A leading separator splits to an empty first segment (dropped), so the
        // entry is relative — an extractor can't write it to the filesystem root.
        var result = CookbookEndpoints.BuildSafeEntryPath("/etc", "passwd");
        result.Should().Be("etc/passwd");
        result.Should().NotStartWith("/");
    }

    [Fact]
    public void Backslash_traversal_is_neutralised()
    {
        CookbookEndpoints.BuildSafeEntryPath(@"..\..\windows", "system.ini")
            .Should().Be("windows/system.ini");
    }

    [Fact]
    public void Path_that_collapses_entirely_falls_back_to_a_neutral_name()
    {
        // Every segment is a traversal token or separator — nothing survives.
        CookbookEndpoints.BuildSafeEntryPath("../..", "..").Should().Be("file");
    }

    [Theory]
    [InlineData("../../etc", "passwd")]
    [InlineData("/absolute", "x")]
    [InlineData(@"..\..\..\", "..")]
    [InlineData("a/../../b", "c")]
    public void Result_never_contains_a_traversal_token_or_leading_slash(string relativePath, string fileName)
    {
        var result = CookbookEndpoints.BuildSafeEntryPath(relativePath, fileName);
        result.Should().NotStartWith("/");
        result.Split('/').Should().NotContain(seg => seg == ".." || seg == ".");
    }
}
