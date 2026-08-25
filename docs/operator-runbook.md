# Operator runbook

What you need to run AL Dev Toolbox for paying users. Each section is a flow,
not a tour of the codebase. Treat the bullet at the end of each section as the
acceptance check.

## Volume layout

The compose stack mounts three named volumes. Back up what's in `pg-data` and
`app-keys` together; backups in `app-backups` are a convenience layer on top.

| Volume        | Mount path inside `aldevtoolbox` | What it holds                                                    |
|---------------|----------------------------------|------------------------------------------------------------------|
| `pg-data`     | (mounted on `db` service)        | Postgres data files — the only thing that *needs* a real backup. |
| `app-keys`    | `/var/lib/aldevtoolbox/dp-keys`  | ASP.NET Data Protection key ring. Loss invalidates all login cookies and decrypts of the stored SMTP password. |
| `app-backups` | `/var/lib/aldevtoolbox/backups`  | `pg_dump` files written by the in-app backup tooling.            |

Verify: `docker volume ls | grep aldevtoolbox` shows three volumes after the
first `docker compose up`.

## Fresh deploy

1. Pick a Postgres password and a bootstrap admin password. The bootstrap
   admin password is read once on a fresh database; rotate it from
   `/account` after first sign-in.

   ```bash
   export POSTGRES_PASSWORD=$(openssl rand -hex 16)
   export BOOTSTRAP_ADMIN_EMAIL=admin@example.com
   export BOOTSTRAP_ADMIN_PASSWORD=$(openssl rand -base64 18)
   ```

2. Bring up the stack. The first boot runs migrations and seeds the Default
   organisation; expect `/readyz` to take ~30–60 s to turn green on a fresh
   `pg-data` volume.

   ```bash
   docker compose up --build -d
   curl --fail --silent --show-error http://localhost:8080/readyz
   ```

3. Sign in as the bootstrap email. The first user is stamped
   `IsSiteAdmin = true`, so the **Site Admin** section appears in the sidebar.
   Visit `/site-admin/settings` and fill in SMTP. Promote a second SiteAdmin
   from `/site-admin/users` so you're not a single point of failure.

4. Hide `/signup` at the reverse proxy if the deployment is invite-only. The
   page is wired but exposed under the bare hostname by default.

Verify: `curl http://localhost:8080/healthz` returns 200; signed-in admin sees
the **Site Admin** sidebar entry.

## Scheduled backup

The in-app scheduler runs `pg_dump -Fc` against the live database and writes
to the `app-backups` volume.

1. Visit `/site-admin/settings`. Under **Backups**, tick **Enable scheduled
   backups** and set the UTC time of day (default 02:00). Save.
2. Set the retention count on the same page. After each backup, files past
   the retention count that aren't pinned are pruned. Pin anything you want
   to keep indefinitely from `/site-admin/backups`.
3. To see the scheduler fire without waiting overnight, set the schedule
   time a minute or two ahead of now. The scheduler polls every minute.

Verify: a row appears under `/site-admin/backups` with kind *Scheduled*
within five minutes of the scheduled time. The `app-backups` volume holds the
file.

## Manual backup

For pre-upgrade or ad-hoc captures.

1. Visit `/site-admin/backups`. Click **Take a backup now**. A row appears
   with kind *Ad-hoc* and a download link.
2. Pin the row before doing anything destructive — unpinned rows roll off
   under retention.
3. To copy the file off the host:

   ```bash
   docker compose cp aldevtoolbox:/var/lib/aldevtoolbox/backups/<filename> ./
   ```

Verify: `docker compose exec aldevtoolbox ls /var/lib/aldevtoolbox/backups`
lists the file.

## In-place restore

Destructive. Drops the `public` schema and replays the dump. The app enters
maintenance mode (503 for non-SiteAdmin) for the duration; SiteAdmin requests
and `/healthz` / `/readyz` keep going.

1. Take a fresh manual backup first (see above). If the restore goes sideways
   you want a known-good snapshot to roll back to.
2. `/site-admin/backups`, find the target row, click **Restore**. Tick the
   confirmation checkbox and submit.
3. While the restore runs, every non-SiteAdmin request gets a static
   maintenance page with the start time. The page polls; signed-in users see
   the app come back automatically when the restore completes.
4. The restore writes an audit row with kind *Restore*. Review
   `/site-admin/audit` afterwards.

Verify: post-restore, `/readyz` is 200 and `/site-admin/audit` shows the
*Restore* entry. Non-SiteAdmin sign-in works.

If the restore fails part-way, the database is in an indeterminate state —
restore the most recent pre-restore backup to roll back, then investigate.

## Off-site (S3-compatible) backups

For pushing scheduled backups to AWS S3, MinIO, or any other S3-compatible
bucket — plus the restore-from-bucket recovery path when the local
`app-backups` volume is gone — see
[`offsite-backups.md`](offsite-backups.md).

Verify: with off-site enabled, the next scheduled backup row on
`/site-admin/backups` carries an `Off-site` badge and the object appears
under the configured prefix in the bucket.

## Microsoft (Entra ID) sign-in

Federated sign-in is opt-in per organisation and sits alongside
email/password. Setup is two-sided: a SiteAdmin registers the app once, then
each organisation turns it on and says which Microsoft tenants may use it.

**In the Microsoft Entra admin center**, under *App registrations → New
registration*:

1. **Supported account types.** Single-tenant ("Accounts in this
   organizational directory only") is the right default when the people
   signing in live in the same tenant that hosts the registration — Entra
   itself then refuses everyone else, on top of the allow-list below.
   Multi-tenant works too, and there the allow-list is the only boundary.
2. **Redirect URI.** Platform *Web*, address `https://<your-host>/signin-microsoft`.
   Both settings pages in the app display the exact URI to copy — use that
   rather than typing it. It has to be the public HTTPS address; the app
   builds it from the forwarded host/proto headers, so a reverse proxy that
   drops `X-Forwarded-Proto` produces an `http://` mismatch here.
3. **Certificates & secrets → New client secret.** Copy the *Value* (not the
   Secret ID) and note the expiry date it shows you.
4. **API permissions.** None needed. The app requests `openid profile email`
   and never calls Graph, so the default `User.Read` can be removed and no
   admin consent is required. Leave the implicit-grant checkboxes off — this
   is authorization code flow with a secret.

**In the app**, as a SiteAdmin: `/site-admin/settings/entra`. Paste the
Application (client) ID, the secret, and its expiry date. This alone lets
nobody in; it only makes the registration available.

**Per organisation**, as an org Admin: `/admin/administration/identity`, under
*Microsoft sign-in*. Turn it on, add the Directory (tenant) ID under
*Microsoft organisations allowed*, Save. The save is refused until at least
one tenant is listed — the allow-list is what keeps strangers out, so an
empty one would either let everybody in or nobody.

An org that needs the registration in its own tenant can supply its own client
id and secret on the same page, under *Use my own registration instead*;
otherwise it rides on the site-wide one.

Verify: the login page offers **Sign in with Microsoft**, and a work account
from a listed tenant reaches the app. An account from an unlisted tenant is
turned away and the attempt appears in `login_attempts`.

## Microsoft client-secret rotation

Entra client secrets expire, and Entra never tells the app when. Sign-in
simply starts failing, so the expiry date is recorded by hand and the app
warns ahead of it.

1. Record the date when you paste a secret in — *Secret expires on*, on
   `/site-admin/settings/entra` for the site-wide registration or
   `/admin/administration/identity` for an organisation's own. Leaving it
   blank is allowed and means no warning is possible.
2. From two weeks out, the settings page carries a warning and an org whose
   own registration is lapsing gets a row in **Needs attention** on `/admin`.
   An org riding on the site-wide registration sees a read-only note naming
   the deadline, since renewing it is a SiteAdmin's job, not theirs.
3. To rotate: create a new secret in the Entra admin center, paste the value
   and its new expiry into the same form, and save. The old secret keeps
   working until Entra retires it, so there is no cutover window.
4. Both secrets are encrypted with the Data Protection key ring. Losing
   `app-keys` means re-entering them — the failure is logged loudly rather
   than silently breaking sign-in.

Verify: with a date inside two weeks, the settings page shows the warning and
`/admin` lists the row; after saving a fresh secret and date, both go quiet.

## SMTP rotation

1. `/site-admin/settings`. Update **SMTP password** (the rest of the SMTP
   block can stay as-is). Save.
2. Click **Send test email to me**. A 200 means SMTP is reachable; failures
   show inline with the SMTP error text.
3. The stored password is encrypted with the Data Protection key ring. Don't
   delete `app-keys` between rotations — the existing ciphertext becomes
   unreadable.

Verify: the test email arrives. `/site-admin/audit` shows a *SystemSettings*
update row redacted to `<redacted>` for the password field.

## SiteAdmin promotion and demotion

SiteAdmin is org-scoped to **Default** at first, but the role itself crosses
orgs — a SiteAdmin sees `/site-admin/*` regardless of which org they belong
to.

1. **Promote.** `/site-admin/users`. Find the user, tick the **Site admin**
   column. The change is audited with both before/after rows.
2. **Demote.** Same page, untick. The "last SiteAdmin" guard refuses to
   demote the only remaining SiteAdmin — promote someone else first.
3. **Lost SiteAdmin.** If every SiteAdmin loses access, bring up a one-off
   container with `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` set
   to a new email — the bootstrap path runs only against an empty database,
   so this is a recovery-from-empty path, not a back door. The supported
   recovery is to restore from a backup taken when SiteAdmin still worked,
   or to SQL-update `users.is_site_admin = true` directly against the
   `pg-data` volume.

Verify: the promoted user sees the **Site Admin** sidebar entry on next
sign-in. `/site-admin/users` refuses to demote the last SiteAdmin.

## Recovering from lost Data Protection keys

If `app-keys` is wiped, all signed-in cookies become invalid (users get
redirected to login) and the encrypted SMTP password becomes unreadable.

1. Recreate the volume by restarting the app — the startup code recreates the
   directory and a fresh key ring.
2. Every user signs in again. The cookie ring rotates without their
   intervention; nothing else breaks.
3. The SMTP password column is now ciphertext that no key can decrypt. Visit
   `/site-admin/settings`, re-enter the password, save. The next email send
   uses the freshly-encrypted value.
4. Audit log entries that captured the encrypted SMTP password before the
   loss stay redacted; they were already redacted at write time.

Verify: `/healthz` returns 200 after the restart; sending a test email from
`/site-admin/settings` succeeds.

## Migrating from a v1 (SQLite) deployment

See [`.design/migrating-from-sqlite.md`](../.design/migrating-from-sqlite.md).
The path is: export TOML from the v1 admin UI, bring up the v2 compose stack
against a fresh `pg-data`, sign in as the bootstrap admin, import the TOML
from `/admin/configuration`.
