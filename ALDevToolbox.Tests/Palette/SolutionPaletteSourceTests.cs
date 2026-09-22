using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Solutions source (#882): the customer row itself, and the four
/// other ways support finds one mid-call - a contact's name, their company, the
/// Voice account number and the tenant id.
///
/// <para>It inherits <see cref="PaletteSourceVisibilityTestBase"/>, and seeds a
/// contact against every solution the harness makes, so the harness's leak case
/// covers the customer-field matches too: a contact of a Private solution is
/// searched for by the customer's name and short name, and must come back as
/// nothing at all.</para>
/// </summary>
public sealed class SolutionPaletteSourceTests : PaletteSourceVisibilityTestBase
{
    /// <summary>The contact seeded against the visible solution, the one a test types.</summary>
    private const string ContactName = "Annette Hill";

    private const string ContactCompany = "Hill Logistics";

    /// <summary>Never searched, never printed - asserted absent from every field.</summary>
    private const string ContactEmail = "annette@hill-logistics.test";

    private const string ContactPhone = "+45 70 20 30 40";

    private const string VoiceNumber = "4711008899";

    private static readonly Guid TenantId = new("6f5f3d6a-7c21-4a6e-9a1e-2b0cf1d4e5a7");

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new SolutionPaletteSource(context.Db, context.Access, Db.NewToolEnablement(context.Db));

    /// <summary>
    /// One contact per solution, named after the solution it belongs to, so the
    /// inherited leak case exercises the contact path as well as the row itself.
    /// Email and phone are seeded precisely because they must never come back.
    /// </summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.OeProjectContacts.Add(new OeProjectContact
        {
            OrganizationId = seed.OrganizationId,
            ProjectId = seed.ProjectId,
            Type = ProjectContactType.Customer,
            Name = $"Annette {seed.ShortName}",
            Company = seed.Title,
            Email = ContactEmail,
            Phone = ContactPhone,
            CreatedAt = DateTime.UtcNow,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// The anonymous caller the harness always tries, plus one whose organisation
    /// has switched Solutions off - the gate the sidebar puts on <c>/solutions</c>.
    /// </summary>
    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        foreach (var entry in base.PrincipalsThatGetNothing()) yield return entry;

        var identity = new ClaimsIdentity(
            [
                new Claim(HttpOrganizationContext.UserIdClaim, CallerUserId.ToString()),
                new Claim(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
                new Claim(ClaimTypes.Role, nameof(UserRole.User)),
                new Claim(ALDevToolbox.Endpoints.EndpointHelpers.DisabledToolsClaim, nameof(ToolKey.Projects)),
            ],
            authenticationType: "Test");
        yield return ("a caller whose organisation has switched Solutions off", new ClaimsPrincipal(identity));
    }

    // ── The row itself ──────────────────────────────────────────────────

    [Fact]
    public async Task A_solution_says_its_short_name_hosting_and_bc_version()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();

        var results = await SearchAsync(VisibleName);

        var row = results.Should().ContainSingle(
            "a customer with several contacts is still one row").Subject;
        row.Kind.Should().Be("solution");
        row.Title.Should().Be(VisibleName);
        row.Href.Should().Be($"/solutions/{VisibleProjectId}");
        row.Subtitle.Should().Be($"{VisibleShortName} - Microsoft cloud - BC 25.3",
            "the palette says what the Solutions list's own Hosted by and BC version columns say");
    }

    [Fact]
    public async Task A_connected_solution_says_the_version_its_production_environment_reports()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();
        await using (var ctx = Db.NewContext())
        {
            // Connected (the stored secret is not a real ciphertext - nothing decrypts it)
            // with a Production environment Business Central has reported on (#907).
            var project = await ctx.OeProjects.FirstAsync(p => p.Id == VisibleProjectId);
            project.BcClientId = "11111111-2222-3333-4444-555555555555";
            project.BcClientSecretEncrypted = "not-a-real-ciphertext";
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = VisibleProjectId, Name = "Production", Type = "Production",
                Version = "26.1.30000.0", FetchedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var results = await SearchAsync(VisibleName);

        results.Should().ContainSingle().Which.Subtitle.Should().Be($"{VisibleShortName} - Microsoft cloud - 26.1.30000.0",
            "the palette and the Solutions list read the version the same way");
    }

    [Fact]
    public async Task A_deleted_solution_is_not_offered()
    {
        await SeedWorldAsync();

        await using (var ctx = Db.NewContext())
        {
            var project = await ctx.OeProjects.FirstAsync(p => p.Id == VisibleProjectId);
            project.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var results = await SearchAsync(VisibleName);

        results.Should().BeEmpty("a row that opens on a deleted solution is a dead end");
    }

    // ── The customer fields support types mid-call ──────────────────────

    [Fact]
    public async Task A_contact_name_finds_the_solution_and_the_row_says_which_contact()
    {
        await SeedWorldAsync();
        await AddContactAsync(VisibleProjectId, ContactName, ContactCompany);

        var results = await SearchAsync(ContactName);

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be(VisibleName, "the result is the Solution, whatever matched");
        row.Href.Should().Be($"/solutions/{VisibleProjectId}");
        row.Subtitle.Should().Be($"contact: {ContactName}");
    }

    [Fact]
    public async Task A_contact_match_never_carries_a_phone_number_or_an_email()
    {
        await SeedWorldAsync();
        await AddContactAsync(VisibleProjectId, ContactName, ContactCompany);

        var results = await SearchAsync(ContactName);

        // Every field, including the one that never leaves the server: a phone
        // number is personal data whether or not the browser would draw it.
        foreach (var field in results.SelectMany(r =>
                     new[] { r.Title, r.Subtitle, r.Href, r.ShortName, r.SearchOnly }))
        {
            (field ?? string.Empty).Should().NotContain("@", "an email address is never searched or shown");
            (field ?? string.Empty).Should().NotContain("70 20 30 40", "a phone number is never searched or shown");
        }
    }

    [Fact]
    public async Task A_contacts_company_finds_the_solution_and_the_row_names_both()
    {
        await SeedWorldAsync();
        await AddContactAsync(VisibleProjectId, ContactName, ContactCompany);

        var results = await SearchAsync("hill logistics");

        results.Should().ContainSingle()
            .Which.Subtitle.Should().Be($"contact: {ContactName} at {ContactCompany}",
                "the company is on the row when it is why the row is there");
    }

    [Fact]
    public async Task A_contact_of_a_private_solution_the_caller_is_not_on_is_not_findable()
    {
        await SeedWorldAsync();
        await AddContactAsync(PrivateProjectId, "Bo Lund", "Contoso Holdings A/S");

        var results = await SearchAsync("bo lund");

        results.Should().BeEmpty(
            "the customer-field matches obey the same visibility rule as the row they would return");
    }

    [Fact]
    public async Task The_voice_account_number_finds_the_solution_without_printing_it()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();

        var results = await SearchAsync(VoiceNumber);

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be(VisibleName);
        row.Subtitle.Should().Be("Voice account number", "the row names the field, never the value");
        (row.Subtitle + row.Title + row.Href).Should().NotContain(VoiceNumber);
    }

    [Fact]
    public async Task The_tenant_id_finds_the_solution_without_printing_it()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();

        var results = await SearchAsync(TenantId.ToString("D"));

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be(VisibleName);
        row.Subtitle.Should().Be("Tenant ID");
    }

    // ── Through the service, the way the endpoint runs it ───────────────

    [Fact]
    public async Task An_exact_short_name_arrives_as_the_palettes_top_hit()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();

        var result = await SearchThroughServiceAsync(VisibleShortName);

        result.Top.Should().NotBeNull("an exact short name is the one row lifted above every group");
        result.Top!.Href.Should().Be($"/solutions/{VisibleProjectId}");
        result.Groups.SelectMany(g => g.Items).Select(i => i.Href)
            .Should().NotContain($"/solutions/{VisibleProjectId}", "the top hit is shown once");
    }

    [Fact]
    public async Task What_a_row_was_found_by_never_reaches_the_browser()
    {
        await SeedWorldAsync();
        await StampCustomerInfoAsync();

        var result = await SearchThroughServiceAsync(VoiceNumber);
        var json = JsonSerializer.Serialize(result);

        json.Should().Contain(VisibleName, "the row itself is what the search found");
        json.Should().NotContain(VoiceNumber,
            "the searched-only field is not on the wire contract, so the number cannot reach the browser");
        json.Should().NotContain("searchOnly").And.NotContain("SearchOnly");
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    /// <summary>Runs the source the way <c>GET /palette/search</c> does.</summary>
    private async Task<PaletteSearchResult> SearchThroughServiceAsync(string query)
    {
        await using var ctx = Db.NewContext();
        var source = CreateSource(new PaletteSourceUnderTest(ctx, Db.OrgContext, new ProjectAccess(ctx, Db.OrgContext)));
        var service = new PaletteSearchService([source], NullLogger<PaletteSearchService>.Instance);

        return await service.SearchAsync(CallerPrincipal(), query, CancellationToken.None);
    }

    /// <summary>The customer facts the Solutions list shows, and the two a result may be found by.</summary>
    private async Task StampCustomerInfoAsync()
    {
        await using var ctx = Db.NewContext();
        var project = await ctx.OeProjects.FirstAsync(p => p.Id == VisibleProjectId);
        project.HostingType = ProjectHostingType.MicrosoftCloud;
        project.BcVersion = "BC 25.3";
        project.VoiceAccountNumber = VoiceNumber;
        project.BcTenantId = TenantId;
        await ctx.SaveChangesAsync();
    }

    private async Task AddContactAsync(int projectId, string name, string company)
    {
        await using var ctx = Db.NewContext();
        ctx.OeProjectContacts.Add(new OeProjectContact
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Type = ProjectContactType.Customer,
            Name = name,
            Company = company,
            Email = ContactEmail,
            Phone = ContactPhone,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
