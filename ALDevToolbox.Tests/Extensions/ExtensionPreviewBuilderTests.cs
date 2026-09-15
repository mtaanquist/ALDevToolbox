using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using AwesomeAssertions;

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
    public void BuildContents_flags_example_files_as_excluded_when_includeExamples_is_false()
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

        // The example row stays in the tree so the toggle in the preview card
        // head visibly costs something; the flag is what greys and strikes it.
        var source = contents.Single(n => n.Name == "Source");
        source.Children.Single(c => c.Name == "Real.al").IsExcluded.Should().BeFalse();
        source.Children.Single(c => c.Name == "Example.al").IsExcluded.Should().BeTrue();
    }

    [Fact]
    public void BuildContents_keeps_example_files_unflagged_when_includeExamples_is_true()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        folder.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1, Path = "Example.al", Content = string.Empty, IsExample = true,
        });

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true, NoPerExtensionFiles);

        contents.Single(n => n.Name == "Source")
            .Children.Single(c => c.Name == "Example.al").IsExcluded.Should().BeFalse();
    }

    [Fact]
    public void BuildContents_adds_gitkeep_when_only_excluded_examples_remain()
    {
        // Mirrors the generator: with examples off the folder ships empty, so
        // it gets the .gitkeep — beside the struck-through example row.
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        folder.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1, Path = "Example.al", Content = string.Empty, IsExample = true,
        });

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: false, NoPerExtensionFiles);

        var source = contents.Single(n => n.Name == "Source");
        source.Children.Select(c => c.Name).Should().BeEquivalentTo(new[] { "Example.al", ".gitkeep" });
        source.Children.Single(c => c.Name == ".gitkeep").IsExcluded.Should().BeFalse();
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

        // With no per-extension org files threaded in, only the declared
        // folder lands. app.json is no longer hardcoded — it arrives via
        // perExtensionFilePaths now that it's a seeded EveryExtension org
        // file. No AppSourceCop.json phantom either.
        contents.Select(c => c.Name).Should()
            .BeEquivalentTo(new[] { "Source" });
    }

    [Fact]
    public void BuildContents_includes_per_extension_org_files_threaded_by_caller()
    {
        var folder = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "Source" };
        // app.json sits alongside other admin-opted-in per-extension files
        // now — the seeded canonical app.json ships through the same join
        // as anything else the template opts into.
        var perExtension = new[] { "app.json", "AppSourceCop.json", ".vscode/settings.json" };

        var contents = ExtensionPreviewBuilder.BuildContents(new[] { folder }, includeExamples: true, perExtension);

        // Top-level files include app.json, AppSourceCop.json and the .vscode parent.
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

        // app.json no longer hardcoded — only the declared folder lands when
        // no per-extension org files are threaded in.
        contents.Select(c => c.Name).Should().Contain(new[] { "Source" });
        contents.Single(n => n.Name == "Source")
            .Children.Should().ContainSingle(c => c.Name == "Helper.al");
    }

    // ===== Empty root folders in the workspace-root preview =====

    [Fact]
    public void BuildWorkspacePreview_shows_declared_root_folders_with_their_placeholder()
    {
        var children = new List<PreviewNode>();

        var root = PreviewTreeBuilder.BuildWorkspacePreview(
            "CRONUSCustomer", children, "workspace.aldt.toml",
            workspaceRootPaths: Array.Empty<string>(),
            emptyRootFolders: new[] { ".alpackages", "docs/decisions" });

        var alpackages = root.Children.Single(n => n.Name == ".alpackages");
        alpackages.Children.Should().ContainSingle(c => c.Name == ".gitkeep");
        var docs = root.Children.Single(n => n.Name == "docs");
        docs.Children.Single(n => n.Name == "decisions").Children
            .Should().ContainSingle(c => c.Name == ".gitkeep");
    }

    [Fact]
    public void BuildWorkspacePreview_skips_the_placeholder_when_a_root_file_fills_the_folder()
    {
        // Mirrors WorkspaceZipBuilder.WriteRootFolders: the folder is in the
        // tree because of the file, so a placeholder beside it would be a lie
        // about what the ZIP holds.
        var children = new List<PreviewNode>();

        var root = PreviewTreeBuilder.BuildWorkspacePreview(
            "CRONUSCustomer", children, "workspace.aldt.toml",
            workspaceRootPaths: new[] { ".assets/rulesets/Company.ruleset.json" },
            emptyRootFolders: new[] { ".assets" });

        var assets = root.Children.Single(n => n.Name == ".assets");
        assets.Children.Should().NotContain(c => c.Name == ".gitkeep");
        assets.Children.Single(n => n.Name == "rulesets").Children
            .Should().ContainSingle(c => c.Name == "Company.ruleset.json");
    }

    [Fact]
    public void BuildWorkspacePreview_without_root_folders_is_unchanged()
    {
        var children = new List<PreviewNode>();

        var root = PreviewTreeBuilder.BuildWorkspacePreview(
            "CRONUSCustomer", children, "workspace.aldt.toml",
            workspaceRootPaths: Array.Empty<string>());

        root.Children.Select(c => c.Name).Should()
            .BeEquivalentTo(new[] { "CRONUSCustomer.code-workspace", "workspace.aldt.toml" });
    }
}
