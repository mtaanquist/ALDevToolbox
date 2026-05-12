using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Extensions;

/// <summary>
/// Pins <see cref="ExtensionPreviewBuilder"/> so the live preview on
/// New Workspace / New Extension / Template Detail keeps reflecting what
/// <c>GenerationService</c> actually emits: recursive folder walk, example
/// filtering, <c>.gitkeep</c> stand-ins for empty leaves, and the
/// libs/permissionsets/Translations fallback for any names the template
/// didn't already declare.
/// </summary>
public sealed class ExtensionPreviewBuilderTests
{
    [Fact]
    public void BuildContents_walks_nested_folders_and_files()
    {
        var root = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        var nested = new WorkspaceExtensionFolder
        {
            OrganizationId = 1, Path = "Codeunits", ParentFolder = root,
        };
        nested.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1, Path = "Helper.al", Content = "// stub",
        });
        root.Folders.Add(nested);

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { root }, includeExamples: true);

        var source = contents.Single(n => n.Name == "Source");
        var codeunits = source.Children.Single(n => n.Name == "Codeunits");
        codeunits.Children.Should().ContainSingle(n => n.Name == "Helper.al" && n.Kind == PreviewNodeKind.File);
    }

    [Fact]
    public void BuildContents_drops_example_files_when_includeExamples_is_false()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        folder.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1, Path = "Real.al", Content = string.Empty, IsExample = false,
        });
        folder.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1, Path = "Example.al", Content = string.Empty, IsExample = true,
        });

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: false);

        var source = contents.Single(n => n.Name == "Source");
        source.Children.Select(c => c.Name).Should().Contain("Real.al");
        source.Children.Select(c => c.Name).Should().NotContain("Example.al");
    }

    [Fact]
    public void BuildContents_emits_gitkeep_for_empty_leaf_folders()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Empty" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true);

        var emptyFolder = contents.Single(n => n.Name == "Empty");
        emptyFolder.Children.Should().ContainSingle(c => c.Name == ".gitkeep");
    }

    [Fact]
    public void BuildContents_skips_fallback_folders_that_collide_case_insensitively()
    {
        var declared = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "translations" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { declared }, includeExamples: true);

        // The fallback "Translations" must not appear alongside the existing
        // case-insensitive match — Windows would treat both as the same folder.
        contents.Count(c => string.Equals(c.Name, "translations", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
        contents.Should().Contain(c => c.Name == "libs");
        contents.Should().Contain(c => c.Name == "permissionsets");
    }

    [Fact]
    public void BuildContents_for_module_folders_uses_same_shape()
    {
        var folder = new ModuleExtensionFolder { OrganizationId = 1, Path = "Source" };
        folder.Files.Add(new ModuleExtensionFile
        {
            OrganizationId = 1, Path = "Helper.al", Content = string.Empty,
        });

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true);

        contents.Select(c => c.Name).Should().Contain(new[] { "app.json", "AppSourceCop.json", "Source", "libs", "permissionsets", "Translations" });
        contents.Single(n => n.Name == "Source")
            .Children.Should().ContainSingle(c => c.Name == "Helper.al");
    }
}
