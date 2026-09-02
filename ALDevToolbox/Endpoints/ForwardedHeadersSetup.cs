using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Parses the <c>TRUSTED_PROXIES</c> environment variable into the
/// <see cref="ForwardedHeadersOptions"/> allow-list.
/// <para>
/// X-Forwarded-For is only honoured when the immediate peer is a trusted
/// proxy. Without that fence any client could set the header and choose its
/// own partition key for the per-IP login and DCR rate limiters, and forge
/// the <c>login_attempts.ip</c> audit value (issue #672). When the variable
/// is unset we leave the framework defaults (loopback only) in place rather
/// than clearing them, so a directly-exposed deployment ignores the header.
/// </para>
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string EnvVarName = "TRUSTED_PROXIES";

    /// <summary>
    /// Result of parsing a <c>TRUSTED_PROXIES</c> value: the single addresses
    /// and CIDR networks that were understood, plus any entries that were not.
    /// </summary>
    public sealed record TrustedProxies(
        IReadOnlyList<IPAddress> Proxies,
        IReadOnlyList<IPNetwork> Networks,
        IReadOnlyList<string> Invalid)
    {
        public bool IsEmpty => Proxies.Count == 0 && Networks.Count == 0;
    }

    /// <summary>
    /// Parses a comma- (or whitespace-) separated list of IP addresses and
    /// CIDR ranges. Blank input yields an empty result; unparseable entries
    /// are collected in <see cref="TrustedProxies.Invalid"/> rather than
    /// throwing, so one typo can't stop the app from booting.
    /// </summary>
    public static TrustedProxies Parse(string? raw)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        var invalid = new List<string>();

        foreach (var entry in (raw ?? string.Empty).Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network))
                {
                    networks.Add(network);
                }
                else
                {
                    invalid.Add(entry);
                }
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                invalid.Add(entry);
            }
        }

        return new TrustedProxies(proxies, networks, invalid);
    }

    /// <summary>Reads and parses the <c>TRUSTED_PROXIES</c> environment variable.</summary>
    public static TrustedProxies FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// Adds the trusted entries to the options. The framework's loopback
    /// defaults are kept, so a proxy on the same host keeps working.
    /// </summary>
    public static void Apply(ForwardedHeadersOptions options, TrustedProxies trusted)
    {
        foreach (var proxy in trusted.Proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in trusted.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }
    }

    /// <summary>Startup log line describing what will be trusted.</summary>
    public static void Log(ILogger logger, TrustedProxies trusted)
    {
        if (trusted.Invalid.Count > 0)
        {
            logger.LogWarning(
                "Ignoring unparseable {EnvVar} entries: {Entries}",
                EnvVarName, string.Join(", ", trusted.Invalid));
        }

        if (trusted.IsEmpty)
        {
            logger.LogInformation(
                "No {EnvVar} configured — X-Forwarded-* headers are only honoured from loopback; the connection address is used for rate limiting and audit.",
                EnvVarName);
        }
        else
        {
            logger.LogInformation(
                "Trusting X-Forwarded-* headers from proxies {Proxies} and networks {Networks} (loopback also trusted).",
                string.Join(", ", trusted.Proxies.Select(p => p.ToString())),
                string.Join(", ", trusted.Networks.Select(n => $"{n.BaseAddress}/{n.PrefixLength}")));
        }
    }
}
