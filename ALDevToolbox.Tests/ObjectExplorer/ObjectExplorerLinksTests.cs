using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pure URL-shape tests for <see cref="ObjectExplorerLinks"/>. No database —
/// the links service only reads the <c>OBJECT_EXPLORER_LEGACY_VIEWER</c> env
/// var (unset here, so the default SSR viewer route applies).
/// </summary>
public sealed class ObjectExplorerLinksTests
{
    [Fact]
    public void SourceFile_appends_from_when_a_view_release_is_supplied()
    {
        var links = new ObjectExplorerLinks();

        links.SourceFile(42, 10, fromReleaseId: 7)
            .Should().Be("/object-explorer/file/42?line=10&from=7");
    }

    [Fact]
    public void SourceFile_omits_from_when_view_release_is_null()
    {
        var links = new ObjectExplorerLinks();

        links.SourceFile(42, 10, fromReleaseId: null)
            .Should().Be(links.SourceFile(42, 10));
    }
}
