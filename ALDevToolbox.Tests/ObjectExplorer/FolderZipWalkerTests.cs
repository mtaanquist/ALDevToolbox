using System.IO.Compression;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="FolderZipWalker"/>. Builds tiny synthetic ZIPs
/// in memory so the assertions stay focused on the folder/filename
/// conventions the DVD uses — no need for the real Microsoft fixtures here.
/// </summary>
public sealed class FolderZipWalkerTests
{
    [Fact]
    public void Pairs_app_with_sibling_source_zip_in_same_folder()
    {
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/DKCore/Source/DK Core.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().NotBeNull();
    }

    [Fact]
    public void Strips_publisher_prefix_when_pairing()
    {
        using var archive = BuildArchive(
            "applications/BaseApp/Source/Microsoft_Base Application.app",
            "applications/BaseApp/Source/Base Application.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry!.Name.Should().Be("Base Application.Source.zip");
    }

    [Fact]
    public void Pairs_by_bare_stem_when_publisher_prefix_present_on_both_sides()
    {
        // _Exclude_ apps keep their prefix on both .app and .Source.zip; the
        // bare-stem candidate (1st try) should match without falling back to
        // the prefix-stripped form.
        using var archive = BuildArchive(
            "applications/APIV2/Source/Microsoft__Exclude_APIV2_.app",
            "applications/APIV2/Source/_Exclude_APIV2_.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().NotBeNull();
    }

    [Fact]
    public void Leaves_source_zip_null_when_no_sibling_present()
    {
        using var archive = BuildArchive(
            "applications/MyApp/Source/Partner_MyApp.app");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().ContainSingle()
            .Which.SourceZipEntry.Should().BeNull();
    }

    [Theory]
    [InlineData("applications/BaseApp/Test/Microsoft_Tests-Bank.app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    [InlineData("applications/TestFramework/AITestToolkit/Microsoft_AI Test Toolkit.app", true)]
    [InlineData("applications/TestFramework/TestLibraries/Any/Microsoft_Any.app", true)]
    [InlineData("applications/Quality Management/Test Library/Microsoft_Quality Management Test Library.app", true)]
    [InlineData("applications/Quality Management/Source/Microsoft_Quality Management.app", false)]
    [InlineData("applications/Shopify/test/Microsoft_Shopify Connector Test.app", true)]
    public void Sets_is_test_when_ancestor_folder_matches_test_convention(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsTest.Should().Be(expected);
    }

    [Theory]
    [InlineData("applications/APIV2/Source/Microsoft__Exclude_APIV2_.app", true)]
    [InlineData("applications/APIV2/Source/_Exclude_APIV2_.app", true)]
    [InlineData("applications/BankDeposits/Source/Microsoft__Exclude_Bank Deposits.app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    public void Sets_is_internal_when_filename_contains_exclude_marker(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsInternal.Should().Be(expected);
    }

    [Theory]
    [InlineData("applications/BaseApp/Source/Microsoft_Danish language (Denmark).app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_French language (Switzerland).app", true)]
    [InlineData("applications/BaseApp/Source/Microsoft_Base Application.app", false)]
    [InlineData("applications/BaseApp/Source/Microsoft_DK Core.app", false)]
    public void Sets_is_language_pack_when_name_matches_language_pattern(string path, bool expected)
    {
        using var archive = BuildArchive(path);
        var entry = FolderZipWalker.Walk(archive).Single();
        entry.IsLanguagePack.Should().Be(expected);
    }

    [Fact]
    public void Does_not_pair_a_source_zip_from_a_different_folder()
    {
        // Two different apps with similarly-named source zips that live in
        // different subfolders — pairing must respect folder boundaries.
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/Other/Source/DK Core.Source.zip");

        var entry = FolderZipWalker.Walk(archive).Single();
        entry.SourceZipEntry.Should().BeNull(
            because: "the source zip sits under a different folder; pairing is per-directory");
    }

    [Fact]
    public void Returns_one_entry_per_app_in_a_multi_app_archive()
    {
        using var archive = BuildArchive(
            "applications/DKCore/Source/Microsoft_DK Core.app",
            "applications/DKCore/Source/DK Core.Source.zip",
            "applications/OIOUBL/Source/Microsoft_OIOUBL.app",
            "applications/OIOUBL/Source/OIOUBL.Source.zip");

        var entries = FolderZipWalker.Walk(archive);
        entries.Should().HaveCount(2);
        entries.Select(e => e.FileName).Should().BeEquivalentTo(
            new[] { "Microsoft_DK Core.app", "Microsoft_OIOUBL.app" });
    }

    /// <summary>
    /// Builds a throwaway <see cref="ZipArchive"/> in memory with the supplied
    /// entry paths. File bodies are empty — none of the walker's behaviour
    /// depends on payload content, only on names and folder layout.
    /// </summary>
    private static ZipArchive BuildArchive(params string[] paths)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in paths)
            {
                z.CreateEntry(p);
            }
        }
        ms.Position = 0;
        // Caller is responsible for disposing the returned archive (which
        // also disposes the backing MemoryStream).
        return new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
    }
}
