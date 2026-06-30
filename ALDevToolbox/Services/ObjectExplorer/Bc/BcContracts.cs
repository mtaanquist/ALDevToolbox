namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>A BC environment as returned by the Admin Center API.</summary>
public sealed record BcEnvironment(string Name, string Type);

/// <summary>A company inside a BC environment, from the automation API.</summary>
public sealed record BcCompany(Guid Id, string Name);

/// <summary>
/// A created <c>extensionUpload</c> in the automation API — its <c>systemId</c> keys
/// the subsequent set-content, upload-action, and status calls.
/// </summary>
public sealed record BcExtensionUpload(string SystemId);

/// <summary>
/// One row of the automation API's <c>extensionDeploymentStatus</c> — the install
/// progress of an app the run uploaded. <see cref="Status"/> is BC's deployment
/// status string (<c>InProgress</c> / <c>Completed</c> / <c>Failed</c> /
/// <c>Unknown</c>); <see cref="AppVersion"/> disambiguates when an app appears more
/// than once across runs.
/// </summary>
public sealed record BcDeploymentStatus(string Name, string AppVersion, string Status);

/// <summary>
/// Classification of a "Test connection" outcome, so the UI can render the right
/// message — especially the GDAP-missing case, which the Admin Center API reports
/// as a 401/403 and which the maintainer fixes by granting GDAP for the customer.
/// </summary>
public enum BcConnectionResult
{
    /// <summary>Token acquired and environments listed.</summary>
    Success,

    /// <summary>The credentials themselves were rejected (bad tenant/client/secret, or the key ring can't decrypt the stored secret).</summary>
    AuthFailed,

    /// <summary>The token was fine but the Admin Center API refused the environments call — GDAP isn't set up (or is insufficient) for this customer.</summary>
    GdapMissing,

    /// <summary>Any other failure (network, unexpected status, malformed response).</summary>
    Error,
}

/// <summary>
/// The outcome of a "Test connection" / "Refresh environments" run: the
/// classification, the number of environments fetched on success, and a
/// user-facing message. Never carries the secret.
/// </summary>
public sealed record BcConnectionTestResult(BcConnectionResult Result, int EnvironmentCount, string Message)
{
    public bool IsSuccess => Result == BcConnectionResult.Success;
}

/// <summary>
/// Raised by the BC HTTP clients when the API returns a non-success status, so the
/// orchestrating service can classify it (e.g. 401/403 on the admin call → GDAP
/// missing). Carries the status code and a short, secret-free detail.
/// </summary>
public sealed class BcApiException : Exception
{
    public System.Net.HttpStatusCode? StatusCode { get; }

    public BcApiException(System.Net.HttpStatusCode? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
