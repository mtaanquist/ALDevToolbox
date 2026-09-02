namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// Pure helpers for <em>Microsoft's</em> platform-update window, mirrored from
/// <c>settings/upgrade</c> onto <c>ProjectEnvironment.BcUpdateWindow*</c>.
/// <para>
/// <b>Two different windows exist and this is the other one.</b> The toolbox's own
/// delivery slot (<see cref="UpdateWindow"/>, <c>ProjectEnvironment.UpdateWindowStart</c>
/// / <c>End</c>) is a commercial arrangement enforced by our worker. This one is when
/// Microsoft patches the environment. Neither is derived from the other; the only
/// relationship worth computing is whether they <see cref="Overlaps">overlap</see>,
/// because a delivery aimed into Microsoft's maintenance hours is the case the
/// environment-status gate then refuses.
/// </para>
/// No DB, no clock beyond the reference date passed in — unit-testable.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public static class BcUpdateWindow
{
    /// <summary>
    /// Converts the Windows time-zone id the API speaks (<c>Romance Standard Time</c>) to
    /// the IANA id the rest of the app and the Linux host use (<c>Europe/Paris</c>).
    /// Returns null when the id is blank or has no mapping.
    /// <para>
    /// Done once, at fetch time, and both forms are stored — because
    /// <c>TimeZoneInfo.FindSystemTimeZoneById</c> throws on Linux for a raw Windows id,
    /// so display code must never be handed one.
    /// </para>
    /// </summary>
    public static string? ToIana(string? windowsTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(windowsTimeZoneId)) return null;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsTimeZoneId.Trim(), out var iana) ? iana : null;
    }

    /// <summary>
    /// The zone to do display maths in: BC's own zone when the conversion worked,
    /// otherwise the project's configured zone, otherwise UTC. The fallback matters —
    /// a Windows id Microsoft adds later has no mapping here, and showing the window in
    /// the customer's own zone is far closer to right than refusing to show it at all.
    /// </summary>
    public static TimeZoneInfo ResolveDisplayZone(string? bcIanaId, string? projectIanaId) =>
        UpdateWindow.ResolveTimeZone(string.IsNullOrWhiteSpace(bcIanaId) ? projectIanaId : bcIanaId);

    /// <summary>
    /// The city half of an IANA id (<c>Europe/Copenhagen</c> → <c>Copenhagen</c>), which
    /// is what a consultant recognises. Shared so every screen names a customer's zone the
    /// same way, instead of some of them showing the raw id.
    /// </summary>
    public static string CityLabel(string ianaId)
    {
        var city = ianaId.Contains('/') ? ianaId[(ianaId.LastIndexOf('/') + 1)..] : ianaId;
        return city.Replace('_', ' ');
    }

    /// <summary>
    /// True when the toolbox's delivery slot and Microsoft's update window share any
    /// minute of the day. Both windows are daily and may wrap past midnight, and they can
    /// be expressed in different zones, so both are projected onto the same UTC day
    /// (<paramref name="referenceUtc"/>) before comparing. Returns false when either
    /// window is unset — nothing to clash with.
    /// <para>
    /// DST makes this an approximation: a window's UTC offset shifts twice a year, so an
    /// overlap computed today can be an hour out in six months. That is acceptable for a
    /// warning whose job is "look at this before you choose", and the delivery-time status
    /// gate is what actually protects the release.
    /// </para>
    /// </summary>
    public static bool Overlaps(
        TimeOnly? ourStart, TimeOnly? ourEnd, TimeZoneInfo ourZone,
        TimeOnly? bcStart, TimeOnly? bcEnd, TimeZoneInfo bcZone,
        DateTime referenceUtc)
    {
        if (!UpdateWindow.IsConfigured(ourStart, ourEnd)) return false;
        if (!UpdateWindow.IsConfigured(bcStart, bcEnd)) return false;

        var ours = ToUtcMinutes(ourStart!.Value, ourEnd!.Value, ourZone, referenceUtc);
        var theirs = ToUtcMinutes(bcStart!.Value, bcEnd!.Value, bcZone, referenceUtc);
        return ours.Any(a => theirs.Any(b => a.Start < b.End && b.Start < a.End));
    }

    /// <summary>
    /// Projects a daily wall-clock window onto minutes-past-midnight UTC, as one segment
    /// or two when it wraps. A window whose bounds are equal is treated as always-open
    /// (the whole day), matching <see cref="UpdateWindow.IsWithin"/>.
    /// </summary>
    private static List<(int Start, int End)> ToUtcMinutes(TimeOnly start, TimeOnly end, TimeZoneInfo zone, DateTime referenceUtc)
    {
        if (start == end) return [(0, 1440)];

        var startUtc = MinutesUtc(start, zone, referenceUtc);
        var endUtc = MinutesUtc(end, zone, referenceUtc);
        if (startUtc == endUtc) return [(0, 1440)];

        return startUtc < endUtc
            ? [(startUtc, endUtc)]
            : [(startUtc, 1440), (0, endUtc)];
    }

    /// <summary>One wall-clock time on the reference day, as minutes past midnight UTC.</summary>
    private static int MinutesUtc(TimeOnly time, TimeZoneInfo zone, DateTime referenceUtc)
    {
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(referenceUtc, DateTimeKind.Utc), zone).Date;
        var local = DateTime.SpecifyKind(localDate + time.ToTimeSpan(), DateTimeKind.Unspecified);

        // A spring-forward gap makes the wall time non-existent for one day a year; nudge
        // into the following hour rather than throwing inside a display path.
        for (var i = 0; i < 4 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddHours(1);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return (int)utc.TimeOfDay.TotalMinutes;
    }
}
