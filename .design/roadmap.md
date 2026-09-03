# Roadmap

Forward-looking ideas that are **not committed**. This is the wishlist; the record of what shipped lives in `completed-milestones.md`. Nothing here is scheduled — order is rough, and sequencing gets hashed out when a phase is actually planned. Move an item into a milestone/issue when it graduates; delete it here when it ships or when we decide against it.

(Successor to the former `milestones.md` "Phase 5 candidates" section. Items that had already shipped — two-factor auth, and live preview on New Extension — were dropped when this file was created.)

## Identity

- **A second identity provider.** Federated sign-in itself shipped: Microsoft Entra ID, opt-in per organisation, coexisting with email-password (`auth-and-audit.md`). What's still uncommitted is a *second* provider — Google, or generic OIDC — which means per-org IdP config rather than the single Entra app registration. Adding one is a conversation first (`CLAUDE.md`).

## UX

- **Mobile / narrow-viewport layout.** The shell is desktop-only today.
- **Per-org theming beyond logo** — accent colour, app name in the top bar, favicon. Partly reachable today by extending M14's `organization_assets` / `organization_settings`.

## Generation

- **Workspace upgrade flow.** Given an existing generated workspace, diff it against the current template state and let the user apply selected updates. Big — needs its own design pass before it's an anchor for a phase.
- **Conditional folders / files.** "Include this folder only when module X is selected." Expressible today by splitting templates; a real conditional grammar would compress that.
- **Binary files in template folders.** v1 was text-only; some templates (icons, splash assets) want bytes.

## Object Explorer

- **Source-only ingest for uncompiled apps.** The workspace-zip import (`FolderZipWalker.WalkWorkspace`) brings in every *compiled* app from a zipped VS Code AL workspace and skips folders that declare an `app.json` but were never built. Building those from source means synthesising the object catalogue from the `.al` headers (no `SymbolReference.json`) with `app.json` as the manifest substitute — a parallel, lower-fidelity ingest path structurally like the C/AL TXT importer (`CalImportService`). Cross-module type links the symbol package hands us for free (resolved `ModuleId`s, method/field types) have to be re-derived by name. Wants its own design pass before implementation. (For the case where we *can* compile, `object-explorer-project-builds.md` supersedes this — compiling produces a real `SymbolReference.json`, so full-fidelity ingest; this lower-fidelity path only matters when source can't be compiled.)
- **Import a workspace straight from Azure DevOps / GitHub (PAT).** ✅ *Shipped* — `object-explorer-project-builds.md` ("As built"): define a Project (called a Customer when this shipped), point it at repos, clone HEAD with a per-org PAT, resolve symbols from each `app.json`'s `application` version, compile with the runtime-provisioned BC compiler, and ingest as a `project`-kind Release. (Still gated on outbound network policy: the host needs `dev.azure.com` / `github.com` reachable.) Manual-symbols recovery shipped — upload the missing dependency `.app`(s) from a build's manage page; they're stored against the project (`oe_project_symbols`) and merged into the symbol cache on a rebuild, so the build resolves a dep absent from both the repo's `.alpackages/` and any Microsoft artifact (see `object-explorer-project-builds.md`, "Manual-symbols recovery shipped"). The follow-ups that were parked here have all shipped, except auto-build, which was **removed**: builds are user-initiated only, because a background sweep has no user whose token to clone with. There is no auto-build scheduler and no `DISABLE_*_AUTO_BUILD_SCHEDULER` / `*_AUTO_BUILD_HOUR_UTC` setting to configure.
  - **Harden first-party dedup, then free the label globally.** ✅ *Shipped.* First-party artifact dedup now keys on an explicit `oe_releases.dedup_key` (`bc-onprem:{Maj}.{Min}:{cc}`, set by `ArtifactReleaseImporter` / `BcArtifactIndex.FormatDedupKey`), with the unique index moved from `(org, label)` to `(org, dedup_key)` filtered to non-null keys. The **label is now a pure display string for every kind** — manual uploads, third-party, and customer releases carry no key and never collide. Migration `20260710000000_HardenFirstPartyDedupKey` adds the column, **backfills it onto existing first-party releases** by parsing their `"Business Central {Maj}.{Min} ({CC})"` label (rows that don't match keep a null key), then swaps the index. The label-uniqueness pre-check became `EnsureDedupKeyAvailableAsync`. **Behavioural note:** a manual upload may now reuse any label, including one an artifact import produced — the label is no longer a uniqueness surface.

## Out of scope, even here

Recorded so they don't get pulled in by accident:

- Per-user accounts on a federated identity model with SCIM provisioning. If we get there, it's a separate product.
- A queue-based generation backend. Generation stays synchronous; if it gets slow, fix the slow part.
