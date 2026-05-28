using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pinning tests for <see cref="ReleaseImportService.ObjectHeaderRegex"/>.
/// The regex drives object-to-source-file linking, so a regression that
/// either misses a real header or falsely matches a permissionset
/// permission entry corrupts the catalog (the outline panel ends up
/// pointing at the wrong .al file, and chain resolution against the
/// query's columns fails because the indexer pulled sub-symbols from the
/// permissionset body instead).
/// </summary>
public sealed class ObjectHeaderRegexTests
{
    [Theory]
    [InlineData("table 36 \"Sales Header\"", "table", "Sales Header")]
    [InlineData("codeunit 80 \"Sales-Post\"", "codeunit", "Sales-Post")]
    [InlineData("query 8889 \"Sent Emails\"", "query", "Sent Emails")]
    [InlineData("interface \"ICustomer Handler\"", "interface", "ICustomer Handler")]
    [InlineData("page 21 \"Customer Card\"", "page", "Customer Card")]
    [InlineData("tableextension 7300 \"ItemExt\" extends \"Item\"", "tableextension", "ItemExt")]
    [InlineData("    permissionset 9054 \"Email - Read\"", "permissionset", "Email - Read")]
    public void Matches_real_object_headers(string line, string expectedKind, string expectedName)
    {
        var m = ReleaseImportService.ObjectHeaderRegex.Match(line);
        m.Success.Should().BeTrue();
        m.Groups[1].Value.Should().BeEquivalentTo(expectedKind);
        var name = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value;
        name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("                  query \"Sent Emails\" = X,")]
    [InlineData("                  table \"Sales Header\" = RIMD,")]
    [InlineData("                  page 21 \"Customer Card\" = X,")]
    [InlineData("                  codeunit 80 \"Sales-Post\" = X;")]
    public void Rejects_permissionset_permission_entries(string line)
    {
        // Inside a `permissionset` body, `Permissions = ...` lists each
        // grant as `<kind> "Name" = <perms>,`. The trailing `=` is the
        // distinguishing feature from a real object header — real
        // headers continue with `{`, `extends`, `implements`, or nothing.
        // The negative lookahead in ObjectHeaderRegex must reject these
        // lines, otherwise the permissionset's file claims every object
        // it permissions and the real .Query.al / .Codeunit.al / etc.
        // file loses the link.
        var m = ReleaseImportService.ObjectHeaderRegex.Match(line);
        m.Success.Should().BeFalse(
            because: "permission grant entries share the `kind \"Name\"` shape but assign perms with `=`");
    }
}
