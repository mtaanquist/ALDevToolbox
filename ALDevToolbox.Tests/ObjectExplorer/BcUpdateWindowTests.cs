using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Microsoft's platform-update window: parsing it, converting the Windows zone it comes
/// in, and deciding whether it collides with the toolbox's own delivery window.
/// <para>
/// The conversion is the part that fails quietly in production. Business Central speaks
/// Windows time-zone ids; the host runs Linux, where handing one to
/// <c>FindSystemTimeZoneById</c> throws. So the id is converted once at fetch time and
/// both forms stored — and the case where the conversion has no answer has to degrade to
/// something, which is what most of these tests pin.
/// </para>
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcUpdateWindowTests
{
    private static readonly DateTime Reference = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // ── Parsing settings/upgrade ──────────────────────────────────────────────

    [Fact]
    public void ParseUpdateSettings_reads_the_wall_time_trio()
    {
        const string json = """
        {
          "preferredStartTime": "02:00",
          "preferredEndTime": "06:00",
          "timeZoneId": "Romance Standard Time",
          "preferredStartTimeUtc": "2026-06-16T00:00:00Z",
          "preferredEndTimeUtc": "2026-06-16T04:00:00Z"
        }
        """;

        var settings = BcAdminClient.ParseUpdateSettings(json)!;

        settings.StartTime.Should().Be(new TimeOnly(2, 0));
        settings.EndTime.Should().Be(new TimeOnly(6, 0));
        settings.WindowsTimeZoneId.Should().Be("Romance Standard Time",
            "the Windows id is what the API takes back on a write, so it is stored verbatim");
        settings.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void ParseUpdateSettings_treats_a_null_body_as_no_window()
    {
        // The API documents a literal "null" for an environment with no window.
        BcAdminClient.ParseUpdateSettings("null").Should().BeNull();
        BcAdminClient.ParseUpdateSettings("").Should().BeNull();
        BcAdminClient.ParseUpdateSettings("{}").Should().BeNull();
    }

    [Fact]
    public void ParseUpdateSettings_tolerates_a_minimal_payload()
    {
        // Only the zone came back - no window, but the environment does have a zone.
        var settings = BcAdminClient.ParseUpdateSettings("""{ "timeZoneId": "UTC" }""")!;

        settings.WindowsTimeZoneId.Should().Be("UTC");
        settings.IsConfigured.Should().BeFalse("a window needs both bounds");
    }

    [Fact]
    public void ParseUpdateSettings_refuses_a_body_that_is_not_json()
    {
        var act = () => BcAdminClient.ParseUpdateSettings("<html>gateway timeout</html>");

        act.Should().Throw<BcApiException>();
    }

    // ── Refusals on a write ───────────────────────────────────────────────────

    [Fact]
    public void A_window_clashing_with_a_scheduled_update_says_what_to_change()
    {
        const string body = """{"code":"ScheduledUpgradeConstraintViolation","message":"Localized prose."}""";

        var message = BcAdminClient.DescribeUpdateSettingsFailure(System.Net.HttpStatusCode.BadRequest, body);

        // Keyed on the code, because the message beside it is Microsoft's prose.
        message.Should().Contain("clashes with the update already scheduled")
            .And.Contain("admin center", "the fix is somewhere the consultant has to go");
        message.Should().NotContain("ScheduledUpgradeConstraintViolation",
            "the wire code is not what a consultant reads");
    }

    [Theory]
    [InlineData("invalidRange", "too small")]
    [InlineData("environmentNotFound", "no longer has this environment")]
    public void Other_documented_refusals_get_their_own_wording(string code, string expected)
    {
        var body = $$"""{"code":"{{code}}","message":"x"}""";

        BcAdminClient.DescribeUpdateSettingsFailure(System.Net.HttpStatusCode.BadRequest, body)
            .Should().Contain(expected);
    }

    [Fact]
    public void An_unrecognised_refusal_still_says_something_useful()
    {
        var message = BcAdminClient.DescribeUpdateSettingsFailure(
            System.Net.HttpStatusCode.InternalServerError, """{"code":"somethingNew","message":"Boom."}""");

        message.Should().Contain("500").And.Contain("Boom.");
    }

    // ── Windows to IANA ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Romance Standard Time", "Europe/Paris")]
    [InlineData("UTC", "Etc/UTC")]
    public void ToIana_converts_the_windows_id(string windows, string expected)
    {
        BcUpdateWindow.ToIana(windows).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not A Real Windows Zone")]
    public void ToIana_returns_null_when_there_is_no_mapping(string? windows)
    {
        BcUpdateWindow.ToIana(windows).Should().BeNull();
    }

    [Fact]
    public void An_unmappable_zone_falls_back_to_the_projects_own_zone()
    {
        // The risk this guards: a Windows id with no IANA mapping must not reach
        // FindSystemTimeZoneById, which throws on Linux. Display falls back instead.
        var zone = BcUpdateWindow.ResolveDisplayZone(bcIanaId: null, projectIanaId: "Europe/Copenhagen");

        zone.Should().NotBeNull();
        zone.Id.Should().Be("Europe/Copenhagen");
    }

    [Fact]
    public void With_no_zone_anywhere_the_fallback_is_utc_rather_than_a_throw()
    {
        BcUpdateWindow.ResolveDisplayZone(bcIanaId: null, projectIanaId: null).Should().Be(TimeZoneInfo.Utc);
        BcUpdateWindow.ResolveDisplayZone(bcIanaId: null, projectIanaId: "Nonsense/Zone").Should().Be(TimeZoneInfo.Utc);
    }

    /// <summary>
    /// What the delivery scheduler's own resolver accepts, which is what the time-zone
    /// field on the connection form is allowed to promise. It turns out to take both
    /// forms on this runtime — .NET maps Windows ids on Linux through ICU — so the
    /// caption says both rather than rejecting a paste from the admin center.
    /// </summary>
    [Fact]
    public void The_projects_timezone_field_accepts_both_iana_and_windows_ids()
    {
        UpdateWindow.ResolveTimeZone("Europe/Copenhagen").Id.Should().Be("Europe/Copenhagen");
        UpdateWindow.ResolveTimeZone("Romance Standard Time").BaseUtcOffset
            .Should().Be(TimeSpan.FromHours(1), "a Windows id resolves too, to the same real zone");
        // Anything else still degrades to UTC instead of throwing in a scheduling path.
        UpdateWindow.ResolveTimeZone("Nonsense/Zone").Should().Be(TimeZoneInfo.Utc);
    }

    // ── Overlap ───────────────────────────────────────────────────────────────

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void A_delivery_slot_inside_microsofts_window_overlaps()
    {
        // Ours 02:00-03:00, Microsoft's 02:00-06:00, same zone.
        BcUpdateWindow.Overlaps(
            new TimeOnly(2, 0), new TimeOnly(3, 0), Utc,
            new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, Reference).Should().BeTrue();
    }

    [Fact]
    public void A_delivery_slot_clear_of_microsofts_window_does_not_overlap()
    {
        // The case the warning must stay silent for: 20:00-21:00 against 02:00-06:00.
        BcUpdateWindow.Overlaps(
            new TimeOnly(20, 0), new TimeOnly(21, 0), Utc,
            new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, Reference).Should().BeFalse();
    }

    [Fact]
    public void A_window_that_wraps_past_midnight_still_finds_the_overlap()
    {
        // Ours 22:00-06:00 (the shape our own windows actually take) against
        // Microsoft's 02:00-06:00: they share 02:00-06:00.
        BcUpdateWindow.Overlaps(
            new TimeOnly(22, 0), new TimeOnly(6, 0), Utc,
            new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, Reference).Should().BeTrue();
    }

    [Fact]
    public void A_wrapping_delivery_window_that_clears_microsofts_does_not_overlap()
    {
        // Ours 22:00-01:00, Microsoft's 02:00-06:00 - adjacent but never together.
        BcUpdateWindow.Overlaps(
            new TimeOnly(22, 0), new TimeOnly(1, 0), Utc,
            new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, Reference).Should().BeFalse();
    }

    [Fact]
    public void Both_windows_wrapping_is_handled()
    {
        BcUpdateWindow.Overlaps(
            new TimeOnly(23, 0), new TimeOnly(5, 0), Utc,
            new TimeOnly(22, 0), new TimeOnly(2, 0), Utc, Reference).Should().BeTrue();
    }

    [Fact]
    public void Different_zones_are_compared_on_the_same_clock()
    {
        // Ours 03:00-04:00 in Copenhagen (UTC+2 in June) is 01:00-02:00 UTC, which
        // collides with a Microsoft window of 01:00-05:00 UTC even though the wall-clock
        // numbers never suggest it.
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

        BcUpdateWindow.Overlaps(
            new TimeOnly(3, 0), new TimeOnly(4, 0), copenhagen,
            new TimeOnly(1, 0), new TimeOnly(5, 0), Utc, Reference).Should().BeTrue();
    }

    [Fact]
    public void An_unset_window_on_either_side_never_warns()
    {
        BcUpdateWindow.Overlaps(null, null, Utc, new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, Reference)
            .Should().BeFalse("no delivery window means nothing is aimed anywhere in particular");
        BcUpdateWindow.Overlaps(new TimeOnly(2, 0), new TimeOnly(6, 0), Utc, null, null, Utc, Reference)
            .Should().BeFalse("an environment with no Microsoft window has nothing to clash with");
    }
}
