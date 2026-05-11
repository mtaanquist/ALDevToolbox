using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Detects whether <c>pg_dump</c> and <c>pg_restore</c> are on PATH and at
/// a major version that can talk to <c>postgres:18</c> (the version the
/// test fixture and compose db both use). The backup tests shell out to
/// these binaries; runners without a matching pair skip instead of
/// hard-failing on the version mismatch <c>pg_dump</c> emits.
/// </summary>
internal static class PgToolAvailability
{
    /// <summary>Minimum acceptable major version. Bump in lockstep with the postgres server version we test against.</summary>
    public const int MinimumMajorVersion = 18;

    private static readonly Lazy<string?> _missingTool = new(Probe, isThreadSafe: true);

    /// <summary>
    /// Returns null when both <c>pg_dump</c> and <c>pg_restore</c> are
    /// runnable and at least <see cref="MinimumMajorVersion"/>; otherwise
    /// returns a human-readable explanation suitable for an xUnit
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
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5_000);
                if (p.ExitCode != 0) return $"{tool} exited {p.ExitCode}.";
                var match = Regex.Match(output, @"(\d+)(?:\.\d+)?");
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
                {
                    return $"{tool} version could not be parsed from '{output.Trim()}'.";
                }
                if (major < MinimumMajorVersion)
                {
                    return $"{tool} v{major} is older than the required v{MinimumMajorVersion}.";
                }
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
