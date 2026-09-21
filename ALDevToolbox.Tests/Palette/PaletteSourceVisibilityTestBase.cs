using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The fence, as a test every <see cref="IPaletteSource"/> has to pass. See
/// <c>.design/command-palette.md</c>, "The fence": the palette is a new read
/// path across most of the app, so it is a new way to leak, and the leak would
/// be silent - a row in a dropdown nobody reviews twice.
///
/// <para>Derive from this, say how to build the source and how to seed one row
/// of its kind against a solution, and inherit three assertions:</para>
/// <list type="number">
///   <item>another organisation's rows never appear;</item>
///   <item>a Private solution the caller is not on does not leak through
///   <em>any</em> field of <em>any</em> result - title, subtitle or href -
///   when its exact name and its short name are searched;</item>
///   <item>a caller who fails the source's gate gets nothing.</item>
/// </list>
///
/// <para>The harness is proved against two sources of its own in
/// <c>ProjectPaletteSourceHarnessTests</c>: one that filters through
/// <c>ProjectAccess.VisibleProjectPredicate</c> and passes, and a naive one that
/// does not and is caught.</para>
/// </summary>
public abstract class PaletteSourceVisibilityTestBase : IDisposable
{
    /// <summary>The signed-in caller: an ordinary org user, on no team, owning nothing.</summary>
    protected const int CallerUserId = 9400;

    /// <summary>Owns the Private solution. The caller is not this person.</summary>
    protected const int StrangerUserId = 9401;

    /// <summary>
    /// The visible solution: Public, in the caller's organisation. Every source
    /// is expected to be able to return its row - an assertion that only ever
    /// says "nothing came back" would pass for a source that returns nothing at
    /// all.
    /// </summary>
    protected const string VisibleName = "CRONUS Coffee";

    protected const string VisibleShortName = "CROCOF";

    /// <summary>
    /// The Private solution the caller has no grant on. Its name and short name
    /// are what the leak test searches for, and neither may come back in any
    /// field of any row.
    /// </summary>
    protected const string PrivateName = "Contoso Holdings";

    protected const string PrivateShortName = "CONHLD";

    /// <summary>The other organisation's solution. Nothing about it may ever appear.</summary>
    protected const string OtherOrgName = "Fabrikam Overseas";

    protected const string OtherOrgShortName = "FABOVS";

    protected TestDb Db { get; } = new();

    private bool _seeded;
    private int _visibleProjectId;
    private int _privateProjectId;
    private int _otherOrgProjectId;

    /// <summary>The Public solution in the caller's org, once <see cref="SeedWorldAsync"/> has run.</summary>
    protected int VisibleProjectId => _visibleProjectId;

    /// <summary>The Private solution the caller is not on.</summary>
    protected int PrivateProjectId => _privateProjectId;

    /// <summary>The other organisation's solution.</summary>
    protected int OtherOrgProjectId => _otherOrgProjectId;

    public void Dispose()
    {
        Db.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── What a deriving test has to supply ──────────────────────────────

    /// <summary>
    /// Builds the source under test against <paramref name="ctx"/>. Everything
    /// it needs is in <see cref="PaletteSourceUnderTest"/>: the context, the
    /// organisation context the harness acts through, and a
    /// <see cref="ProjectAccess"/> on the same context.
    /// </summary>
    protected abstract IPaletteSource CreateSource(PaletteSourceUnderTest context);

    /// <summary>
    /// Seeds one row of this source's kind, belonging to
    /// <see cref="PaletteSourceSeed.ProjectId"/>, named
    /// <see cref="PaletteSourceSeed.Title"/>.
    ///
    /// <para>Called three times - once for the visible solution, once for the
    /// Private one, once for the other organisation's - so a source over
    /// solutions themselves has nothing to do here, while a source over
    /// releases or environments seeds one row hanging off each.</para>
    /// </summary>
    protected abstract Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed);

    /// <summary>
    /// What the caller types to find the visible row. Defaults to the solution's
    /// name; override when the source's rows are named something else.
    /// </summary>
    protected virtual string VisibleQuery => VisibleName;

    /// <summary>
    /// Whether this source's rows belong to a solution. Almost all of them do,
    /// and for those the Private-solution case below is the whole point of the
    /// harness.
    ///
    /// <para>A source whose rows hang off no solution at all - recipes are
    /// org-scoped and belong to the organisation, not to a customer - has
    /// nothing for that case to assert: the row the harness seeds under the
    /// Private solution's name is one every member of the organisation may open,
    /// so "it did not come back" would be a false claim about the fence. Such a
    /// source says so here and the case reports as skipped, which is visible in
    /// the run; the cross-organisation case and the gate case still apply and
    /// still have to pass.</para>
    /// </summary>
    protected virtual bool RowsBelongToSolutions => true;

    /// <summary>
    /// Callers this source must answer nothing for. An anonymous principal is
    /// always in the list - a request with no cookie has no organisation, so a
    /// source that answers one is reading across every tenant at once. A source
    /// with a role gate adds a principal holding the wrong role.
    /// </summary>
    protected virtual IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        yield return ("an anonymous caller", new ClaimsPrincipal(new ClaimsIdentity()));
    }

    // ── The assertions every source inherits ────────────────────────────

    [Fact]
    public async Task The_source_finds_the_row_the_caller_may_see()
    {
        // Not part of the fence, but the fence's assertions are all "nothing
        // came back", and a source that returns nothing at all would sail
        // through every one of them.
        await SeedWorldAsync();

        var results = await SearchAsync(VisibleQuery);

        results.Should().NotBeEmpty(
            "the harness's other cases assert absence; this one proves the source works at all");
    }

    [Fact]
    public async Task Another_organisations_rows_never_appear()
    {
        await SeedWorldAsync();

        foreach (var term in new[] { OtherOrgName, OtherOrgShortName })
        {
            var results = await SearchAsync(term);
            AssertMentionsNothingOf(results, OtherOrgName, OtherOrgShortName, OtherOrgProjectId,
                $"searching '{term}' as a user of another organisation");
        }
    }

    [Fact]
    public async Task A_private_solution_the_caller_is_not_on_never_leaks()
    {
        Assert.SkipUnless(RowsBelongToSolutions,
            "this source's rows hang off no solution, so no solution's visibility can leak through them");

        await SeedWorldAsync();

        // Searched by its exact name and by its short name, because those are
        // what someone who has heard of the customer would type - and the whole
        // point of Private is that they learn nothing here. See
        // .design/command-palette.md, "The fence".
        foreach (var term in new[] { PrivateName, PrivateShortName })
        {
            var results = await SearchAsync(term);
            AssertMentionsNothingOf(results, PrivateName, PrivateShortName, PrivateProjectId,
                $"searching '{term}' with no grant on the Private solution");
        }
    }

    [Fact]
    public async Task A_caller_who_fails_the_gate_gets_nothing()
    {
        await SeedWorldAsync();

        foreach (var (description, principal) in PrincipalsThatGetNothing())
        {
            await using var ctx = Db.NewContext();
            var source = CreateSource(new PaletteSourceUnderTest(ctx, Db.OrgContext, new ProjectAccess(ctx, Db.OrgContext)));

            var available = await source.IsAvailableAsync(principal, CancellationToken.None);
            if (available)
            {
                // A source may choose to answer the gate "yes" and return
                // nothing instead; what it may not do is return rows.
                var results = await source.SearchAsync(
                    PaletteQuery.Parse(VisibleQuery), 25, CancellationToken.None);
                results.Should().BeEmpty(
                    "{0} must get nothing from {1}, through the gate or through an empty search",
                    description, source.Id);
            }
        }
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    /// <summary>Runs the source as the seeded caller, the way the service does.</summary>
    protected async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(string rawQuery)
    {
        await using var ctx = Db.NewContext();
        var source = CreateSource(new PaletteSourceUnderTest(ctx, Db.OrgContext, new ProjectAccess(ctx, Db.OrgContext)));

        var query = PaletteQuery.Parse(rawQuery);
        query.IsUsable.Should().BeTrue("the harness only searches for things a user could type");

        var principal = CallerPrincipal();
        (await source.IsAvailableAsync(principal, CancellationToken.None)).Should().BeTrue(
            "the seeded caller is an ordinary member of the organisation and must pass the gate");

        var candidates = await source.SearchAsync(query, 25, CancellationToken.None);
        // Rank the way the service does: a source is allowed to over-return, so
        // what the user would actually see is what gets asserted on.
        return PaletteRanking.Rank(query, candidates).Select(m => m.Candidate).ToList();
    }

    /// <summary>The seeded caller, as a principal a gate can read.</summary>
    protected static ClaimsPrincipal CallerPrincipal(string role = nameof(UserRole.User)) =>
        new(new ClaimsIdentity(
            [
                new Claim(HttpOrganizationContext.UserIdClaim, CallerUserId.ToString()),
                new Claim(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
                new Claim(ClaimTypes.Role, role),
            ],
            authenticationType: "Test"));

    private void AssertMentionsNothingOf(
        IReadOnlyList<PaletteCandidate> results, string name, string shortName, int projectId, string when)
    {
        // Every field, not just the title: a subtitle naming the customer or an
        // href carrying their solution id leaks exactly as much as a title does.
        // SearchOnly is in the list although it never reaches the browser - a
        // source that put a Private customer there would still be answering
        // "yes, that name exists" to anyone who typed it.
        foreach (var candidate in results)
        {
            foreach (var field in new[]
                     {
                         candidate.Title, candidate.Subtitle, candidate.Href,
                         candidate.ShortName, candidate.SearchOnly,
                     })
            {
                if (string.IsNullOrEmpty(field)) continue;
                field.Should().NotContainEquivalentOf(name, "{0} must not leak the name", when);
                field.Should().NotContainEquivalentOf(shortName, "{0} must not leak the short name", when);
            }

            candidate.Href.Should().NotMatchRegex($@"(^|\D){projectId}(\D|$)",
                "{0} must not leak the solution's id in a link", when);
        }
    }

    /// <summary>
    /// Seeds two organisations, three solutions and one row per solution from
    /// the source under test. Idempotent - each inherited case calls it.
    /// </summary>
    protected async Task SeedWorldAsync()
    {
        if (_seeded) return;
        _seeded = true;

        await using (var ctx = Db.NewContext())
        {
            ctx.Users.AddRange(
                NewUser(CallerUserId, TestDb.DefaultOrgId, "caller@cronus.test", UserRole.User),
                NewUser(StrangerUserId, TestDb.DefaultOrgId, "stranger@cronus.test", UserRole.User));
            await ctx.SaveChangesAsync();
        }

        _visibleProjectId = await SeedProjectAsync(
            TestDb.DefaultOrgId, VisibleName, VisibleShortName, ProjectVisibility.Public, CallerUserId);
        _privateProjectId = await SeedProjectAsync(
            TestDb.DefaultOrgId, PrivateName, PrivateShortName, ProjectVisibility.Private, StrangerUserId);
        _otherOrgProjectId = await SeedProjectAsync(
            TestDb.OtherOrgId, OtherOrgName, OtherOrgShortName, ProjectVisibility.Public, null);

        await SeedRowForAsync(TestDb.DefaultOrgId, _visibleProjectId, VisibleName, VisibleShortName);
        await SeedRowForAsync(TestDb.DefaultOrgId, _privateProjectId, PrivateName, PrivateShortName);
        await SeedRowForAsync(TestDb.OtherOrgId, _otherOrgProjectId, OtherOrgName, OtherOrgShortName);

        // Act as the caller for the rest of the fixture. Set last, so the seeding
        // above ran without a user - a seed that needed one would be hiding a
        // grant the assertions below assume away.
        Db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        Db.OrgContext.CurrentUserId = CallerUserId;
        Db.OrgContext.IsSiteAdmin = false;
    }

    private async Task<int> SeedProjectAsync(
        int organizationId, string name, string shortName, ProjectVisibility visibility, int? ownerId)
    {
        var previousOrg = Db.OrgContext.CurrentOrganizationId;
        Db.OrgContext.CurrentOrganizationId = organizationId;
        try
        {
            await using var ctx = Db.NewContext();
            var project = new OeProject
            {
                OrganizationId = organizationId,
                Name = name,
                ShortName = shortName,
                DefaultArtifactCountry = "dk",
                CreatedByUserId = ownerId,
                Visibility = visibility,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            return project.Id;
        }
        finally
        {
            Db.OrgContext.CurrentOrganizationId = previousOrg;
        }
    }

    private async Task SeedRowForAsync(int organizationId, int projectId, string title, string shortName)
    {
        var previousOrg = Db.OrgContext.CurrentOrganizationId;
        Db.OrgContext.CurrentOrganizationId = organizationId;
        try
        {
            await using var ctx = Db.NewContext();
            await SeedRowAsync(ctx, new PaletteSourceSeed(organizationId, projectId, title, shortName));
            await ctx.SaveChangesAsync();
        }
        finally
        {
            Db.OrgContext.CurrentOrganizationId = previousOrg;
        }
    }

    private static User NewUser(int id, int organizationId, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };
}

/// <summary>What a source under test is built from - the same three things it gets from DI.</summary>
public sealed record PaletteSourceUnderTest(
    AppDbContext Db, IOrganizationContext OrgContext, ProjectAccess Access);

/// <summary>
/// One row for the harness to seed: which organisation and solution it belongs
/// to, and the name the harness will search for.
/// </summary>
public sealed record PaletteSourceSeed(int OrganizationId, int ProjectId, string Title, string ShortName);
