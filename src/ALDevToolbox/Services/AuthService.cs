using System.Security.Cryptography;
using System.Text;

namespace ALDevToolbox.Services;

/// <summary>
/// Reads the shared admin password from <c>ADMIN_PASSWORD</c> (env) or
/// <c>ADMIN_PASSWORD_FILE</c> (path to a file containing the password) and
/// verifies submitted candidates against it in constant time.
///
/// Auth model and rationale: see <c>.design/auth-and-audit.md</c>. There are
/// no user accounts — anyone who knows the password is "the admin".
/// </summary>
public sealed class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly byte[]? _passwordHash;

    public AuthService(ILogger<AuthService> logger)
    {
        _logger = logger;

        var password = ResolvePassword();
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning(
                "Admin password is not configured. Set ADMIN_PASSWORD or ADMIN_PASSWORD_FILE to enable the admin section.");
            _passwordHash = null;
            return;
        }

        _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    /// <summary>True when an admin password is configured and login is possible.</summary>
    public bool IsConfigured => _passwordHash is not null;

    /// <summary>
    /// Constant-time comparison of <paramref name="candidate"/> against the
    /// configured password. Returns false if the password is unconfigured or
    /// the candidate is empty.
    /// </summary>
    public bool Verify(string? candidate)
    {
        if (_passwordHash is null || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(_passwordHash, candidateHash);
    }

    private static string? ResolvePassword()
    {
        var direct = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var path = Environment.GetEnvironmentVariable("ADMIN_PASSWORD_FILE");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        return null;
    }
}
