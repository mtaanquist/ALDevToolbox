# Connecting AI clients to the MCP server

AL Dev Toolbox exposes its template browser, workspace generator, Object
Explorer queries, and snippet library through a Model Context Protocol (MCP)
endpoint at `/mcp`. AI agents authenticate with a **Personal Access Token
(PAT)** issued from your account page.

## 1. Create a token

1. Sign in at the AL Dev Toolbox web app.
2. Go to **Account → Manage access tokens** (or directly to
   `/account/access-tokens`).
3. Click **Create token**. Give it a recognisable name (e.g. *Cursor on
   laptop*) and pick an expiry. **Save the token immediately** — the next
   page is the only place it will be shown.

You can revoke a token at any time from the same page. SiteAdmins can revoke
tokens across the whole deployment at `/site-admin/access-tokens`.

## 2. Configure your AI client

The reveal screen at `/account/access-tokens/created` shows ready-to-paste
snippets with your token and the server URL pre-filled. The reference shapes
below use `${PAT}` and `${SERVER}` placeholders.

### VS Code (GitHub Copilot agent mode)

Save to your workspace's `.vscode/mcp.json` (or your user `settings.json`
under `mcp.servers`). Requires Copilot agent mode to be enabled.

```json
{
  "servers": {
    "aldevtoolbox": {
      "type": "http",
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Reload the window after saving.

### Claude Desktop

Open **Settings → Developer → Edit Config**. The config file is at:
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- Linux: `~/.config/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "aldevtoolbox": {
      "type": "http",
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Restart Claude Desktop after saving.

### Cursor

Open **Cursor Settings → MCP → Add Server**, or edit `~/.cursor/mcp.json`
directly:

```json
{
  "mcpServers": {
    "aldevtoolbox": {
      "url": "${SERVER}/mcp",
      "headers": {
        "Authorization": "Bearer ${PAT}"
      }
    }
  }
}
```

Cursor reloads MCP servers automatically.

### Claude Code (CLI)

Run from any project directory:

```sh
claude mcp add aldevtoolbox \
  --transport http \
  --url ${SERVER}/mcp \
  --header "Authorization: Bearer ${PAT}"
```

The server is then available in any Claude Code session in that directory.

## 3. What you can ask

Once connected, the agent has tools for:

- **Templates and generation** — list templates and modules, generate a new
  workspace or a standalone extension. The generated ZIP comes back inline
  (base64) in the tool result.
- **Object Explorer** — list releases, search objects / procedures /
  content, and find references to a specific table or field across a BC
  release. Useful for *"which procedures touch the No. field on Sales
  Line in version 28.1?"*-style questions.
- **Forward-edge navigation** — outline an object, read one procedure's
  source, list its outgoing calls. The three tools chain naturally:
  `get_object_outline` returns each symbol's id and line number;
  `get_procedure_source` slices the body (capped at 200 lines with a
  truncation marker); `list_procedure_calls` returns the outgoing
  method calls and field accesses so the agent can follow the chain.
  Useful for *"what happens when you post a sales order in 28.1?"* —
  trace from the Sales Order page's Post action through
  `CallPostDocument` into Codeunit `Sales-Post`. Pass `symbolId` from
  the outline when the procedure name is ambiguous (page-action
  `OnAction`, table-field `OnValidate`).
- **Snippets** — search and fetch snippets your organisation has saved.

The agent acts as you: same organisation, same role, same audit trail. PATs
do **not** carry SiteAdmin authority unless your account does.

## Troubleshooting

- **401 Unauthorized** — the token is missing, malformed, expired, or
  revoked. Re-issue it.
- **The server doesn't appear in my client** — confirm the URL is reachable
  from your machine (`curl -H "Authorization: Bearer ${PAT}" ${SERVER}/mcp`
  should return a 200 / streamable response, not a 404 or HTML).
- **Tool calls return validation errors** — the toolbox returns the same
  field-keyed errors the web UI uses. Re-prompt the agent with the missing
  field or a corrected value.
