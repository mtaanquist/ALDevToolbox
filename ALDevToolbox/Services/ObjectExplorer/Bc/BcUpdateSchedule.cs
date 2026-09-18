namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Reading of the Admin Center's update schedule that both the fleet page and the date
/// writes have to agree on.
/// <para>
/// <c>scheduleDetails.latestSelectableDateTime</c> is an <em>exclusive</em> upper bound:
/// <c>2027-03-01T00:00:00Z</c> means "before 1 March", which is why the admin center's own
/// picker stops at 28 February. Sending that value straight back as
/// <c>selectedDateTime</c> asks for a moment Business Central will not take, and it
/// answers by leaving the date where it was (issue #804). So a bound that lands on
/// midnight UTC is a day boundary and the last date we may actually ask for is the
/// previous day; a bound that carries a time of day is already a real moment inside the
/// allowed range and is used as it stands. See <c>.design/environment-updates.md</c>.
/// </para>
/// </summary>
public static class BcUpdateSchedule
{
    /// <summary>
    /// The last moment we may send as <c>selectedDateTime</c> for an update whose
    /// exclusive bound is <paramref name="latestSelectable"/>. Null in, null out — an
    /// update Business Central gave no bound cannot be moved to one.
    /// </summary>
    public static DateTimeOffset? EffectiveLatest(DateTimeOffset? latestSelectable)
    {
        if (latestSelectable is not { } bound) return null;
        var utc = bound.ToUniversalTime();
        return utc.TimeOfDay == TimeSpan.Zero
            ? new DateTimeOffset(utc.Date.AddDays(-1), TimeSpan.Zero)
            : utc;
    }

    /// <summary>
    /// The same reading for the mirrored <c>bc_next_update_latest_date</c> column, which
    /// holds the bound as a UTC <see cref="DateTime"/>. This is the date a page shows as
    /// "latest Microsoft allows", so it matches the admin center's picker.
    /// </summary>
    public static DateTime? EffectiveLatestUtc(DateTime? latestSelectable)
    {
        if (latestSelectable is not { } bound) return null;
        var utc = bound.Kind == DateTimeKind.Utc ? bound : DateTime.SpecifyKind(bound, DateTimeKind.Utc);
        return utc.TimeOfDay == TimeSpan.Zero ? utc.Date.AddDays(-1) : utc;
    }
}
