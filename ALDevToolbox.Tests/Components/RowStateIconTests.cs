using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the three-part contract <see cref="RowStateIcon"/>'s own doc comment
/// states: a row's keyline class, its glyph and its accessible label always
/// agree, and every keyline class resolves to a real <c>--bar-*</c> token.
///
/// This test exists because the same defect has now been shipped twice. A state
/// with no arm falls through to <c>("queued", "clock", ...)</c>, which draws a
/// clock and says "waiting to start" — so <c>expired</c> and <c>disabled</c>
/// rows read as pending until PR 9a added their arms, and a repository provider
/// with no token read as a *draft* (a pencil, "someone is editing this") until
/// PR 9b added <c>not-connected</c>. The fall-through is deliberate and stays;
/// what this catches is a caller passing a state nobody mapped.
/// </summary>
public sealed class RowStateIconTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public RowStateIconTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Every state any page actually passes. Add the state here in the same
    /// commit that adds its arm — an entry missing from this list is a state
    /// silently taking the fall-through.
    /// </summary>
    public static TheoryData<string, string, string, string> MappedStates() => new()
    {
        // state            row class      glyph            label
        { "succeeded",      "succeeded",   "circle-check",  "Succeeded" },
        { "failed",         "failed",      "circle-x",      "Failed" },
        { "running",        "running",     "refresh-cw",    "Running" },
        { "queued",         "queued",      "clock",         "Queued" },
        { "cancelled",      "cancelled",   "x",             "Cancelled" },
        { "published",      "published",   "rocket",        "Published" },
        { "active",         "published",   "circle-check",  "Active" },
        { "deprecated",     "archived",    "archive",       "Deprecated" },
        { "draft",          "draft",       "pencil",        "Draft" },
        { "archived",       "archived",    "archive",       "Archived" },
        { "deleted",        "archived",    "trash-2",       "Deleted" },
        { "revoked",        "archived",    "shield-off",    "Revoked" },
        { "expired",        "archived",    "clock",         "Expired" },
        { "disabled",       "archived",    "circle-x",      "Disabled" },
        { "connected",      "published",   "circle-check",  "Connected" },
        { "not-connected",  "queued",      "plug",          "Not connected" },
        { "new",            "new",         "circle-plus",   "New" },
        { "modified",       "modified",    "pencil",        "Modified" },
        { "unchanged",      "unchanged",   "minus",         "Unchanged" },
        { "untranslated",   "untranslated","circle",        "Untranslated" },
        { "fuzzy",          "fuzzy",       "circle-alert",  "Needs review" },
        { "translated",     "translated",  "check",         "Translated" },
        { "final",          "final",       "check-check",   "Final" },
    };

    [Theory]
    [MemberData(nameof(MappedStates))]
    public void Class_glyph_and_label_agree(string state, string rowClass, string glyph, string label)
    {
        RowStateIcon.RowClass(state).Should().Be("is-" + rowClass);

        var cut = _ctx.RenderComponent<RowStateIcon>(p => p.Add(c => c.State, state));
        var span = cut.Find("span.data-table__state");
        span.GetAttribute("aria-label").Should().Be(label);
        span.GetAttribute("title").Should().Be(label);
        cut.Find("svg").GetAttribute("class").Should().Contain($"lucide-{glyph}");
    }

    [Theory]
    [MemberData(nameof(MappedStates))]
    public void Every_row_class_resolves_to_a_bar_token(string state, string rowClass, string glyph, string label)
    {
        _ = glyph;
        _ = label;
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "ALDevToolbox", "wwwroot", "tokens.css"));
        tokens.Should().Contain($"--bar-{rowClass}:",
            $"the '{state}' arm draws its keyline from --bar-{rowClass}, which must exist or the row shows no colour at all");
    }

    /// <summary>
    /// The one thing a table row must never do: carry a status pill. Guards the
    /// design system's "status placement, one rule" - the row's state is the
    /// edge keyline plus this glyph, never a pill in a cell.
    /// </summary>
    [Fact]
    public void Renders_a_glyph_rather_than_a_pill()
    {
        var cut = _ctx.RenderComponent<RowStateIcon>(p => p.Add(c => c.State, "failed"));
        cut.Markup.Should().NotContain("status-pill");
    }

    [Fact]
    public void An_unmapped_state_still_renders_a_readable_name_rather_than_an_empty_cell()
    {
        var cut = _ctx.RenderComponent<RowStateIcon>(p => p.Add(c => c.State, "flibbertigibbet"));
        cut.Find("span.data-table__state").GetAttribute("aria-label").Should().Be("Flibbertigibbet");
        RowStateIcon.RowClass("flibbertigibbet").Should().Be("is-queued");
    }

    [Fact]
    public void Label_overrides_the_state_word_when_the_page_has_a_better_one()
    {
        var cut = _ctx.RenderComponent<RowStateIcon>(p => p
            .Add(c => c.State, "unhealthy")
            .Add(c => c.Label, "Cannot reach the database"));
        cut.Find("span.data-table__state").GetAttribute("aria-label").Should().Be("Cannot reach the database");
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
