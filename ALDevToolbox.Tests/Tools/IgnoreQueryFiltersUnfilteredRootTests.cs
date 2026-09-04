using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// The mirror of <see cref="IgnoreQueryFiltersBaselineTests"/> (#701).
///
/// <para>That test makes every bypass of the tenant query filter visible. This
/// one makes sure the visible ones are all real: <c>IgnoreQueryFilters()</c>
/// suppresses filters, so a call on a query rooted at an entity that has no
/// filter suppresses nothing. It costs the review signal anyway — the next
/// reader sees a deliberate tenant-fence crossing and has to work out that it
/// does nothing, and the one after that copies the pattern into a query where
/// it *would* matter.</para>
///
/// <para>The unfiltered entity set is read from the built EF model rather than
/// hard-coded, so scoping a table later (a <c>ScopeToOrganization&lt;T&gt;</c>
/// call in <c>AppDbContext</c>) automatically stops this test complaining about
/// bypasses on it.</para>
///
/// <para><b>Deliberately conservative.</b> <c>IgnoreQueryFilters()</c> applies
/// to the whole query tree, so a query rooted at an unfiltered entity still
/// needs the bypass when it reaches a filtered one — the live example is
/// <c>PerTenantBackupService.ListAsync</c>, whose root carries no filter but
/// whose <c>Include(b =&gt; b.CreatedByUser)</c> pulls in <c>User</c>, which is
/// filtered. A statement carrying any <c>Include</c> is therefore left alone:
/// this test only flags the shape it can settle from the statement itself. A
/// query that reaches a filtered entity some other way (a navigation inside a
/// <c>Where</c> or <c>Select</c>) is a false positive; add its file to
/// <see cref="FilesWithBypassesThatDoRealWork"/> with the reason, the same
/// deliberate-diff posture as the baseline count.</para>
/// </summary>
public sealed class IgnoreQueryFiltersUnfilteredRootTests
{
    private const string Marker = "IgnoreQueryFilters(";

    /// <summary>
    /// Files whose bypass on an unfiltered root is genuinely doing work,
    /// because the query reaches a filtered entity in a way this test cannot
    /// see. Add an entry only with the reason, and say the same thing in a
    /// comment at the call site so the next reader does not delete it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FilesWithBypassesThatDoRealWork =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void No_bypass_sits_on_a_query_rooted_at_an_entity_with_no_filter()
    {
        var unfilteredSets = UnfilteredDbSetNames();
        unfilteredSets.Should().NotBeEmpty("the model must build for this test to mean anything");

        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (FilesWithBypassesThatDoRealWork.ContainsKey(relative)) continue;

            var code = StripComments(File.ReadAllLines(file));
            for (var i = 0; i < code.Length; i++)
            {
                var setName = unfilteredSets.FirstOrDefault(s => MentionsDbSet(code[i], s));
                if (setName is null) continue;

                var statement = ReadStatement(code, i);
                if (!statement.Contains(Marker, StringComparison.Ordinal)) continue;
                // An Include may reach a filtered entity from this unfiltered
                // root, which is the one shape where the bypass is real.
                if (statement.Contains("Include(", StringComparison.Ordinal)) continue;

                offenders.Add($"{relative}:{i + 1} (rooted at {setName})");
            }
        }

        offenders.Should().BeEmpty(
            "IgnoreQueryFilters() on a query rooted at an entity with no query filter suppresses "
            + "nothing — drop the call and keep whatever the comment said about why the read spans "
            + "organisations. If the query does reach a filtered entity, say so at the call site and "
            + "list the file in FilesWithBypassesThatDoRealWork in this test");
    }

    /// <summary>
    /// The <c>DbSet</c> property names on <see cref="AppDbContext"/> whose entity
    /// type carries no query filter. Model-only: building the model needs no
    /// database, so this runs without Docker.
    /// </summary>
    private static HashSet<string> UnfilteredDbSetNames()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        return typeof(AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => (p.Name, Entity: p.PropertyType.GetGenericArguments()[0]))
            .Where(x => ctx.Model.FindEntityType(x.Entity) is { } entity
                        && (entity.GetDeclaredQueryFilters()?.Count ?? 0) == 0)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Matches a member access onto the named DbSet (<c>db.Organizations</c>).</summary>
    private static bool MentionsDbSet(string line, string setName)
    {
        var index = line.IndexOf('.');
        while (index >= 0)
        {
            var start = index + 1;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            if (start + setName.Length <= line.Length
                && string.CompareOrdinal(line, start, setName, 0, setName.Length) == 0)
            {
                var after = start + setName.Length;
                if (after >= line.Length || (!char.IsLetterOrDigit(line[after]) && line[after] != '_'))
                {
                    return true;
                }
            }
            index = line.IndexOf('.', index + 1);
        }
        return false;
    }

    /// <summary>
    /// The statement starting on <paramref name="start"/>: lines up to and
    /// including the first one ending in a semicolon. Bounded so a malformed
    /// read can't swallow the rest of the file.
    /// </summary>
    private static string ReadStatement(IReadOnlyList<string> code, int start)
    {
        var lines = new List<string>();
        for (var i = start; i < code.Count && i < start + 30; i++)
        {
            lines.Add(code[i]);
            if (code[i].TrimEnd().EndsWith(';')) break;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Blanks out <c>//</c>, <c>/* */</c> and Razor <c>@* *@</c> comments so
    /// prose about the fence is never mistaken for a call site.
    /// </summary>
    private static string[] StripComments(IReadOnlyList<string> raw)
    {
        var code = new string[raw.Count];
        var inBlock = false;
        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            var buffer = new System.Text.StringBuilder(line.Length);
            var j = 0;
            while (j < line.Length)
            {
                if (inBlock)
                {
                    if (Starts(line, j, "*/") || Starts(line, j, "*@")) { inBlock = false; j += 2; }
                    else j++;
                }
                else if (Starts(line, j, "//"))
                {
                    break;
                }
                else if (Starts(line, j, "/*") || Starts(line, j, "@*"))
                {
                    inBlock = true;
                    j += 2;
                }
                else
                {
                    buffer.Append(line[j]);
                    j++;
                }
            }
            code[i] = buffer.ToString();
        }
        return code;
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;

    /// <summary>Application sources only: generated migrations and build output are out of scope.</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        var app = Path.Combine(root, "ALDevToolbox");
        return Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                var segments = Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/').Split('/');
                return !segments.Contains("obj") && !segments.Contains("bin") && !segments.Contains("Migrations");
            })
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
