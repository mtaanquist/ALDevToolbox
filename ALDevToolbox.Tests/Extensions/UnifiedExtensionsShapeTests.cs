using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Extensions;

/// <summary>
/// Smoke tests for the unified-extensions schema (Issue #54). These tests
/// don't exercise behaviour — that comes back when the service-layer rewrite
/// lands — but they pin the entity shape so a future refactor that, say,
/// flattens the recursive folder tree back into slash-separated paths fails
/// here loudly instead of silently regressing.
/// </summary>
public sealed class UnifiedExtensionsShapeTests
{
    [Fact]
    public void WorkspaceExtension_carries_recursive_folder_tree_with_files_at_any_depth()
    {
        var ext = new WorkspaceExtension
        {
            OrganizationId = 1,
            Path = "Core",
            NameTemplate = "{{extension_prefix}} Core",
            Required = true,
        };

        var src = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "src" };
        var codeunits = new WorkspaceExtensionFolder { OrganizationId = 1, Path = "codeunits", ParentFolder = src };
        src.Folders.Add(codeunits);
        ext.Folders.Add(src);

        codeunits.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1,
            Path = "AppInstall.Codeunit.al",
            Content = "codeunit 90000 \"{{affix}} App Install\" { Subtype = Install; }",
        });
        // A file can attach to an intermediate folder too — not just leaves.
        src.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = 1,
            Path = "Readme.txt",
            Content = "Marker file at the src/ folder root.",
        });

        ext.Folders.Should().ContainSingle().Which.Path.Should().Be("src");
        ext.Folders[0].Folders.Should().ContainSingle().Which.Path.Should().Be("codeunits");
        ext.Folders[0].Folders[0].Files.Should().ContainSingle()
            .Which.Content.Should().Contain("{{affix}}");
        ext.Folders[0].Files.Should().ContainSingle();
    }

    [Fact]
    public void Dependency_reference_shapes_are_independent_per_row()
    {
        var deps = new List<WorkspaceExtensionDependency>
        {
            new() { Ordering = 0, RefExtensionPath = "Core" },
            new() { Ordering = 1, RefModuleKey = "system-application" },
            new() {
                Ordering = 2,
                LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
                LitName = "System Application",
                LitPublisher = "Microsoft",
                LitVersion = "27.0.0.0",
            },
        };

        deps[0].RefExtensionPath.Should().Be("Core");
        deps[0].RefModuleKey.Should().BeNull();
        deps[0].LitId.Should().BeNull();

        deps[1].RefModuleKey.Should().Be("system-application");
        deps[1].RefExtensionPath.Should().BeNull();

        deps[2].LitId.Should().NotBeNull();
        deps[2].RefExtensionPath.Should().BeNull();
        deps[2].RefModuleKey.Should().BeNull();
    }

    [Fact]
    public void AffixType_None_keeps_substitution_path_explicit_for_the_empty_affix()
    {
        var defaults = new TemplateDefaults
        {
            Affix = string.Empty,
            AffixType = AffixType.None,
        };

        defaults.AffixType.Should().Be(AffixType.None);
        defaults.Affix.Should().BeEmpty();
    }

    [Fact]
    public void ModuleExtensionFolder_mirrors_the_workspace_recursive_tree()
    {
        var module = new Module { OrganizationId = 1, Key = "document-capture", Name = "Document Capture" };
        var src = new ModuleExtensionFolder { OrganizationId = 1, Path = "src" };
        var subfolder = new ModuleExtensionFolder { OrganizationId = 1, Path = "interfaces", ParentFolder = src };
        src.Folders.Add(subfolder);
        module.ExtensionFolders.Add(src);

        subfolder.Files.Add(new ModuleExtensionFile
        {
            OrganizationId = 1,
            Path = "IDocumentSink.Interface.al",
            Content = "interface \"IDocumentSink\" { }",
        });

        module.ExtensionFolders.Should().ContainSingle();
        module.ExtensionFolders[0].Folders.Should().ContainSingle();
        module.ExtensionFolders[0].Folders[0].Files.Should().ContainSingle();
    }
}
