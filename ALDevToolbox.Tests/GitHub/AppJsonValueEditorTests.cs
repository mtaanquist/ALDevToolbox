using ALDevToolbox.Services.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// Reading and bumping an <c>app.json</c> without disturbing it (issue #630).
///
/// <para>The parser has to keep the dependency versions it used to drop -
/// without them a repository cannot be told it is a version behind. And the
/// edit has to be exactly that: the values that moved, and nothing else. A
/// manifest is a file people maintain, so a pull request that reorders its keys
/// or reflows its indentation is one nobody can review.</para>
///
/// <para><see cref="ALDevToolbox.Services.GitHub.AppJsonValueEditor"/> is
/// internal, so these drive it through the assembly-internals seam the test
/// project already has.</para>
/// </summary>
public sealed class AppJsonValueEditorTests
{
    private const string Manifest = """
        {
          // Kept by a person, comments and all.
          "id": "1c0ffee0-0000-4000-8000-000000000001",
          "name": "Payment Import",
          "version": "1.0.0.0",
          "application": "27.0.0.0",
          "platform": "27.0.0.0",
          "dependencies": [
            { "id": "AAAA1111-0000-4000-8000-000000000001", "name": "Other", "version": "1.0.0.0" },
            { "appId": "{63CA2FA4-4F03-4F2B-A480-172FEF340D3F}", "name": "System Application", "version": "27.0.0.0" }
          ]
        }
        """;

    [Fact]
    public void The_parser_keeps_a_dependencys_version_however_the_manifest_spells_its_id()
    {
        var manifest = AppJsonManifestParser.Parse(Manifest);

        manifest.Should().NotBeNull();
        manifest!.Dependencies.Should().HaveCount(2);
        manifest.Dependencies[0].Version.Should().Be("1.0.0.0");
        manifest.Dependencies[1].Name.Should().Be("System Application");
        manifest.Dependencies[1].Version.Should().Be("27.0.0.0", "the older appId spelling carries a version too");
    }

    [Fact]
    public void A_dependency_that_states_no_version_reads_as_stating_none()
    {
        var manifest = AppJsonManifestParser.Parse(
            """{"id":"x","name":"n","publisher":"p","version":"1.0.0.0","dependencies":[{"id":"a","name":"A"}]}""");

        manifest!.Dependencies.Should().ContainSingle().Which.Version.Should().BeNull();
    }

    [Fact]
    public void Bumping_the_application_changes_that_value_and_nothing_else()
    {
        var edited = ALDevToolbox.Services.GitHub.AppJsonValueEditor
            .ReplaceRootProperty(Manifest, "application", "28.2.0.0");

        edited.Should().Be(Manifest.Replace("\"application\": \"27.0.0.0\"", "\"application\": \"28.2.0.0\""));
    }

    [Fact]
    public void Bumping_one_dependency_leaves_the_other_and_the_top_level_version_alone()
    {
        var edited = ALDevToolbox.Services.GitHub.AppJsonValueEditor.ReplaceDependencyVersion(
            Manifest, "63ca2fa4-4f03-4f2b-a480-172fef340d3f", "28.2.0.0");

        edited.Should().Be(Manifest.Replace(
            "\"name\": \"System Application\", \"version\": \"27.0.0.0\"",
            "\"name\": \"System Application\", \"version\": \"28.2.0.0\""));
        // The extension's own version and the other dependency read as they did.
        edited.Should().Contain("\"version\": \"1.0.0.0\"");
    }

    [Fact]
    public void A_dependency_that_is_not_there_is_not_edited_at_all()
    {
        ALDevToolbox.Services.GitHub.AppJsonValueEditor
            .ReplaceDependencyVersion(Manifest, "99999999-0000-4000-8000-000000000009", "28.2.0.0")
            .Should().BeNull();
    }

    [Fact]
    public void A_manifest_that_is_not_json_is_refused_rather_than_mangled()
    {
        ALDevToolbox.Services.GitHub.AppJsonValueEditor
            .ReplaceRootProperty("not json at all", "application", "28.2.0.0")
            .Should().BeNull();
    }
}
