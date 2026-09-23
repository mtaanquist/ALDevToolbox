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

    /// <summary>
    /// Captured verbatim from alc 17.0.34.45391 compiling an app.json that declares
    /// the Microsoft application and platform plus two third-party dependencies,
    /// against an empty package cache (issue #901). Not written from memory: the
    /// wording is what the parser has to match.
    /// </summary>
    private const string CapturedMissingPackages = """
        Microsoft (R) AL Compiler version 17.0.34.45391
        Copyright (C) Microsoft Corporation. All rights reserved

        Compilation started for project 'CRONUS Continia Extensions' containing '1' files at '10:14:50.006'.

        error AL1022: A package with publisher 'Microsoft', name 'Application', and a version compatible with '26.0.0.0' could not be found in the package cache folders: /tmp/oe-build-x/symbols
        error AL1022: A package with publisher 'Microsoft', name 'System', and a version compatible with '26.0.0.0' could not be found in the package cache folders: /tmp/oe-build-x/symbols
        error AL1022: A package with publisher 'Continia Software', name 'Continia Core', and a version compatible with '12.1.0.0' could not be found in the package cache folders: /tmp/oe-build-x/symbols
        error AL1022: A package with publisher 'ForNAV', name 'ForNAV Core', and a version compatible with '7.0.0.0' could not be found in the package cache folders: /tmp/oe-build-x/symbols

        Compilation ended at '10:14:50.227'.
        """;

    [Fact]
    public void Reads_each_package_the_compiler_could_not_find()
    {
        var missing = AlcOutputParser.ParseMissingPackages(CapturedMissingPackages);

        missing.Should().Equal(
            new AlcMissingPackage("Microsoft", "Application", "26.0.0.0"),
            new AlcMissingPackage("Microsoft", "System", "26.0.0.0"),
            new AlcMissingPackage("Continia Software", "Continia Core", "12.1.0.0"),
            new AlcMissingPackage("ForNAV", "ForNAV Core", "7.0.0.0"));
    }

    [Fact]
    public void A_missing_package_is_not_a_file_diagnostic()
    {
        // AL1022 names no file, so it cannot become a check-run annotation; the
        // missing-package reader is the only thing that sees it.
        AlcOutputParser.Parse(CapturedMissingPackages).Should().BeEmpty();
    }

    [Fact]
    public void Reads_a_missing_package_line_with_windows_line_endings_once()
    {
        var line = "error AL1022: A package with publisher 'Continia Software', name 'Continia Core', and a version compatible with '12.1.0.0' could not be found in the package cache folders: C:\\build\\symbols\r\n";

        AlcOutputParser.ParseMissingPackages(line + line)
            .Should().ContainSingle("the compiler repeats itself when a folder is compiled twice")
            .Which.Should().Be(new AlcMissingPackage("Continia Software", "Continia Core", "12.1.0.0"));
    }

    [Fact]
    public void Other_errors_are_not_missing_packages()
    {
        var output = """
            /src/App/My.al(12,5): error AL0118: The name 'CC Helper' does not exist in the current context
            /src/App/My.al(4,1): warning AA0005: Braces are redundant
            error AL1021: Some other package problem that is not a missing package
            """;

        AlcOutputParser.ParseMissingPackages(output).Should().BeEmpty();
        AlcOutputParser.ParseMissingPackages(null).Should().BeEmpty();
    }
}
