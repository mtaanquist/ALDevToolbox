using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Sessions tab's list. The named user is a support consultant on the phone to a
/// customer whose posting run has been locked for an hour: they need to find the session
/// that is holding everything, say whose it is, and end it - without reading a wire token
/// or learning anything about this codebase.
/// </summary>
public sealed class EnvironmentSessionsListTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public EnvironmentSessionsListTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static BcSession Session(
        int id, string user, string clientType = "WebClient", TimeSpan? running = null,
        string currentObject = "", DateTimeOffset? since = null) => new(
        id, user, clientType, since ?? new DateTimeOffset(2026, 9, 20, 6, 30, 0, TimeSpan.Zero),
        "OnRun", "Sales Order", "42", "Page",
        currentObject, currentObject.Length > 0 ? 82 : null, currentObject.Length > 0 ? "CodeUnit" : string.Empty,
        running);

    private IRenderedComponent<EnvironmentSessionsList> Render(
        BcSession[] sessions, bool canCancel = true, Action<ComponentParameterCollectionBuilder<EnvironmentSessionsList>>? extra = null) =>
        _ctx.Render<EnvironmentSessionsList>(p =>
        {
            p.Add(c => c.Sessions, sessions)
                .Add(c => c.EnvironmentName, "Production")
                .Add(c => c.FetchedAt, DateTime.UtcNow)
                .Add(c => c.CanCancel, canCancel)
                .Add(c => c.TimeZone, TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"))
                .Add(c => c.TimeZoneLabel, "Copenhagen");
            extra?.Invoke(p);
        });

    [Fact]
    public void Each_session_says_who_how_they_got_in_since_when_what_it_is_running_and_for_how_long()
    {
        var cut = Render([
            Session(47, "ola@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
            Session(48, "kari@cronus.example", "ODataV4", running: TimeSpan.FromSeconds(3)),
        ]);

        cut.FindAll("thead th").Select(h => h.TextContent.Trim()).Should().Equal(
            "Long-running", "Who", "Signed in through", "Signed in since", "Running now",
            "For how long", "Actions");

        var rows = cut.FindAll("tbody tr");
        rows[0].Children[1].TextContent.Should().Be("ola@cronus.example");
        rows[0].Children[2].TextContent.Should().Be("Web client");
        rows[0].Children[3].TextContent.Should().Be("20 Sep, 08:30", "times are in the customer's zone");
        rows[0].Children[4].TextContent.Should().Be("Post Sales Documents (code unit 82)");
        rows[0].Children[5].TextContent.Should().Be("1 h 2 min");

        rows[1].Children[2].TextContent.Should().Be("Web service (OData)", "never the wire word");
        rows[1].Children[5].TextContent.Should().Be("3 sec");
    }

    /// <summary>
    /// The row somebody is looking for has to be findable at a glance, and the rule is
    /// said out loud under the table rather than left as a colour nobody can explain.
    /// </summary>
    [Fact]
    public void A_session_stuck_in_one_operation_is_marked_and_the_rule_is_on_screen()
    {
        var cut = Render([
            Session(47, "ola@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
            Session(48, "kari@cronus.example", running: TimeSpan.FromSeconds(3)),
            Session(49, "batch@cronus.example", "Background"),
        ]);

        var rows = cut.FindAll("tbody tr");
        rows[0].ClassList.Should().Contain("is-running");
        rows[0].QuerySelector(".status-pill")!.TextContent.Should().Be("Long-running");
        rows[1].ClassList.Should().NotContain("is-running");
        rows[1].QuerySelector(".status-pill").Should().BeNull();
        rows[2].QuerySelector(".status-pill").Should().BeNull("a session Business Central said nothing about is not marked");

        // The footer wraps in the markup, so the rule is read as words rather than bytes.
        var footer = string.Join(' ', cut.Find(".card__foot").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        footer.Should().Contain("marked long-running when it has spent 5 minutes or more on the same operation");
    }

    [Fact]
    public void Nobody_signed_in_is_a_plain_and_reassuring_answer()
    {
        var cut = Render([]);

        cut.FindAll("table").Should().BeEmpty();
        cut.Find(".empty-state__title").TextContent.Should().Be("Nobody is signed in to Production");
        string.Join(' ', cut.Find(".empty-state").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Should().Contain("no job queue sessions are open, so nothing here is holding a record");
    }

    [Fact]
    public void The_head_says_how_fresh_the_list_is_that_it_keeps_itself_current_and_offers_a_refresh()
    {
        var pressed = 0;
        var cut = Render([Session(47, "ola@cronus.example")], extra: p => p
            .Add(c => c.UpdateEverySeconds, 30)
            .Add(c => c.OnRefresh, () => pressed++));

        cut.Find(".env-sessions__fresh").TextContent.Should().Be("Read just now");
        cut.Find(".card__sub").TextContent.Should().Contain("Updates every 30 seconds")
            .And.Contain("Times are in Copenhagen time", "the zone is said before the times, not under them");

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Refresh")).Click());
        cut.WaitForAssertion(() => pressed.Should().Be(1));
    }

    [Fact]
    public void While_it_is_refreshing_the_button_says_so_and_cannot_be_pressed_again()
    {
        var cut = Render([Session(47, "ola@cronus.example")], extra: p => p.Add(c => c.Refreshing, true));

        var button = cut.FindAll("button").Single(b => b.ClassList.Contains("btn--loading"));
        button.TextContent.Should().Contain("Refreshing");
        button.HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// A list left to go stale is worse than no list, so when it stops it says so - and
    /// says the one thing that starts it again.
    /// </summary>
    [Fact]
    public void When_it_stops_keeping_itself_current_it_says_so_and_points_at_refresh()
    {
        var cut = Render([Session(47, "ola@cronus.example")], extra: p => p
            .Add(c => c.Stopped, true)
            .Add(c => c.StoppedAfterText, "10 minutes"));

        // The fact in the sentence, the instruction in a button beside it.
        cut.Find(".env-sessions__note").TextContent.Should().Contain("Stopped updating after 10 minutes");
        cut.Find(".env-sessions__note button").TextContent.Trim().Should().Be("Keep updating");
        cut.FindAll("tbody tr").Should().ContainSingle("the list somebody is reading out stays on screen");
        cut.Find(".card__sub").TextContent.Should().NotContain("Updates every",
            "a card that has stopped must not go on promising that it updates");
    }

    [Fact]
    public void A_failed_update_keeps_the_list_and_says_quietly_that_it_is_not_current()
    {
        var cut = Render([Session(47, "ola@cronus.example")], extra: p => p
            .Add(c => c.UpdateFailed, "Couldn't reach Business Central."));

        cut.Find(".env-sessions__note").TextContent.Should()
            .Contain("Couldn't reach Business Central.").And.Contain("last one we could read");
        cut.FindAll("tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void Ending_a_session_is_offered_per_row_and_never_as_the_pages_primary_action()
    {
        BcSession? asked = null;
        var cut = Render([Session(47, "ola@cronus.example"), Session(48, "kari@cronus.example")],
            extra: p => p.Add(c => c.OnCancel, s => asked = s));

        var buttons = cut.FindAll(".data-table__actions button");
        buttons.Should().HaveCount(2);
        buttons[0].TextContent.Trim().Should().Be("End session");
        buttons[0].ClassList.Should().Contain("btn--danger");
        cut.FindAll(".btn--primary").Should().BeEmpty();
        buttons[0].GetAttribute("aria-label").Should().Be("End the session of ola@cronus.example");

        cut.WaitForAssertion(() => cut.FindAll(".data-table__actions button")[1].Click());
        cut.WaitForAssertion(() => asked!.SessionId.Should().Be(48));
    }

    [Fact]
    public void Somebody_who_cannot_end_a_session_is_offered_no_button_at_all()
    {
        var cut = Render([Session(47, "ola@cronus.example")], canCancel: false);

        cut.FindAll("thead th").Select(h => h.TextContent.Trim()).Should().NotContain("Actions");
        cut.FindAll(".data-table__actions button").Should().BeEmpty();
    }

    [Fact]
    public void While_a_cancel_is_in_flight_no_other_row_can_be_asked_for()
    {
        var cut = Render([Session(47, "ola@cronus.example"), Session(48, "kari@cronus.example")],
            extra: p => p.Add(c => c.Busy, true));

        cut.FindAll(".data-table__actions button").Should().OnlyContain(b => b.HasAttribute("disabled"));
    }

    [Fact]
    public void A_session_business_central_did_not_name_still_reads_as_a_sentence()
    {
        var cut = Render([Session(47, string.Empty, "SomeNewClient")]);

        var row = cut.Find("tbody tr");
        row.Children[1].TextContent.Should().Be("Not named");
        row.Children[2].TextContent.Should().Be("Some new client");
        row.Children[4].TextContent.Should().Be("Sales Order (page 42)", "it falls back to where the session came in");
        row.Children[5].TextContent.Should().Be("—");
        cut.Find(".data-table__actions button").GetAttribute("aria-label")
            .Should().Be("End the session of an unnamed user");
    }
}
