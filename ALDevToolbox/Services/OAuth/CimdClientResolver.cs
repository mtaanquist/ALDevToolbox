using System.Net;
using System.Text.Json;
using ALDevToolbox.Endpoints;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Resolves a Client ID Metadata Document (CIMD) into a registered OAuth
/// application on the fly, so Claude's hosted surfaces can use
/// <c>https://claude.ai/oauth/mcp-oauth-client-metadata</c> as their
/// <c>client_id</c> instead of pre-registering via DCR.
///
/// <para>
/// Claude picks CIMD over DCR whenever our discovery metadata advertises
/// <c>client_id_metadata_document_supported: true</c> and <c>"none"</c> in
/// <c>token_endpoint_auth_methods_supported</c>. Both are set in
/// <c>Program.cs</c>. Without this resolver in place OpenIddict's standard
/// validator fails the authorisation request with
/// <c>ID2052: The specified 'client_id' is invalid</c> because no row in
/// <c>oauth_applications</c> matches the URL — see
/// <see href="https://documentation.openiddict.com/errors/ID2052"/>.
/// </para>
///
/// The resolver runs at <see cref="OpenIddictServerHandlerType.Custom"/>
/// before OpenIddict's <c>ValidateClientId</c> handler. If the <c>client_id</c>
/// is an HTTPS URL and no matching application exists yet, it fetches the
/// metadata document, validates the basic shape per the
/// <see href="https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization#client-id-metadata-documents">MCP CIMD spec</see>,
/// and creates the application via
/// <see cref="IOpenIddictApplicationManager"/> so the rest of the pipeline
/// sees a normal client. Subsequent connections from the same URL skip the
/// fetch — the row is already there.
/// </summary>
public sealed class CimdClientResolver : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private const int MaxBodyBytes = 64 * 1024;

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CimdClientResolver> _logger;

    public CimdClientResolver(
        IOpenIddictApplicationManager applications,
        IHttpClientFactory httpClientFactory,
        ILogger<CimdClientResolver> logger)
    {
        _applications = applications;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url)) return;
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return;

        // Already resolved? OpenIddict's own validator picks it up from here.
        var existing = await _applications.FindByClientIdAsync(clientId, context.CancellationToken);
        if (existing is not null) return;

        CimdMetadata? metadata;
        try
        {
            metadata = await FetchAsync(url, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CIMD fetch failed for {Url}", url);
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: $"Failed to fetch client metadata document from '{url}'.");
            return;
        }
        if (metadata is null)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: $"Client metadata document at '{url}' was empty or not valid JSON.");
            return;
        }

        // The document MUST self-identify with the same URL — otherwise
        // any document could claim any client_id.
        if (!string.Equals(metadata.ClientId, clientId, StringComparison.Ordinal))
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document's client_id does not match the requested URL.");
            return;
        }
        if (metadata.RedirectUris is null || metadata.RedirectUris.Length == 0)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document must declare at least one redirect_uri.");
            return;
        }
        // CIMD is for public clients only — the spec uses "none" as the
        // token-endpoint auth method, which is what tells OpenIddict the
        // client doesn't carry a secret.
        if (!string.Equals(metadata.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document must declare token_endpoint_auth_method=none.");
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
            DisplayName = string.IsNullOrWhiteSpace(metadata.ClientName) ? clientId : metadata.ClientName!.Trim(),
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Endpoints.Revocation,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                "scp:mcp",
                "scp:" + OpenIddictConstants.Scopes.OfflineAccess,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            },
        };
        foreach (var uri in metadata.RedirectUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                descriptor.RedirectUris.Add(parsed);
            }
        }
        if (descriptor.RedirectUris.Count == 0)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document's redirect_uris are all invalid.");
            return;
        }
        descriptor.Properties["registration_source"] = JsonSerializer.SerializeToElement("cimd");
        descriptor.Properties["registered_at"] = JsonSerializer.SerializeToElement(DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(metadata.ClientUri))
        {
            descriptor.Properties["client_uri"] = JsonSerializer.SerializeToElement(metadata.ClientUri);
        }

        try
        {
            await _applications.CreateAsync(descriptor, context.CancellationToken);
            _logger.LogInformation(
                "Resolved CIMD client {ClientId} ({DisplayName}) with {RedirectCount} redirect_uri(s).",
                clientId, descriptor.DisplayName, descriptor.RedirectUris.Count);
        }
        catch (Exception ex)
        {
            // Race: a parallel request created the same row first.
            // FindByClientIdAsync will succeed for the next handler, so we
            // log and continue rather than fail the request.
            _logger.LogInformation(ex, "CIMD client {ClientId} appeared concurrently — using existing row.", clientId);
        }
    }

    private async Task<CimdMetadata?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(CimdClientResolver));
        client.Timeout = FetchTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox-MCP/1.0");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Metadata document returned HTTP {(int)response.StatusCode}.");
        }
        // Cap the read so a hostile document can't exhaust memory.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        while (total <= MaxBodyBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxBodyBytes)
        {
            throw new InvalidOperationException($"Metadata document exceeded {MaxBodyBytes} bytes.");
        }
        return JsonSerializer.Deserialize<CimdMetadata>(
            buffer.AsSpan(0, total),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// Subset of the RFC 7591 client metadata shape that CIMD reuses. We
    /// only read the fields we actually honour; unknown fields are dropped.
    /// </summary>
    private sealed record CimdMetadata
    {
        [System.Text.Json.Serialization.JsonPropertyName("client_id")]
        public string? ClientId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("client_uri")]
        public string? ClientUri { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
        public string[]? RedirectUris { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; init; }
    }
}
