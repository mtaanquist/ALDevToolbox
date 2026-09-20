using System.Globalization;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// How database sizes and a tenant's storage use read on screen. The allowance belongs to
/// the customer's tenant and is shared by all its environments, and Business Central lets a
/// tenant go over it, so "use" can be more than 100%. See <c>.design/environment-updates.md</c>.
/// </summary>
public static class StorageDisplay
{
    /// <summary>From here a tenant is worth a look before it becomes a conversation with the customer.</summary>
    public const double WarnFrom = 0.8;

    /// <summary>"12.4 GB", or "830 MB" under a gigabyte. Business Central reports kilobytes.</summary>
    public static string Size(long kilobytes)
    {
        var gb = kilobytes / 1024d / 1024d;
        return gb >= 1
            ? gb.ToString(gb >= 100 ? "N0" : "N1", CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, Math.Round(kilobytes / 1024d)).ToString("N0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>"", "warn" or "danger" - the bar's tone for a fraction of the allowance used.</summary>
    public static string Tone(double use) => use >= 1 ? "danger" : use >= WarnFrom ? "warn" : string.Empty;

    /// <summary>"Customer at 82% of 80 GB", and says so in words when it is over.</summary>
    public static string Summary(double use, long quotaKilobytes)
    {
        var percent = Math.Round(use * 100).ToString("N0", CultureInfo.InvariantCulture);
        var line = $"Customer at {percent}% of {Size(quotaKilobytes)}";
        return use >= 1 ? line + " - over its allowance" : line;
    }
}
