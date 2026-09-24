using System.Globalization;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// The organisation's display time zone (issue #942): every time the app shows
/// is converted from UTC into it, with the UTC instant kept for the hover title.
/// Rendered through <c>Components/Shared/Timestamp.razor</c>; pages should not
/// format a <see cref="DateTime"/> themselves (the date-formatting baseline test
/// counts the ones that still do).
///
/// <para>Scoped, so in Blazor Server one instance lives per circuit and the zone
/// is read once per circuit, then held in a field. No organisation in scope
/// (pre-login pages, background workers) means UTC.</para>
///
/// <para>The read goes through <see cref="IDbContextFactory{TContext}"/>, not the
/// circuit's <see cref="AppDbContext"/>, for the same reason as
/// <see cref="AuditService"/> (#741): a timestamp renders in the middle of a
/// page whose own query may still be in flight on the shared context, and a
/// <c>DbContext</c> allows one operation at a time. The factory is scoped, so
/// the context it hands out carries the circuit's own
/// <see cref="IOrganizationContext"/> and the tenant query filter applies as
/// usual.</para>
/// </summary>
public sealed class DisplayTimeZone
{
    /// <summary>The label format of the hover title: the exact instant, in UTC.</summary>
    public const string UtcTooltipFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<DisplayTimeZone> _logger;

    private TimeZoneInfo? _zone;
    private Task<TimeZoneInfo>? _loading;

    public DisplayTimeZone(
        IDbContextFactory<AppDbContext> dbFactory,
        IOrganizationContext orgContext,
        ILogger<DisplayTimeZone> logger)
    {
        _dbFactory = dbFactory;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// The zone times are shown in. Resolves synchronously on first use if
    /// <see cref="EnsureLoadedAsync"/> has not run yet; components call that
    /// first so the render path never blocks on the database.
    /// </summary>
    public TimeZoneInfo Zone => _zone ??= Resolve(LoadZoneId());

    /// <summary>
    /// Loads the zone once, shared by every caller on the circuit: a page full of
    /// timestamps issues one read, not one per timestamp.
    /// </summary>
    public Task<TimeZoneInfo> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_zone is not null) return Task.FromResult(_zone);
        return _loading ??= LoadAsync(ct);
    }

    /// <summary>
    /// Forgets the resolved zone, so the next read picks up a change just saved on
    /// this circuit. Other circuits see the change when their page next loads.
    /// </summary>
    public void Invalidate()
    {
        _zone = null;
        _loading = null;
    }

    /// <summary>
    /// <paramref name="utc"/> in the display zone. <see cref="DateTimeKind.Unspecified"/>
    /// is taken as UTC, because that is what EF hands back for a <c>timestamp</c>
    /// column; a <see cref="DateTimeKind.Local"/> value is converted to UTC first.
    /// </summary>
    public DateTime ToDisplay(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Zone);

    /// <summary><paramref name="utc"/> in the display zone, formatted with the invariant culture.</summary>
    public string Format(DateTime utc, string format) =>
        ToDisplay(utc).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a time the user typed into a <c>datetime-local</c> input
    /// ("2026-03-29T02:30") as a wall-clock time in the display zone and returns
    /// the UTC instant, or null when the text is blank or not a time. The input
    /// sits beside times shown in this zone, so reading it as UTC would make the
    /// page contradict itself.
    ///
    /// <para>A wall-clock time the spring change skips (02:30 in Copenhagen on
    /// the last Sunday of March) does not exist; it is read with the zone's
    /// standard offset, which lands on the moment the clocks jumped past it.
    /// A time the autumn change repeats is read as standard time.</para>
    /// </summary>
    public DateTime? ParseInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
        // Text carrying its own offset or "Z" names an instant already.
        if (parsed.Kind != DateTimeKind.Unspecified) return AsUtc(parsed);
        var zone = Zone;
        return zone.IsInvalidTime(parsed)
            ? DateTime.SpecifyKind(parsed - zone.BaseUtcOffset, DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(parsed, zone);
    }

    /// <summary>The hover title for a time: the exact instant in UTC, labelled so.</summary>
    public string UtcTooltip(DateTime utc) =>
        AsUtc(utc).ToString(UtcTooltipFormat, CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// Normalises a stored or rendered <see cref="DateTime"/> to
    /// <see cref="DateTimeKind.Utc"/>. Unspecified is read as UTC, not as local.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// The zones an admin can pick from: IANA ids only (the ones with a <c>/</c>),
    /// without the <c>Etc/</c> aliases, sorted by id. UTC is not in the list; the
    /// picker offers it separately as the default.
    /// </summary>
    public static IReadOnlyList<TimeZoneInfo> SelectableZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Where(z => z.Id.Contains('/') && !z.Id.StartsWith("Etc/", StringComparison.Ordinal))
            .OrderBy(z => z.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// "Europe/Copenhagen (UTC+02:00)": the id and the zone's offset right now,
    /// so the label is honest in summer and in winter.
    /// </summary>
    public static string Label(TimeZoneInfo zone, DateTime utcNow)
    {
        var offset = zone.GetUtcOffset(AsUtc(utcNow));
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{zone.Id} (UTC{sign}{offset.Duration():hh\\:mm})";
    }

    /// <summary>The display zone's offset from UTC at the instant <paramref name="utc"/>.</summary>
    public TimeSpan OffsetAt(DateTime utc) => Zone.GetUtcOffset(AsUtc(utc));

    /// <summary>
    /// A time of day stored in UTC (issue #970: the backup schedule), shown in the
    /// display zone with the offset <paramref name="offset"/>, normally
    /// <see cref="OffsetAt"/> today. The stored value stays UTC and does not move
    /// with daylight saving, so what this returns shifts by an hour when the
    /// clocks change; the page says so beside it.
    /// </summary>
    public static TimeOnly TimeOfDayToDisplay(TimeOnly utcTimeOfDay, TimeSpan offset) =>
        utcTimeOfDay.Add(offset);

    /// <summary>
    /// The inverse of <see cref="TimeOfDayToDisplay"/>: a time of day typed in the
    /// display zone, back to the UTC time of day to store. Using the same offset
    /// both ways makes an unchanged value round-trip exactly.
    /// </summary>
    public static TimeOnly TimeOfDayToUtc(TimeOnly displayTimeOfDay, TimeSpan offset) =>
        displayTimeOfDay.Add(-offset);

    /// <summary>"04:00": a time of day, formatted with the invariant culture.</summary>
    public static string FormatTimeOfDay(TimeOnly timeOfDay) =>
        timeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);

    private async Task<TimeZoneInfo> LoadAsync(CancellationToken ct)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        string? zoneId = null;
        if (orgId is not null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                zoneId = await db.OrganizationSettings.AsNoTracking()
                    .Where(s => s.OrganizationId == orgId)
                    .Select(s => s.DisplayTimeZoneId)
                    .FirstOrDefaultAsync(ct);
            }
            catch
            {
                // Don't cache a failed load: the next caller tries again.
                _loading = null;
                throw;
            }
        }
        return _zone = Resolve(zoneId);
    }

    private string? LoadZoneId()
    {
        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null) return null;
        using var db = _dbFactory.CreateDbContext();
        return db.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => s.DisplayTimeZoneId)
            .FirstOrDefault();
    }

    /// <summary>
    /// A stored id this host cannot resolve (a tz database without it) falls back
    /// to UTC with a warning rather than breaking every page that shows a time.
    /// </summary>
    private TimeZoneInfo Resolve(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId)) return TimeZoneInfo.Utc;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)) return zone;
        _logger.LogWarning("Display time zone {ZoneId} could not be resolved on this host; showing times in UTC.", zoneId);
        return TimeZoneInfo.Utc;
    }
}
