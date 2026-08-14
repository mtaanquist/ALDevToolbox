namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Relative timestamps for build and delivery histories ("4 minutes ago",
/// "2 weeks ago"). Pulled out when the Pipelines browser moved onto the design
/// system's list archetype and would have been the third byte-identical copy.
///
/// Note there is a *second*, shorter phrasing still living privately on
/// <c>PipelineEditorDialog</c> and <c>ReleasePipelineDetail</c> ("4 min ago",
/// then "on 2026-07-01" past a week). That one is deliberately not merged in
/// here: unifying them would change visible copy on two pages as a side effect
/// of a refactor. Fold them in when those pages migrate and the wording is a
/// decision rather than an accident.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// A compact relative time for a UTC instant. Clamps future timestamps to
    /// "just now" — clock skew between the app and Postgres shouldn't render as
    /// a build that finished in the future.
    /// </summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => Plural((int)span.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural((int)span.TotalHours, "hour"),
            { TotalDays: < 7 } => Plural((int)span.TotalDays, "day"),
            { TotalDays: < 30 } => Plural((int)(span.TotalDays / 7), "week"),
            { TotalDays: < 365 } => Plural((int)(span.TotalDays / 30), "month"),
            _ => Plural((int)(span.TotalDays / 365), "year"),
        };

        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }
}
