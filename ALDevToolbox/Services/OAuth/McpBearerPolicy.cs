namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Name of the authorisation policy that gates the MCP endpoint. The policy
/// accepts either a <c>aldt_pat_…</c> Personal Access Token or an OAuth
/// access token issued by OpenIddict — both schemes mount the same downstream
/// claim set, so MCP tools never see the difference.
/// </summary>
public static class McpBearerPolicy
{
    public const string Name = "McpBearer";
}
