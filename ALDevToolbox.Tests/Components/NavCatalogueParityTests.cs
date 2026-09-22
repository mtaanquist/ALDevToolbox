using System.Reflection;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Keeps the sidebar and the command palette's "Go to" list saying the same
/// thing. They are two renderers over one list
/// (<see cref="NavDestinations.All"/>), but only one of them is generated from
/// it: <c>NavMenu.razor</c> keeps its own markup, because its groups collapse,
/// nest and carry active states that a flat list has no room for.
///
/// <para>So the agreement is enforced here instead. A page added to the sidebar
/// and not to the catalogue is a page the palette silently cannot reach; one
/// added to the catalogue and not the sidebar is a door with no sign on it.
/// Neither breaks anything, which is why nothing else would report them.</para>
///
/// <para>The second half is the same guard the icon catalogue has: a link is
/// only a link if something answers it, and a typo in an <c>href</c> renders
/// perfectly and 404s on click.</para>
/// </summary>
public sealed class NavCatalogueParityTests
{
    /// <summary>
    /// Destinations the palette offers that the sidebar does not, each with the
    /// reason it is reachable some other way. The list is supposed to stay
    /// short: the palette is a faster way to the pages we already have, not a
    /// second navigation with pages of its own.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotInTheSidebar =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/account"] = "the top bar's user button, which the sidebar has no counterpart for",
            ["/docs/mcp"] = "linked from the MCP page and from the sign-in footer, not from the sidebar",
            ["/docs/search"] = "linked from the palette's own footer, which is hidden at phone width",
        };

    [Fact]
    public void Every_page_the_sidebar_links_to_is_in_the_catalogue()
    {
        var missing = SidebarHrefs()
            .Where(href => NavDestinations.All.All(d => d.Href != href))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "the palette renders from NavDestinations, so a page only the sidebar knows about "
            + "cannot be jumped to. Add it to Domain/Navigation/NavDestinations.cs with the same "
            + "gate NavMenu.razor gives it. Missing: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void Every_destination_in_the_catalogue_is_either_in_the_sidebar_or_explained()
    {
        var sidebar = SidebarHrefs();

        var strays = NavDestinations.All
            .Select(d => d.Href)
            .Where(href => !sidebar.Contains(href) && !NotInTheSidebar.ContainsKey(href))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

        strays.Should().BeEmpty(
            "a destination the sidebar does not offer is one the palette invented. Either add it "
            + "to NavMenu.razor, or list it in NotInTheSidebar with how people reach it. "
            + "Unexplained: {0}",
            string.Join(", ", strays));

        var stale = NotInTheSidebar.Keys
            .Where(href => sidebar.Contains(href) || NavDestinations.All.All(d => d.Href != href))
            .ToList();
        stale.Should().BeEmpty(
            "these exceptions no longer describe anything and should be deleted: {0}",
            string.Join(", ", stale));
    }

    [Fact]
    public void Every_destination_resolves_to_a_real_page()
    {
        var routes = RoutableTemplates();
        routes.Should().HaveCountGreaterThan(50,
            "a silently empty route set would make this assertion pass vacuously");

        var broken = NavDestinations.All
            .Where(d => !routes.Contains(d.Href))
            .Select(d => $"{d.Label} -> {d.Href}")
            .ToList();

        broken.Should().BeEmpty(
            "a wrong href renders perfectly and 404s on click. Broken: {0}",
            string.Join(", ", broken));
    }

    [Fact]
    public void Every_destination_has_a_vendored_icon()
    {
        var icons = Directory
            .EnumerateFiles(RepoRoot.Combine("ALDevToolbox", "Resources", "Icons"), "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        var missing = NavDestinations.All
            .Where(d => !icons.Contains(d.Icon))
            .Select(d => $"{d.Label} -> {d.Icon}")
            .ToList();

        missing.Should().BeEmpty(
            "an icon with no SVG renders as an invisible placeholder, and the rows are icon-led. "
            + "Missing: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void No_two_destinations_share_an_href()
    {
        NavDestinations.All.Select(d => d.Href).Should().OnlyHaveUniqueItems(
            "the script re-selects a row by its href when results arrive, so a duplicate would "
            + "make the selection jump");
    }

    /// <summary>
    /// The in-app paths the sidebar links to. External links (the GitHub mark,
    /// the version stamp) are Razor expressions rather than literal paths, so
    /// they never match.
    /// </summary>
    private static HashSet<string> SidebarHrefs()
    {
        var markup = File.ReadAllText(
            RepoRoot.Combine("ALDevToolbox", "Components", "Layout", "NavMenu.razor"));
        markup = Regex.Replace(markup, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        return Regex.Matches(markup, "href=\"(/[^\"@]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every parameterless route the app assembly exposes.</summary>
    private static HashSet<string> RoutableTemplates() =>
        typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true))
            .Select(r => r.Template)
            .ToHashSet(StringComparer.Ordinal);
}
