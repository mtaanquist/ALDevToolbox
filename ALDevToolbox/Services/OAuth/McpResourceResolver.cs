using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Ensures the <c>mcp</c> scope row carries the canonical MCP resource URL
/// for the request's host. The MCP spec (2025-11-25) has clients pass
/// <c>resource=https://&lt;host&gt;/mcp</c> on every authorisation request
/// per RFC 8707, and OpenIddict's stock <c>ValidateResources</c> handler
/// rejects with <c>ID2190</c> (<c>invalid_target</c>) unless that URL is in
/// the scope row's <c>Resources</c> collection.
///
/// <para>
/// We don't know the public host at startup, so the row is seeded lazily:
/// the first authorisation request from any host upserts the resource and
/// the result is cached in-process so subsequent requests skip the DB.
/// </para>
///
/// Runs as an OpenIddict event handler on
/// <see cref="ValidateAuthorizationRequestContext"/>, sequenced ahead of
/// the stock <c>ValidateResources</c> via a deliberately small
/// <see cref="OpenIddictServerHandlerDescriptor.Order"/>. Only the
/// authorize-request path needs the hook — token-endpoint resource
/// validation reads the stored values from the auth code / refresh token
/// rather than the request, so once the authorise leg succeeds the
/// downstream legs use the cached audience.
/// </summary>
public sealed class McpResourceResolver : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    private static readonly ConcurrentDictionary<string, byte> KnownResources = new(StringComparer.Ordinal);

    private readonly IOpenIddictScopeManager _scopes;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpResourceResolver> _logger;

    public McpResourceResolver(
        IOpenIddictScopeManager scopes,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpResourceResolver> logger)
    {
        _scopes = scopes;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.Request.Scope;
        if (string.IsNullOrEmpty(scope)) return;

        // Skip cheaply when the request doesn't ask for the mcp scope.
        var asksForMcp = false;
        foreach (var token in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "mcp", StringComparison.Ordinal))
            {
                asksForMcp = true;
                break;
            }
        }
        if (!asksForMcp) return;

        var http = _httpContextAccessor.HttpContext;
        if (http is null || !http.Request.Host.HasValue) return;

        var canonical = $"{http.Request.Scheme}://{http.Request.Host.Value}/mcp";
        if (KnownResources.ContainsKey(canonical)) return;

        var existing = await _scopes.FindByNameAsync("mcp", context.CancellationToken);
        var descriptor = new OpenIddictScopeDescriptor
        {
            Name = "mcp",
            DisplayName = "Model Context Protocol",
        };
        if (existing is not null)
        {
            descriptor.DisplayName = await _scopes.GetDisplayNameAsync(existing, context.CancellationToken) ?? descriptor.DisplayName;
            descriptor.Description = await _scopes.GetDescriptionAsync(existing, context.CancellationToken);
            foreach (var resource in await _scopes.GetResourcesAsync(existing, context.CancellationToken))
            {
                descriptor.Resources.Add(resource);
            }
        }
        if (descriptor.Resources.Contains(canonical))
        {
            KnownResources.TryAdd(canonical, 0);
            return;
        }
        descriptor.Resources.Add(canonical);

        try
        {
            if (existing is null)
            {
                await _scopes.CreateAsync(descriptor, context.CancellationToken);
            }
            else
            {
                await _scopes.UpdateAsync(existing, descriptor, context.CancellationToken);
            }
            _logger.LogInformation("MCP scope: registered resource {Resource}.", canonical);
        }
        catch (Exception ex)
        {
            // Concurrent first-request from another worker may have written the
            // same row already; tolerate and let the next request read it.
            _logger.LogInformation(ex, "MCP scope upsert for {Resource} raced; will retry on next request.", canonical);
            return;
        }
        KnownResources.TryAdd(canonical, 0);
    }
}
