using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Extensions;

/// <summary>
/// Pins <see cref="ExtensionPreviewBuilder"/> so the live preview on
/// New Workspace / New Extension / Template Detail keeps reflecting what
/// <c>GenerationService</c> actually emits: recursive folder walk, example
/// filtering, <c>.gitkeep</c> stand-ins for empty leaves, and any
/// per-extension <see cref="OrganizationFile"/> rows the caller threads
/// in.
/// </summary>
public sealed class ExtensionPreviewBuilderTests
{
    private static readonly IReadOnlyList<string> NoPerExtensionFiles = Array.Empty<string>();

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

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { root }, includeExamples: true, NoPerExtensionFiles);

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

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: false, NoPerExtensionFiles);

        var source = contents.Single(n => n.Name == "Source");
        source.Children.Select(c => c.Name).Should().Contain("Real.al");
        source.Children.Select(c => c.Name).Should().NotContain("Example.al");
    }

    [Fact]
    public void BuildContents_emits_gitkeep_for_empty_leaf_folders()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Empty" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true, NoPerExtensionFiles);

        var emptyFolder = contents.Single(n => n.Name == "Empty");
        emptyFolder.Children.Should().ContainSingle(c => c.Name == ".gitkeep");
    }

    [Fact]
    public void BuildContents_does_not_add_fallback_folders()
    {
        var declared = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { declared }, includeExamples: true, NoPerExtensionFiles);

        // With no per-extension org files threaded in, only app.json and
        // the declared folder land — no AppSourceCop.json phantom.
        contents.Select(c => c.Name).Should()
            .BeEquivalentTo(new[] { "app.json", "Source" });
    }

    [Fact]
    public void BuildContents_includes_per_extension_org_files_threaded_by_caller()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        var perExtension = new[] { "AppSourceCop.json", ".vscode/settings.json" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true, perExtension);

        // Top-level files include AppSourceCop.json and the .vscode parent.
        contents.Select(c => c.Name).Should().Contain(new[] { "app.json", "AppSourceCop.json", ".vscode", "Source" });
        var vscode = contents.Single(c => c.Name == ".vscode");
        vscode.Children.Should().ContainSingle(c => c.Name == "settings.json");
    }

    [Fact]
    public void BuildContents_for_module_folders_uses_same_shape()
    {
        var folder = new ModuleExtensionFolder { OrganizationId = 1, Path = "Source" };
        folder.Files.Add(new ModuleExtensionFile
        {
            OrganizationId = 1, Path = "Helper.al", Content = string.Empty,
        });

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true, NoPerExtensionFiles);

        contents.Select(c => c.Name).Should().Contain(new[] { "app.json", "Source" });
        contents.Single(n => n.Name == "Source")
            .Children.Should().ContainSingle(c => c.Name == "Helper.al");
    }
}
