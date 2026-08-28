using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the modal overlay, which PR 15a moved from the legacy
/// <c>.confirm-modal</c> family in base.css onto the design system's
/// <c>.confirm-dialog</c> in components.css.
///
/// Three of the four things here fail silently, and one of them shipped broken
/// for an afternoon before a screenshot caught it.
///
/// <b>The backdrop only covers the viewport by borrowing its parent.</b>
/// <c>.modal-backdrop</c> is <c>position: absolute; inset: 0</c> because the
/// design project demonstrates it inside a review frame. It is full-screen here
/// only because <c>.modal-layer</c> is <c>position: fixed; inset: 0</c>. Relax
/// the layer to static and the backdrop collapses onto the dialog itself —
/// still a visible dialog, no error, and nothing behind it dimmed.
///
/// <b>The rounded corners are prototype scaffolding.</b> That same review frame
/// gives <c>.modal-backdrop</c> a <c>border-radius</c>, which on a full-viewport
/// scrim rounds the corners of the screen. The reset in the layer looks like a
/// dead rule and is not.
///
/// <b>A menu that outranks the modal punches through it.</b> The legacy
/// <c>.ra__pop</c> sat at <c>z-index: 80</c> against the layer's 50, so picking
/// "Compare with..." from a row's kebab opened the release picker *under* the
/// still-open menu — lit, un-dimmed and still clickable over the scrim. Nothing
/// about the markup says so. (PR 15b then deleted <c>.ra__pop</c> outright; the
/// design system's <c>.ra__menu</c> is what the assertion watches now.)
/// Meanwhile the reconnect overlay has to stay on top of everything: if the
/// circuit drops while a
/// dialog is open, the dialog is dead and the reconnect notice is the only
/// thing worth seeing.
///
/// <b>A dialog nobody ported keeps rendering.</b> The classes it names are gone
/// from every sheet, so it renders as an unstyled block in the top-left corner
/// with nothing dimmed behind it.
/// </summary>
public sealed class ModalLayerTests
{
    private const string Components = "ALDevToolbox/wwwroot/components.css";
    private const string Shell = "ALDevToolbox/wwwroot/shell.css";

    private static readonly string[] Sheets =
    [
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/app.css",
        "ALDevToolbox/wwwroot/code-editor.css",
        "ALDevToolbox/wwwroot/source-viewer.css",
        "ALDevToolbox/wwwroot/shell.css",
        "ALDevToolbox/wwwroot/pages.css",
        "ALDevToolbox/wwwroot/pages-forms.css",
        "ALDevToolbox/wwwroot/pages-content.css",
        "ALDevToolbox/wwwroot/pages-power.css",
    ];

    // ── The layer the backdrop resolves against ────────────────────────

    [Fact]
    public void The_modal_layer_is_a_fixed_full_cover_parent()
    {
        var layer = Rule(Read(Components), ".modal-layer");
        layer.Should().NotBeNull(because: ".modal-layer is what makes .modal-backdrop full-screen");
        layer.Should().Contain("position: fixed",
            because: "an absolutely-positioned backdrop resolves against its nearest positioned "
                   + "ancestor; a static layer collapses the scrim onto the dialog and dims nothing");
        layer.Should().Contain("inset: 0",
            because: "the layer has to be the viewport for `inset: 0` on the backdrop to mean the viewport");
    }

    [Fact]
    public void The_backdrop_loses_its_prototype_frame_radius_inside_the_layer()
    {
        Rule(Read(Components), ".modal-backdrop").Should().Contain("border-radius",
            because: "this test only matters while the component still carries one; "
                   + "if the design project drops it upstream, drop the reset with it");

        Rule(Read(Components), ".modal-layer > .modal-backdrop").Should()
            .NotBeNull(because: "the component's radius is scaffolding from the review frame it was "
                              + "drawn in; on a full-viewport scrim it rounds the corners of the screen")
            .And.Contain("border-radius: 0");
    }

    // ── Stacking, which no markup states ───────────────────────────────

    [Fact]
    public void A_row_action_menu_never_draws_over_an_open_dialog()
    {
        const string selector = ".ra__menu";
        var menu = ZIndex(Rule(Read(Components), selector));
        menu.Should().NotBeNull(because: $"{selector} is a popup and needs a z-index to be a popup");
        menu.Should().BeLessThan(ModalLayerZ(),
            because: "\"Compare with...\" in a row's kebab opens a dialog, and the menu it was "
                   + "picked from is still open behind it — above the layer, it stays lit and "
                   + "clickable over the scrim");
    }

    [Fact]
    public void The_reconnect_overlay_still_outranks_a_dialog()
    {
        ZIndex(Rule(Read(Shell), ".reconnect")).Should().BeGreaterThan(ModalLayerZ(),
            because: "when the circuit drops, an open dialog is inert — the reconnect notice is "
                   + "the only thing on screen that can still do anything");
    }

    // ── Nothing left behind ────────────────────────────────────────────

    [Fact]
    public void The_legacy_confirm_modal_family_is_gone_from_every_sheet()
    {
        foreach (var sheet in Sheets)
        {
            Selectors(Read(sheet)).Should().NotContain(sel => sel.Contains("confirm-modal"),
                because: $"{sheet} would keep styling a class the markup no longer renders, "
                       + "which reads as \"still supported\" to the next author");
        }
    }

    [Fact]
    public void Every_overlay_in_the_app_is_on_the_ported_vocabulary()
    {
        var offenders = Razors()
            .Where(f => RenderedClasses(StripComments(File.ReadAllText(f))).Contains("confirm-modal"))
            .Select(Relative)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the class has no rules left, so a missed overlay renders as an unstyled "
                   + "block in the top-left corner with nothing dimmed behind it");
    }

    [Fact]
    public void Every_modal_layer_wraps_a_backdrop_and_a_dialog()
    {
        foreach (var file in Razors())
        {
            var classes = RenderedClasses(StripComments(File.ReadAllText(file))).ToHashSet();
            if (!classes.Contains("modal-layer"))
            {
                continue;
            }

            classes.Should().Contain("modal-backdrop",
                because: $"{Relative(file)} opens a layer with nothing dimming the page under it");
            classes.Should().Contain("confirm-dialog",
                because: $"{Relative(file)} opens a layer with no panel in it — the layer is "
                       + "`display: grid; place-items: center` around whatever it holds, so a "
                       + "raw div lands centred and unstyled");
        }
    }

    // ── Helpers (mirroring CompareScreenTests) ─────────────────────────

    private static int ModalLayerZ() =>
        ZIndex(Rule(Read(Components), ".modal-layer"))
        ?? throw new InvalidOperationException(".modal-layer has no z-index");

    private static int? ZIndex(string? body)
    {
        if (body is null)
        {
            return null;
        }

        var m = Regex.Match(body, @"z-index:\s*(?<v>-?\d+)");
        return m.Success ? int.Parse(m.Groups["v"].Value) : null;
    }

    private static IEnumerable<string> Razors() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "ALDevToolbox/Components"), "*.razor",
            SearchOption.AllDirectories);

    private static string Relative(string full) =>
        Path.GetRelativePath(Root(), full).Replace('\\', '/');

    private static string StripComments(string razor) =>
        Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);

    private static IEnumerable<string> RenderedClasses(string markup) =>
        Regex.Matches(markup, @"class=""(?<v>[^""]*)""")
            .SelectMany(m => Regex.Replace(m.Groups["v"].Value, @"@\([^)]*\)", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => c.Length > 0 && !c.StartsWith('@'));

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            yield return (m.Groups["sel"].Value.Trim(), m.Groups["body"].Value);
        }
    }

    private static IEnumerable<string> Selectors(string css) => Rules(css).Select(r => r.Selector);

    private static string? Rule(string css, string selector) =>
        Rules(css).FirstOrDefault(r => r.Selector.Split(',')
            .Any(s => s.Trim() == selector)).Body;

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
