using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Whitelists the canonical MCP resource URL for the request host so
/// OpenIddict's <c>ValidateResources</c> handler accepts the
/// <c>resource=https://&lt;host&gt;/mcp</c> parameter that the MCP spec
/// (2025-11-25, RFC 8707) requires on every authorisation request.
///
/// <para>
/// <c>ValidateResources</c> compares <c>request.GetResources()</c> against
/// <see cref="OpenIddictServerOptions.Resources"/>, an in-memory
/// <see cref="HashSet{T}"/> populated at startup via
/// <c>o.RegisterResources(...)</c>. The public host isn't known when the
/// host builds, so this handler adds the canonical URL to that set the
/// first time any authorise request from that host reaches us, and caches
/// the result in-process so subsequent requests skip the work.
/// </para>
///
/// Token-endpoint resource validation reads the audience from the auth
/// code / refresh token rather than the request, so a hook on the
/// authorise pipeline alone is enough. Runs at
/// <see cref="OpenIddictServerHandlerType.Custom"/> ahead of the stock
/// <c>ValidateResources</c> handler (<c>ValidateScopes.Descriptor.Order +
/// 1_000</c>) via a deeply negative <c>SetOrder</c> — see
/// <c>Program.cs</c>.
/// </summary>
public sealed class McpResourceResolver : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    private static readonly ConcurrentDictionary<string, byte> KnownResources = new(StringComparer.Ordinal);
    private static readonly object ResourcesGate = new();

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpResourceResolver> _logger;

    public McpResourceResolver(
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpResourceResolver> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.Request.Scope;
        if (string.IsNullOrEmpty(scope)) return ValueTask.CompletedTask;

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
        if (!asksForMcp) return ValueTask.CompletedTask;

        var http = _httpContextAccessor.HttpContext;
        if (http is null || !http.Request.Host.HasValue) return ValueTask.CompletedTask;

        var canonical = $"{http.Request.Scheme}://{http.Request.Host.Value}/mcp";
        if (KnownResources.ContainsKey(canonical)) return ValueTask.CompletedTask;

        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri)) return ValueTask.CompletedTask;

        // OpenIddictServerOptions.Resources is a HashSet<Uri> and isn't
        // safe under concurrent writes; serialise the first-add. After the
        // cache fills the gate is contention-free.
        lock (ResourcesGate)
        {
            if (context.Options.Resources.Add(uri))
            {
                _logger.LogInformation("MCP resource: whitelisted {Resource} for OAuth resource validation.", uri.AbsoluteUri);
            }
        }
        KnownResources.TryAdd(canonical, 0);
        return ValueTask.CompletedTask;
    }
}
