using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// Where the numbers in <c>.design/command-palette.md</c>, "Budget", come from
/// (#889): it seeds an organisation the size that section describes, runs the
/// palette's own fan-out against it, and prints p50 / p95 per source and for the
/// whole search, followed by <c>EXPLAIN (ANALYZE, BUFFERS)</c> for each source's
/// query.
///
/// <para><b>It does not run in the suite.</b> Seeding and 100 runs per query
/// take a minute, and a timing assertion on a shared CI runner is a flake
/// generator - so this is a measurement to take deliberately when the claim is
/// challenged, not a guard. It reports as skipped unless
/// <c>ALDT_PALETTE_BENCH=1</c>:</para>
/// <code>
/// ALDT_PALETTE_BENCH=1 ALDT_PALETTE_BENCH_OUT=/tmp/palette.md \
///   ALDevToolbox.Tests/bin/Release/net10.0/ALDevToolbox.Tests -class "*PaletteBudgetBenchmark"
/// </code>
/// <para><c>ALDT_PALETTE_BENCH_SOLUTIONS</c>, <c>_RELEASES</c> and
/// <c>_RECIPES</c> resize the fixture - four times the default is what the
/// "Where it degrades first" paragraph was measured at.</para>
///
/// <para>The first query measured in a process reads high (7-9 ms against 4 ms
/// for the same query later): that is the test process still warming up, not the
/// query. Reversing the query order moves the tax to whichever query is first,
/// which is how that was established.</para>
/// </summary>
public sealed class PaletteBudgetBenchmark : IDisposable
{
    private const int Org = TestDb.DefaultOrgId;
    private const int AdminUserId = 9600;
    private const int MemberUserId = 9601;
    private const int TeamId = 9602;

    private const int MemberNoTeamUserId = 9603;

    private static readonly int Solutions = Size("SOLUTIONS", 500);
    private const int EnvironmentsPerSolution = 3;
    private static readonly int Releases = Size("RELEASES", 200);
    private static readonly int Recipes = Size("RECIPES", 300);
    private const int ContactsPerSolution = 2;

    private static int Size(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("ALDT_PALETTE_BENCH_" + name), out var v) ? v : fallback;

    private const int Warmup = 40;
    private const int Runs = 60;

    private static readonly string[] Queries =
    [
        "cro", "cronus prod", "annette", "26.0 dk", "posting", "ab", "zzqqxx",
    ];

    private readonly TestDb _db = new();
    private readonly StringBuilder _out = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Measure()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ALDT_PALETTE_BENCH") == "1",
            "throwaway harness; set ALDT_PALETTE_BENCH=1 to run it");

        var seedStarted = Stopwatch.GetTimestamp();
        await SeedAsync();
        _out.AppendLine($"Seed took {(int)Stopwatch.GetElapsedTime(seedStarted).TotalSeconds} s");
        await ReportCountsAsync();

        // One pass over everything before anything is measured: the first query
        // of a process pays for cold shared buffers and an unplanned statement,
        // and that cost belongs to the fixture rather than to the source that
        // happened to run first.
        foreach (var (_, principal, userId) in Callers())
        {
            _db.OrgContext.CurrentUserId = userId;
            foreach (var q in Queries)
            {
                await using var ctx = _db.NewContext();
                var acc = new ProjectAccess(ctx, _db.OrgContext);
                var service = new PaletteSearchService(
                    SourcesFor(ctx, acc), NullLogger<PaletteSearchService>.Instance);
                await service.SearchAsync(principal, q, default);
            }
        }

        foreach (var (who, principal, userId) in Callers())
        {
            _db.OrgContext.CurrentUserId = userId;
            _db.OrgContext.IsSiteAdmin = false;
            _out.AppendLine();
            _out.AppendLine($"## {who}");
            _out.AppendLine();
            _out.AppendLine("| query | access | solutions | environments | releases | recipes | WHOLE |");
            _out.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

            foreach (var q in Queries)
            {
                var row = await MeasureAsync(principal, q);
                _out.AppendLine(row);
            }
        }

        await ExplainAsync();

        var path = Environment.GetEnvironmentVariable("ALDT_PALETTE_BENCH_OUT")
                   ?? Path.Combine(Path.GetTempPath(), "palette-bench.md");
        await File.WriteAllTextAsync(path, _out.ToString());
    }

    /// <summary>
    /// The three callers whose SQL differs: an Admin bypasses visibility
    /// entirely, a member on a team pays the team join, and a member on none
    /// pays the predicate without it.
    /// </summary>
    private static IEnumerable<(string Who, ClaimsPrincipal Principal, int UserId)> Callers()
    {
        yield return ("Org admin (bypasses visibility)", Principal(AdminUserId, UserRole.Admin), AdminUserId);
        yield return ("Plain member on one team", Principal(MemberUserId, UserRole.User), MemberUserId);
        yield return ("Plain member on no team", Principal(MemberNoTeamUserId, UserRole.User), MemberNoTeamUserId);
    }

    private static ClaimsPrincipal Principal(int userId, UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim(HttpOrganizationContext.UserIdClaim, userId.ToString()),
                new Claim(HttpOrganizationContext.OrganizationIdClaim, Org.ToString()),
                new Claim(ClaimTypes.Role, role.ToString()),
            ],
            authenticationType: "Test"));

    // ── Measuring ───────────────────────────────────────────────────────

    private async Task<string> MeasureAsync(ClaimsPrincipal principal, string rawQuery)
    {
        var query = PaletteQuery.Parse(rawQuery);
        var access = new List<double>();
        var perSource = new Dictionary<string, List<double>>
        {
            ["solutions"] = [], ["environments"] = [], ["releases"] = [], ["recipes"] = [],
        };
        var whole = new List<double>();
        var rowCount = 0;

        for (var i = 0; i < Warmup + Runs; i++)
        {
            var measured = i >= Warmup;

            await using (var ctx = _db.NewContext())
            {
                var acc = new ProjectAccess(ctx, _db.OrgContext);
                var started = Stopwatch.GetTimestamp();
                await acc.GetSnapshotAsync(CancellationToken.None);
                if (measured) access.Add(Ms(started));

                foreach (var source in SourcesFor(ctx, acc))
                {
                    var t = Stopwatch.GetTimestamp();
                    var found = await source.SearchAsync(query, PaletteSearchService.CandidatesPerSource, default);
                    if (measured) perSource[source.Id].Add(Ms(t));
                    if (measured && i == Warmup) rowCount += found.Count;
                }
            }

            await using (var ctx = _db.NewContext())
            {
                var acc = new ProjectAccess(ctx, _db.OrgContext);
                var service = new PaletteSearchService(
                    SourcesFor(ctx, acc), NullLogger<PaletteSearchService>.Instance);
                var t = Stopwatch.GetTimestamp();
                await service.SearchAsync(principal, rawQuery, default);
                if (measured) whole.Add(Ms(t));
            }
        }

        return $"| `{rawQuery}` ({rowCount} cand) | {Cell(access)} | {Cell(perSource["solutions"])} "
               + $"| {Cell(perSource["environments"])} | {Cell(perSource["releases"])} "
               + $"| {Cell(perSource["recipes"])} | {Cell(whole)} |";
    }

    private List<IPaletteSource> SourcesFor(AppDbContext ctx, ProjectAccess access) =>
    [
        new SolutionPaletteSource(ctx, access, _db.NewToolEnablement(ctx)),
        new EnvironmentPaletteSource(ctx, access, _db.NewToolEnablement(ctx)),
        new ReleasePaletteSource(ctx, access, _db.NewToolEnablement(ctx)),
        new RecipePaletteSource(ctx, TestDb.EverythingEnabled()),
    ];

    private static double Ms(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;

    private static string Cell(List<double> samples)
    {
        if (samples.Count == 0) return "-";
        samples.Sort();
        return $"{Pct(samples, 0.50):0.0} / {Pct(samples, 0.95):0.0}";
    }

    private static double Pct(List<double> sorted, double p)
    {
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    // ── EXPLAIN ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs every source once with an interceptor that re-issues each command as
    /// EXPLAIN (ANALYZE, BUFFERS) on the same connection.
    /// </summary>
    private async Task ExplainAsync()
    {
        _db.OrgContext.CurrentUserId = MemberUserId;
        foreach (var raw in new[] { "cro", "cronus prod", "26.0 dk", "posting" })
        {
            var interceptor = new ExplainInterceptor();
            await using var ctx = _db.NewContext(interceptor);
            var acc = new ProjectAccess(ctx, _db.OrgContext);
            await acc.GetSnapshotAsync(default);
            interceptor.On = true;
            foreach (var source in SourcesFor(ctx, acc))
            {
                interceptor.Label = $"{source.Id} / '{raw}'";
                await source.SearchAsync(PaletteQuery.Parse(raw), PaletteSearchService.CandidatesPerSource, default);
            }
            interceptor.On = false;
            _out.AppendLine();
            _out.AppendLine(interceptor.Report.ToString());
        }
    }

    private sealed class ExplainInterceptor : DbCommandInterceptor
    {
        public bool On { get; set; }
        public string Label { get; set; } = string.Empty;
        public StringBuilder Report { get; } = new();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!On) return result;

            Report.AppendLine($"### EXPLAIN {Label}");
            Report.AppendLine("```");
            Report.AppendLine(command.CommandText);
            Report.AppendLine("--");
            await using var explain = command.Connection!.CreateCommand();
            explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + command.CommandText;
            foreach (DbParameter p in command.Parameters)
            {
                var copy = new NpgsqlParameter(p.ParameterName, p.Value);
                explain.Parameters.Add(copy);
            }
            await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken)) Report.AppendLine(reader.GetString(0));
            }
            Report.AppendLine("```");
            return result;
        }
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private async Task ReportCountsAsync()
    {
        await using var ctx = _db.NewContext();
        _out.AppendLine($"Fixture: {await ctx.OeProjects.CountAsync()} solutions, "
            + $"{await ctx.OeProjectEnvironments.CountAsync()} environments, "
            + $"{await ctx.OeReleases.CountAsync()} releases, "
            + $"{await ctx.Recipes.CountAsync()} recipes, "
            + $"{await ctx.OeProjectContacts.CountAsync()} contacts, "
            + $"{await ctx.OeProjectBuilds.CountAsync()} builds");
    }

    private static readonly string[] FirstWords =
    [
        "CRONUS", "Contoso", "Fabrikam", "Nordwind", "Adventure", "Litware", "Proseware",
        "Tailspin", "Woodgrove", "Northwind", "Moller", "Møller", "Dansk", "Nordic", "Baltic",
    ];

    private static readonly string[] SecondWords =
    [
        "Coffee", "Holdings", "Logistics", "Trading", "Industries", "Foods", "Systems",
        "Bakery", "Marine", "Energy", "Retail", "Pharma", "Textiles", "Motors", "Consulting",
    ];

    private static readonly string[] ContactFirstNames =
    [
        "Annette", "Bjorn", "Camilla", "Dennis", "Eva", "Frederik", "Gitte", "Henrik",
        "Ida", "Jesper", "Karin", "Lars", "Mette", "Niels", "Ole", "Pia",
    ];

    private static readonly string[] RecipeWords =
    [
        "Posting", "Validation", "Upgrade", "Permission", "Report", "Interface", "Telemetry",
        "Dimension", "Journal", "Ledger", "Notification", "Webhook", "Queue", "Isolated Storage",
    ];

    private async Task SeedAsync()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Users.AddRange(
                NewUser(AdminUserId, "admin@cronus.test", UserRole.Admin),
                NewUser(MemberUserId, "member@cronus.test", UserRole.User),
                NewUser(MemberNoTeamUserId, "solo@cronus.test", UserRole.User));
            ctx.Teams.Add(new Team
            {
                Id = TeamId, OrganizationId = Org, Name = "Delivery",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = Org, TeamId = TeamId, UserId = MemberUserId, CreatedAt = DateTime.UtcNow,
            });

            for (var i = 0; i < 6; i++)
            {
                ctx.ApplicationVersions.Add(new ApplicationVersion
                {
                    OrganizationId = Org, Key = $"bc{24 + i}", Name = $"{24 + i}.0.0.0",
                    Application = $"{24 + i}.0.0.0", Runtime = $"1{i}.0", Ordering = i,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync();
        }

        var hostings = Enum.GetValues<ProjectHostingType>();
        var projectIds = new List<int>(Solutions);
        var privateWithTeam = new List<int>();

        await using (var ctx = _db.NewContext())
        {
            for (var i = 0; i < Solutions; i++)
            {
                var name = $"{FirstWords[i % FirstWords.Length]} {SecondWords[(i / 3) % SecondWords.Length]} {i:000}";
                var isPrivate = i % 7 == 0;
                ctx.OeProjects.Add(new OeProject
                {
                    OrganizationId = Org,
                    Name = name,
                    ShortName = ShortNameFor(i),
                    DefaultArtifactCountry = i % 2 == 0 ? "dk" : "w1",
                    HostingType = hostings[i % hostings.Length],
                    BcVersion = $"{24 + (i % 5)}.{i % 3}",
                    VoiceAccountNumber = i % 3 == 0 ? $"47110{i:0000}" : null,
                    BcTenantId = i % 4 == 0 ? Guid.NewGuid() : null,
                    Visibility = isPrivate ? ProjectVisibility.Private : ProjectVisibility.Public,
                    // Private solutions belong to nobody the caller is: half of
                    // them are reachable through the team instead.
                    CreatedByUserId = isPrivate ? AdminUserId : null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync();
            projectIds.AddRange(await ctx.OeProjects.Select(p => p.Id).ToListAsync());
        }

        await using (var ctx = _db.NewContext())
        {
            var privates = await ctx.OeProjects
                .Where(p => p.Visibility == ProjectVisibility.Private)
                .Select(p => p.Id).ToListAsync();
            for (var i = 0; i < privates.Count; i += 2)
            {
                privateWithTeam.Add(privates[i]);
                ctx.OeProjectTeams.Add(new OeProjectTeam
                {
                    OrganizationId = Org, ProjectId = privates[i], TeamId = TeamId, CreatedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync();
        }

        // Environments and contacts.
        await using (var ctx = _db.NewContext())
        {
            var names = new[] { "Production", "Sandbox", "Preproduction" };
            for (var i = 0; i < projectIds.Count; i++)
            {
                for (var e = 0; e < EnvironmentsPerSolution; e++)
                {
                    ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
                    {
                        OrganizationId = Org,
                        ProjectId = projectIds[i],
                        Name = names[e],
                        Type = e == 0 ? "Production" : "Sandbox",
                        Status = BcEnvironmentStatus.Active,
                        Version = $"2{4 + (i % 5)}.{i % 3}.41125.0",
                        FetchedAt = DateTime.UtcNow,
                    });
                }

                for (var c = 0; c < ContactsPerSolution; c++)
                {
                    ctx.OeProjectContacts.Add(new OeProjectContact
                    {
                        OrganizationId = Org,
                        ProjectId = projectIds[i],
                        Type = ProjectContactType.Customer,
                        Name = $"{ContactFirstNames[(i + c) % ContactFirstNames.Length]} {SecondWords[i % SecondWords.Length]}sen",
                        Company = $"{FirstWords[i % FirstWords.Length]} A/S",
                        Email = $"contact{i}-{c}@example.test",
                        Phone = "+45 70 20 30 40",
                        CreatedAt = DateTime.UtcNow,
                    });
                }

                if (i % 50 == 0) await ctx.SaveChangesAsync();
            }
            await ctx.SaveChangesAsync();
        }

        // Releases: artifact imports and solution builds.
        await using (var ctx = _db.NewContext())
        {
            var countries = new[] { "dk", "w1", "no", "se" };
            var buildReleases = new List<(int ReleaseIndex, int ProjectId)>();
            for (var i = 0; i < Releases; i++)
            {
                var fromBuild = i % 2 == 0;
                // Artifact imports carry a unique (version, country) dedup key,
                // so walk the space rather than repeating it: k = 0..99 maps to
                // major 24..28, minor 0..4, one of four countries.
                var k = i / 2;
                var major = 24 + (k / 20);
                var minor = (k % 20) / 4;
                ctx.OeReleases.Add(fromBuild
                    ? new OeRelease
                    {
                        OrganizationId = Org,
                        Label = $"Build {i:000} for {FirstWords[i % FirstWords.Length]}",
                        BcVersion = $"{major}.{minor}.1.{i}",
                        Kind = "project",
                        Status = "ready",
                        ProjectName = "stale",
                        ImportedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    }
                    : new OeRelease
                    {
                        OrganizationId = Org,
                        Label = $"Business Central {major}.{minor} ({countries[k % countries.Length].ToUpperInvariant()})",
                        BcVersion = $"{major}.{minor}.12345.{i}",
                        DedupKey = $"bc-onprem:{major}.{minor}:{countries[k % countries.Length]}",
                        Kind = "first_party",
                        Status = "ready",
                        ImportedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                if (fromBuild) buildReleases.Add((i, projectIds[i % projectIds.Count]));
            }
            await ctx.SaveChangesAsync();

            var releaseIds = await ctx.OeReleases.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync();
            foreach (var (index, projectId) in buildReleases)
            {
                ctx.OeProjectBuilds.Add(new OeProjectBuild
                {
                    OrganizationId = Org,
                    ProjectId = projectId,
                    ReleaseId = releaseIds[index],
                    Status = ProjectBuildStatus.Ready,
                    StartedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync();
        }

        // Recipes.
        await using (var ctx = _db.NewContext())
        {
            var versionIds = await ctx.ApplicationVersions.Select(v => v.Id).ToListAsync();
            for (var i = 0; i < Recipes; i++)
            {
                var recipe = RecipeBuilder.Default(
                    $"{RecipeWords[i % RecipeWords.Length]} {SecondWords[(i / 2) % SecondWords.Length]} {i:000}",
                    Org);
                recipe.Keywords = string.Join(", ",
                    RecipeWords[(i + 1) % RecipeWords.Length].ToLowerInvariant(),
                    SecondWords[i % SecondWords.Length].ToLowerInvariant(),
                    "al");
                recipe.MinimumApplicationVersionId = versionIds[i % versionIds.Count];
                ctx.Recipes.Add(recipe);
                if (i % 50 == 0) await ctx.SaveChangesAsync();
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("ANALYZE");
        }

        _db.OrgContext.CurrentOrganizationId = Org;
    }

    private static string ShortNameFor(int i)
    {
        var first = FirstWords[i % FirstWords.Length];
        var second = SecondWords[(i / 3) % SecondWords.Length];
        var prefix = new string(first.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        var suffix = new string(second.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return prefix + suffix;
    }

    private static User NewUser(int id, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = Org,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };
}
