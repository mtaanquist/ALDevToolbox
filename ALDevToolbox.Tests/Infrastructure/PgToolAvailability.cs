using System.Diagnostics;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Detects whether <c>pg_dump</c> and <c>pg_restore</c> are on PATH. The
/// backup tests shell out to those binaries via <c>BackupService</c>;
/// runners without them (rare; CI installs <c>postgresql-client</c>) skip
/// instead of hard-failing.
/// </summary>
internal static class PgToolAvailability
{
    private static readonly Lazy<string?> _missingTool = new(Probe, isThreadSafe: true);

    /// <summary>
    /// Returns null when both <c>pg_dump</c> and <c>pg_restore</c> are runnable;
    /// otherwise returns a human-readable explanation suitable for an xUnit
    /// <c>Skip</c> reason.
    /// </summary>
    public static string? MissingToolReason => _missingTool.Value;

    private static string? Probe()
    {
        foreach (var tool in new[] { "pg_dump", "pg_restore" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return $"{tool} could not be started.";
                p.WaitForExit(5_000);
                if (p.ExitCode != 0) return $"{tool} exited {p.ExitCode}.";
            }
            catch (Exception ex)
            {
                return $"{tool} unavailable: {ex.Message}";
            }
        }
        return null;
    }
}

/// <summary>
/// <see cref="FactAttribute"/> that skips when <c>pg_dump</c>/<c>pg_restore</c>
/// can't be invoked. Test code reads cleanly: tag the test with
/// <c>[PgToolFact]</c> instead of repeating a runtime check.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class PgToolFactAttribute : FactAttribute
{
    public PgToolFactAttribute()
    {
        var reason = PgToolAvailability.MissingToolReason;
        if (reason is not null)
        {
            Skip = "pg_dump/pg_restore not on PATH: " + reason;
        }
    }
}
