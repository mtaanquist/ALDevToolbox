# Roadmap

Forward-looking ideas that are **not committed**. This is the wishlist; the record of what shipped lives in `completed-milestones.md`. Nothing here is scheduled — order is rough, and sequencing gets hashed out when a phase is actually planned. Move an item into a milestone/issue when it graduates; delete it here when it ships or when we decide against it.

(Successor to the former `milestones.md` "Phase 5 candidates" section. Items that had already shipped — two-factor auth, and live preview on New Extension — were dropped when this file was created.)

## Identity

- **SSO / OIDC integration alongside email-password.** Per-org IdP config (Azure AD, Google, generic OIDC). Existing accounts coexist with federated ones. Deliberately left out of managed-hosting v1; the biggest single identity item.

## UX

- **Mobile / narrow-viewport layout.** The shell is desktop-only today.
- **Per-org theming beyond logo** — accent colour, app name in the top bar, favicon. Partly reachable today by extending M14's `organization_assets` / `organization_settings`.

## Generation

- **Workspace upgrade flow.** Given an existing generated workspace, diff it against the current template state and let the user apply selected updates. Big — needs its own design pass before it's an anchor for a phase.
- **Conditional folders / files.** "Include this folder only when module X is selected." Expressible today by splitting templates; a real conditional grammar would compress that.
- **Binary files in template folders.** v1 was text-only; some templates (icons, splash assets) want bytes.

## Out of scope, even here

Recorded so they don't get pulled in by accident:

- Per-user accounts on a federated identity model with SCIM provisioning. If we get there, it's a separate product.
- A queue-based generation backend. Generation stays synchronous; if it gets slow, fix the slow part.
