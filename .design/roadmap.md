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

## Object Explorer

- **Source-only ingest for uncompiled apps.** The workspace-zip import (`FolderZipWalker.WalkWorkspace`) brings in every *compiled* app from a zipped VS Code AL workspace and skips folders that declare an `app.json` but were never built. Building those from source means synthesising the object catalogue from the `.al` headers (no `SymbolReference.json`) with `app.json` as the manifest substitute — a parallel, lower-fidelity ingest path structurally like the C/AL TXT importer (`CalImportService`). Cross-module type links the symbol package hands us for free (resolved `ModuleId`s, method/field types) have to be re-derived by name. Wants its own design pass before implementation. (For the case where we *can* compile, `object-explorer-customer-builds.md` supersedes this — compiling produces a real `SymbolReference.json`, so full-fidelity ingest; this lower-fidelity path only matters when source can't be compiled.)
- **Import a workspace straight from Azure DevOps / GitHub (PAT).** ✅ *Shipped* — `object-explorer-customer-builds.md` ("As built"): define a Customer, point it at repos, clone HEAD with a per-org PAT, resolve symbols from each `app.json`'s `application` version, compile with the runtime-provisioned BC compiler, and ingest as a `customer`-kind Release. (Still gated on outbound network policy: the host needs `dev.azure.com` / `github.com` reachable.) Remaining follow-ups:
  - **Manual-symbols recovery (was "PR 3b").** On a `failed` build-result row, upload the missing dependency `.app`(s) and re-compile just that one extension without redoing the whole build — the fallback for a dep absent from both the repo's `.alpackages/` and any Microsoft artifact.
  - **Auto-build scheduler.** A daily sweep (mirroring `ReleaseAutoImportScheduler`) that re-pulls each Customer's repos from HEAD and rebuilds. The captured `commit_sha` / `commit_date` provenance is the dedup key this needs (skip a rebuild when HEAD hasn't moved).
  - **Harden first-party dedup, then free the label globally.** First-party artifact dedup currently keys on the computed label, with the unique label index as a race backstop; only `customer`-kind is exempt from that index today. Storing the artifact version/URL as an explicit dedup key would let us drop label uniqueness for *all* kinds, making the label a pure display string. Raised during the customer-builds work; parked.

## Out of scope, even here

Recorded so they don't get pulled in by accident:

- Per-user accounts on a federated identity model with SCIM provisioning. If we get there, it's a separate product.
- A queue-based generation backend. Generation stays synchronous; if it gets slow, fix the slow part.
