# Migrating from SQLite (v1 → P4.16)

P4.16 replaces SQLite with PostgreSQL 18 as the only supported database. There is no automatic in-place migration — the upgrade is a one-way export/import:

1. Export the catalogue from the previous (SQLite) deployment as TOML.
2. Bring up the new (Postgres) deployment against an empty volume.
3. Import the TOML on the new deployment.

Audit log history is intentionally **not** preserved across the move. `audit_log` rows live in the database, the export does not include them, and the new deployment starts with an empty audit table. Operationally relevant changes start fresh post-upgrade.

## Step-by-step

The procedure assumes you control both the old and the new deployment. If the new one is replacing the old one in place, run the export first, then tear down the old stack before bringing up the new one.

1. **On the old (SQLite) deployment** — sign in as an admin in each organisation that needs to carry over and click **Export to TOML** under `/admin/configuration`. Save the resulting ZIP per-organisation; export is per-org. (If the old deployment is the single-tenant pre-M13 layout, you only have the one export to take.)

2. **Provision the new deployment** — start the new compose stack. The `db` service comes up empty; the app applies the M16 `InitialCreate` migration; the bootstrap admin (from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`) lands in the **Default** organisation. The Default org is seeded from `Templates.seed/` on first admin login.

3. **For each organisation** — sign in as that org's admin and use the **Import** button under `/admin/configuration` to upload the TOML ZIP exported in step 1. Import is idempotent; re-uploading the same ZIP into the same org is safe.

4. **Verify** — confirm template count, module count, dependency catalogue, application versions, organisation defaults, logo, and always-included files match. Generate a workspace ZIP for one of the runtime templates as a smoke test.

## What gets carried over

The export contains everything the import understands:

- Runtime templates and their folder layouts, including per-folder file contents.
- Module definitions and their dependencies.
- The well-known dependency catalogue.
- Application versions.
- Organisation settings (default publisher, default ID range, default brief, default core description).
- The organisation logo.
- Always-included files.

What it does **not** carry over:

- **Audit log history.** Acknowledged in the milestone — the savings from a clean Postgres history outweigh the cost of recreating audit context.
- **User accounts.** Each user re-signs up against the new deployment. The bootstrap admin still comes from the env vars.
- **Pending signup requests, password-reset tokens, login attempts.** Transient by design.

## Connection string

The new deployment reads `ConnectionStrings__DefaultConnection` (Npgsql format). The compose file builds it from `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, all of which default to `aldevtoolbox` if unset. Setting `POSTGRES_PASSWORD` is a deployment requirement, even if the rest is defaulted.

## Rolling back

The new deployment writes a fresh database. Rolling back is "stop the new stack, restart the old one" — the old SQLite file is untouched. Once the new deployment has been promoted to production and used long enough to accumulate state worth keeping, the old volume can be archived and removed.
