using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Reading <c>alc</c>'s console output into diagnostics (issue #627). The rules
/// worth pinning are the ones a check-run annotation depends on: the file, the
/// line, the severity and the code have to survive, a Windows drive letter's
/// colon must not be mistaken for the one that separates the severity, and a
/// line that is not a diagnostic must not become one.
/// </summary>
public sealed class AlcOutputParserTests
{
    [Fact]
    public void Parses_a_posix_error_line()
    {
        var diagnostics = AlcOutputParser.Parse(
            "/tmp/oe-build-x/repo-0/App/Pages/MyPage.al(12,5): error AL0118: The name 'Foo' does not exist");

        diagnostics.Should().ContainSingle();
        var d = diagnostics[0];
        d.Path.Should().Be("/tmp/oe-build-x/repo-0/App/Pages/MyPage.al");
        d.Line.Should().Be(12);
        d.Column.Should().Be(5);
        d.Severity.Should().Be("error");
        d.Code.Should().Be("AL0118");
        d.Message.Should().Be("The name 'Foo' does not exist");
    }

    [Fact]
    public void Keeps_a_windows_drive_letter_in_the_path()
    {
        // The colon after C is the trap: a greedy split on ':' would leave the
        // path as "C" and the annotation would name no file GitHub knows.
        var diagnostics = AlcOutputParser.Parse(
            @"C:\src\App\Codeunits\My.al(4,1): warning AA0005: Braces are redundant");

        diagnostics.Should().ContainSingle();
        diagnostics[0].Path.Should().Be(@"C:\src\App\Codeunits\My.al");
        diagnostics[0].Severity.Should().Be("warning");
        diagnostics[0].Code.Should().Be("AA0005");
    }

    [Fact]
    public void Drops_the_trailing_project_in_brackets()
    {
        var diagnostics = AlcOutputParser.Parse(
            "/src/App/My.al(4,1): warning AA0005: Braces are redundant [/src/App/app.json]");

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().Be("Braces are redundant",
            "the project path repeats what the file path already says");
    }

    [Fact]
    public void Reads_a_diagnostic_with_no_code()
    {
        var diagnostics = AlcOutputParser.Parse("/src/App/My.al(9,2): error: Something went wrong");

        diagnostics.Should().ContainSingle();
        diagnostics[0].Code.Should().BeEmpty();
        diagnostics[0].Message.Should().Be("Something went wrong");
    }

    [Fact]
    public void Skips_lines_that_are_not_diagnostics()
    {
        var diagnostics = AlcOutputParser.Parse("""
            Microsoft (R) AL Compiler version 15.0
            Compilation started for project 'App'
            Compilation ended: 0 errors, 0 warnings
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Keeps_the_compilers_order_and_every_severity()
    {
        var diagnostics = AlcOutputParser.Parse("""
            /src/A.al(1,1): error AL0118: first
            /src/B.al(2,2): warning AA0005: second
            /src/C.al(3,3): info AS0011: third
            """);

        diagnostics.Select(d => d.Severity).Should().Equal("error", "warning", "info");
        diagnostics.Select(d => d.Message).Should().Equal("first", "second", "third");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_output_yields_nothing(string? output) =>
        AlcOutputParser.Parse(output).Should().BeEmpty();

    [Fact]
    public void MakeRelative_strips_the_clone_root_and_normalises_slashes() =>
        AlcOutputParser.MakeRelative(@"C:\build\repo-0\App\My.al", @"C:\build\repo-0")
            .Should().Be("App/My.al");

    [Fact]
    public void MakeRelative_leaves_a_path_outside_the_clone_alone()
    {
        // A diagnostic about a symbol package is not a file in the pull request;
        // inventing a repository-relative name for it would be worse than saying
        // where it really is.
        AlcOutputParser.MakeRelative("/tmp/oe-build-x/symbols/Base.app", "/tmp/oe-build-x/repo-0")
            .Should().Be("/tmp/oe-build-x/symbols/Base.app");
    }

    [Fact]
    public void MakeRelative_without_a_clone_root_only_normalises() =>
        AlcOutputParser.MakeRelative(@"App\My.al", null).Should().Be("App/My.al");
}
