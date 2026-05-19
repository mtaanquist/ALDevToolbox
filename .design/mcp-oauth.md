# MCP OAuth — design

## Why

Claude.ai's directory and custom-connector flow does **not** accept a
user-pasted bearer token. From [Anthropic's connector docs][anthropic-auth]:

> User-pasted bearer tokens (`static_bearer`) are not yet supported.

The two out-of-the-box methods Claude does support are **DCR** (Dynamic
Client Registration, RFC 7591) and **CIMD** (Client ID Metadata Document).
Anything else (`oauth_anthropic_creds`, `custom_connection`) requires
emailing `mcp-review@anthropic.com` and an Anthropic-side review.

Today AL Dev Toolbox gates `/mcp` with a single bearer flavour: the
PAT (`aldt_pat_…`) handler in
`Services/Account/PatAuthenticationHandler.cs`. That works for Claude
Desktop, Cursor, Copilot agent mode, and Claude Code, all of which let you
paste the token into a config file. It does not work for Claude on the web
or on mobile. To make those clients addable as a Claude custom connector,
we ship an OAuth 2.1 authorization server alongside the PAT path.

PAT stays. OAuth is added. The MCP endpoint accepts either.

## Auth modes

| Mode  | What Claude does                                              | When                                                                 |
|-------|---------------------------------------------------------------|----------------------------------------------------------------------|
| PAT   | Reads `Authorization: Bearer aldt_pat_…` from its config file | Desktop apps, CLI tools, scripts                                     |
| OAuth | DCR-registers itself as a public client, runs the auth-code   | claude.ai, Claude mobile, Cowork — the hosted Claude surfaces        |
| OAuth | Identifies itself with a CIMD URL                             | Claude prefers this when the AS metadata advertises CIMD support     |

Claude picks CIMD over DCR only when the AS metadata advertises **both**
`client_id_metadata_document_supported: true` **and** `"none"` in
`token_endpoint_auth_methods_supported`. Otherwise it falls back to DCR.
ALDevToolbox advertises both, so high-traffic CIMD clients avoid the per-
connection DCR registration.

`client_credentials` is **not** supported. The MCP authorization spec
forbids pure machine-to-machine grants — every connection needs user
consent.

## Architecture

```
 Claude (browser / mobile)               AL Dev Toolbox
 ──────────────────────────              ──────────────
   GET /mcp ─────────────────────────────► McpEndpoints
                                           │ McpBearerPolicy → 401
   ← 401 WWW-Authenticate: Bearer            (PatAuthenticationHandler.HandleChallengeAsync)
       resource_metadata="…/.well-known/oauth-protected-resource"

   GET /.well-known/oauth-protected-resource ──► OAuthEndpoints (hand-rolled)
   ← { resource, authorization_servers, scopes_supported, … }

   GET /.well-known/oauth-authorization-server ──► OpenIddict + customisation
   ← { issuer, registration_endpoint, code_challenge_methods_supported:[S256],
       client_id_metadata_document_supported: true, … }

   POST /oauth/register (DCR) ──► OAuthEndpoints.MapDynamicClientRegistration
   ← 201 { client_id, redirect_uris, token_endpoint_auth_method: "none", … }

   Browser-redirect GET /oauth/authorize?… ──► OAuthConsent.razor
                                              [Authorize] forces cookie sign-in
                                              ↓
                                              Consent form
   POST /oauth/authorize ──► OAuthEndpoints.MapAuthorizeComplete
                              ↓ stamps oauth_consents row
                              ↓ SignIn(OpenIddictServerAspNetCoreScheme, principal)
   ← 302 redirect_uri?code=…

   POST /oauth/token ──► OpenIddict default handler
   ← { access_token, refresh_token, expires_in, token_type: "Bearer" }

   GET /mcp Authorization: Bearer <access_token> ──► OpenIddict validation
                                                     ↓ claims transformer
                                                     ↓ McpBearerPolicy OK
                                                     MCP tool runs as user
```

## Persistence

Five tables, all snake_case:

| Table                | Owner       | Notes                                                                 |
|----------------------|-------------|-----------------------------------------------------------------------|
| `oauth_applications` | OpenIddict  | DCR-registered & CIMD-resolved clients. Org attribution in `properties`. |
| `oauth_authorizations` | OpenIddict | Short-lived grant tickets.                                            |
| `oauth_scopes`       | OpenIddict  | Registered scope catalogue.                                           |
| `oauth_tokens`       | OpenIddict  | Access + refresh tokens. Reference + payload columns are DP-wrapped.  |
| `oauth_consents`     | ALDevToolbox | One row per (user, client, org). Drives the consent auto-skip + revoke. |

The four OpenIddict tables sit **outside** the multi-tenant query filter
because pre-auth flows (`/oauth/token`, `/oauth/register`) must read them
before any `IOrganizationContext` is mounted. Org attribution lives in
OpenIddict's free-form `properties` JSON column, stamped during the
consent step. `oauth_consents` is inside the filter — it's a normal
per-org table.

## Key ring

OpenIddict signs and encrypts both the data-protection-wrapped tokens and
the discovery `kid`s with the existing ASP.NET Core Data Protection key
ring on the `app-keys` volume. Losing `app-keys` already invalidates the
auth cookie and the system_settings SMTP password; OAuth tokens sharing
its fate isn't a new failure mode.

## Why OpenIddict and not hand-rolled

Hand-rolling an OAuth 2.1 authorization server is roughly 1000–1500 lines
of crypto + spec correctness we'd own forever. OpenIddict is BSD-licensed,
ships `net10.0` targets in v7.5.0, and already implements PKCE S256, RFC
6749 error codes, refresh-token rotation, and discovery metadata. The
remaining pieces — DCR endpoint, AS metadata customisation, resource
metadata, consent UI — are small enough to live in `OAuthEndpoints.cs`
plus one Razor page.

## DCR endpoint

OpenIddict 7.5.0's server builder does **not** expose a first-class
registration endpoint. We hand-roll it as a minimal API at
`POST /oauth/register` and write through `IOpenIddictApplicationManager`.
Anonymous (Claude registers without a user); rejects non-https
`redirect_uris` except for loopback (Claude Code uses
`http://127.0.0.1:<port>/callback`); stamps registration provenance
("dcr") into the application's `properties` JSON.

The discovery metadata customisation in `Program.cs` adds
`registration_endpoint` to the AS metadata so Claude finds it.

## Consent screen

`Components/Pages/AccountSecurity/OAuthConsent.razor` — static SSR Razor
page at `GET /oauth/authorize`, gated by `[Authorize]` on the cookie
scheme. Reads the OAuth request from `HttpContext.GetOpenIddictServerRequest()`,
re-emits every query parameter as a hidden form input so OpenIddict's
middleware can reconstruct the request on the POST. The form POSTs to
the same URL (`/oauth/authorize`); the POST handler in `OAuthEndpoints`
stamps an `oauth_consents` row and calls `SignIn(...)`, which OpenIddict
turns into the auth-code redirect.

Auto-skip when a matching consent row already covers the requested
scopes is recorded but the user still clicks Allow — a security
trade-off in favour of the user always seeing what they're approving.
A v2 could shorten this to a silent auto-submit.

## Claim bridge

`Services/OAuth/OAuthClaimsTransformer.cs` runs after OpenIddict
validation succeeds and stamps the ALDevToolbox claim names
(`HttpOrganizationContext.UserIdClaim` / `OrganizationIdClaim` / etc.)
on the principal. The MCP endpoint (and every other consumer of
`IOrganizationContext`) is then identical whether the bearer was a PAT
or an OAuth access token.

The transformer re-checks the user's status against the live row —
deactivating a user kills their OAuth tokens on the next request.

## Configuration

Everything has a sensible default; the operator knobs are:

| Key                                  | Default                | Purpose                                                    |
|--------------------------------------|------------------------|------------------------------------------------------------|
| `OAuth:Issuer`                       | request-derived        | Override when the public URL differs from `Request.Host`.  |
| `OAuth:AccessTokenLifetimeMinutes`   | 60                     | Refresh fires ~5 min before expiry.                        |
| `OAuth:RefreshTokenLifetimeDays`     | 30                     | Rolling — each refresh extends the window.                 |

DCR + CIMD kill-switches will land alongside the SiteAdmin runtime toggle
for MCP in a follow-up — same shape as `system_settings.mcp_enabled`.

## Risks

- **Reverse-proxy `X-Forwarded-Proto` missing in prod** → OpenIddict
  emits `http://` as the issuer → Claude rejects discovery. Add a
  startup self-check that logs a hard error when the resolved issuer is
  plain `http://` outside `Development`. (Open.)
- **CIMD open-client surface** — anyone with a public JSON metadata
  document can act as a client. The plan reserves a
  `OAuth:CimdAllowedHosts` allow-list; default is permissive to match
  Claude's expected behaviour. (Open.)
- **Multi-org users** — `User.OrganizationId` is scalar today, so the
  consent screen doesn't need an org selector. When multi-org lands, the
  consent page must show the chosen org name prominently, bind the
  consent row to it, and re-prompt when the user picks a different org.
- **Refresh-token reuse detection** — OpenIddict raises an event when a
  rotated refresh token is replayed. Wire that event to revoke the
  entire grant + audit `OAuthTokenReuseDetected`. (Open.)

[anthropic-auth]: https://claude.com/docs/claude/connectors/authentication
