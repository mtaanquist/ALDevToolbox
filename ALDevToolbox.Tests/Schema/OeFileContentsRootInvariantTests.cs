using FluentAssertions;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// Guards the <c>oe_file_contents</c> tenant-isolation invariant (#426).
///
/// <para>
/// <c>oe_modules.SymbolReferenceContent</c> and <c>oe_module_files.FileContent</c>
/// point at the unscoped <c>oe_file_contents</c> table via
/// <c>HasPrincipalKey(ContentHash)</c>. That dedup table carries no
/// <c>organization_id</c> and no EF query filter, so it is safe <em>only</em>
/// while it is never queried as a root — content is reached exclusively by
/// navigating from a tenant-scoped principal (Module / ModuleFile). Querying
/// the <c>OeFileContents</c> <see cref="Microsoft.EntityFrameworkCore.DbSet{T}"/>
/// directly would bypass the tenant fence.
/// </para>
///
/// <para>
/// Today the DbSet is declared on <c>AppDbContext</c> (EF needs the entity
/// registered) but referenced nowhere else. This test asserts that stays true,
/// so the safety property can't silently regress into a service / page / MCP
/// query. It scans source, so it self-skips if the repo tree isn't present
/// (e.g. a published-only test run); in CI and the dev container the source is
/// always at hand and the invariant is enforced.
/// </para>
/// </summary>
public sealed class OeFileContentsRootInvariantTests
{
    [Fact]
    public void OeFileContents_dbset_is_never_referenced_outside_its_declaration()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            // No source tree alongside the test binaries — nothing to scan.
            return;
        }

        var appDir = Path.Combine(root, "ALDevToolbox");
        var declaringFile = Path.Combine(appDir, "Data", "AppDbContext.cs");
        var migrationsDir = Path.Combine(appDir, "Data", "Migrations");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            // The DbSet declaration itself is the one allowed mention; migrations
            // are generated EF scaffolding and don't issue tenant-scoped reads.
            if (PathsEqual(file, declaringFile)) continue;
            if (file.StartsWith(migrationsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (IsUnderObjOrBin(file, appDir)) continue;

            if (File.ReadAllText(file).Contains("OeFileContents", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        offenders.Should().BeEmpty(
            "oe_file_contents is unscoped (no tenant filter); content must be reached only by "
            + "navigating from a tenant-scoped Module / ModuleFile, never by querying the DbSet directly. "
            + "If one of these genuinely needs the DbSet, scope it and update this guard.");
    }

    private static bool IsUnderObjOrBin(string file, string appDir)
    {
        var rel = Path.GetRelativePath(appDir, file);
        return rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    /// <summary>Walks up from the test binaries to the repo root (the folder holding the .slnx).</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
