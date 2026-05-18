using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Mounts the Model Context Protocol HTTP transport at <c>/mcp</c> and
/// guards it with the bearer-token <see cref="PatAuthenticationHandler"/>.
///
/// /mcp is intentionally <strong>not</strong> tagged for /healthz or
/// /readyz — these probes must keep returning quickly regardless of MCP
/// traffic. It is also outside the maintenance-mode allow-list, so MCP
/// requests pause during restores like every other org-scoped call.
/// </summary>
internal static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
        if (!options.Enabled)
        {
            // Kill-switch: when Mcp:Enabled=false we don't mount the routes
            // at all. /mcp returns 404 in that case, which is the same shape
            // the SDK would produce for a path the agent has mis-typed.
            return app;
        }

        app.MapMcp("/mcp").RequireAuthorization(PatAuthenticationHandler.AuthenticationScheme);
        return app;
    }
}
