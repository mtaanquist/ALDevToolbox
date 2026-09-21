using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Palette;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// Runs <see cref="PaletteSourceVisibilityTestBase"/> against a source over
/// <c>oe_projects</c> written the way a real one has to be: the organisation
/// query filter plus
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>. It passes, which is what
/// makes the harness worth inheriting.
///
/// <para>The source here is test-only. The real Solutions source is #882; this
/// one exists so the harness has something to prove itself against before any
/// real source is written, and it is the shape #882 should copy.</para>
/// </summary>
public sealed class ProjectPaletteSourceHarnessTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new VisibilityRespectingProjectSource(context.Db, context.Access);

    /// <summary>A source over solutions themselves has nothing extra to seed.</summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed) => Task.CompletedTask;

    [Fact]
    public async Task The_visible_solution_comes_back_with_its_short_name_and_link()
    {
        await SeedWorldAsync();

        var results = await SearchAsync(VisibleName);

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("solution");
        row.Title.Should().Be(VisibleName);
        row.ShortName.Should().Be(VisibleShortName);
        row.Href.Should().Be($"/solutions/{VisibleProjectId}");
    }

    [Fact]
    public async Task An_exact_short_name_finds_the_solution_it_belongs_to()
    {
        await SeedWorldAsync();

        var results = await SearchAsync(VisibleShortName);

        results.Should().ContainSingle().Which.Title.Should().Be(VisibleName);
    }
}

/// <summary>
/// Proof that the harness would catch a source that forgot the fence. Runs the
/// harness's own leak assertion against a source written the naive way - the
/// organisation query filter alone, no visibility predicate - and expects it to
/// fail.
///
/// <para>Without this, "every source passes the harness" would be a statement
/// about the sources and not about the harness. The naive source is a private
/// nested class so xUnit does not collect it as a test class of its own; only
/// the assertion below runs it.</para>
/// </summary>
public sealed class PaletteHarnessCatchesALeakySourceTests
{
    [Fact]
    public async Task A_source_without_the_visibility_predicate_is_caught()
    {
        using var leaky = new LeakyHarness();

        var act = () => leaky.A_private_solution_the_caller_is_not_on_never_leaks();

        (await act.Should().ThrowAsync<Exception>(
            "a source that reads oe_projects through the org filter alone still returns a Private "
            + "solution the caller has no grant on, and the harness exists to notice"))
            .Which.Message.Should().Contain(PrivateNameFragment);
    }

    [Fact]
    public async Task The_same_leaky_source_still_passes_the_cross_organisation_case()
    {
        // The two failures are different failures: the EF query filter already
        // stops the other organisation, which is exactly why a source can look
        // safe while leaking. Pinning this keeps the leak case from passing for
        // the wrong reason.
        using var leaky = new LeakyHarness();

        var act = () => leaky.Another_organisations_rows_never_appear();

        await act.Should().NotThrowAsync();
    }

    private const string PrivateNameFragment = "Contoso Holdings";

    /// <summary>
    /// The harness, wired to a source that filters by organisation and nothing
    /// else - the mistake this whole file exists to make loud.
    /// </summary>
    private sealed class LeakyHarness : PaletteSourceVisibilityTestBase
    {
        protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
            new NaiveProjectSource(context.Db);

        protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed) => Task.CompletedTask;
    }
}

/// <summary>
/// A palette source over solutions, written the way a real one has to be: one
/// <c>AsNoTracking()</c> read, scoped by the organisation query filter and
/// narrowed by <see cref="ProjectAccess.VisibleProjectPredicate"/>, projecting
/// only the fields a row needs. No <c>IgnoreQueryFilters()</c>.
/// </summary>
internal sealed class VisibilityRespectingProjectSource : IPaletteSource
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public VisibilityRespectingProjectSource(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    public string Id => "solutions";
    public string Label => "Solutions";
    public int Order => PaletteGroupOrder.Solutions;

    /// <summary>Any signed-in member of the organisation; the visibility rules do the rest.</summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user.Identity?.IsAuthenticated == true);

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);

        // No ILike pre-filter: a few hundred solutions per organisation is one
        // small indexed read, and filtering in SQL would be accent-sensitive
        // (Postgres has no unaccent here) where the ranking is not. See
        // PaletteQuery.SqlTerms.
        var rows = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .OrderBy(p => p.Name)
            .Take(limit)
            .Select(p => new { p.Id, p.Name, p.ShortName })
            .ToListAsync(ct);

        return rows
            .Select(r => new PaletteCandidate("solution", r.Name, r.ShortName, $"/solutions/{r.Id}", r.ShortName))
            .ToList();
    }
}

/// <summary>
/// The same source with the visibility predicate left out - the plausible
/// mistake, not a strawman: it is <c>AsNoTracking()</c>, it soft-delete filters,
/// and it never calls <c>IgnoreQueryFilters()</c>, so it passes every review
/// rule the codebase has and still hands a Private customer's name to someone
/// with no grant on it. Test-only; never registered.
/// </summary>
internal sealed class NaiveProjectSource : IPaletteSource
{
    private readonly AppDbContext _db;

    public NaiveProjectSource(AppDbContext db) => _db = db;

    public string Id => "solutions";
    public string Label => "Solutions";
    public int Order => PaletteGroupOrder.Solutions;

    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user.Identity?.IsAuthenticated == true);

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        var rows = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .OrderBy(p => p.Name)
            .Take(limit)
            .Select(p => new { p.Id, p.Name, p.ShortName })
            .ToListAsync(ct);

        return rows
            .Select(r => new PaletteCandidate("solution", r.Name, r.ShortName, $"/solutions/{r.Id}", r.ShortName))
            .ToList();
    }
}
