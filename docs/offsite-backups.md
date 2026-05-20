# Off-site (S3-compatible) backups

The in-app backup scheduler already writes `pg_dump` files to the
`app-backups` named volume (see [operator-runbook.md](operator-runbook.md)).
Off-site backup adds a second, disaster-recovery copy by uploading every
scheduled *full* backup to an S3-compatible bucket and pruning objects past
a configurable retention window.

Tested against AWS S3 and MinIO; any S3-compatible server (Backblaze B2,
Wasabi, Cloudflare R2, Hetzner Object Storage, Ceph RGW, SeaweedFS, …)
should work — most just need **Force path-style addressing** turned on
and an endpoint URL.

## What ends up off-site, and what doesn't

| Backup kind                                           | Lives on `app-backups` volume | Uploaded off-site |
|-------------------------------------------------------|:----------------------------:|:-----------------:|
| Full backup, **Scheduled** (`BackupKind.Scheduled`)   | Yes                           | Yes (automatic, after each scheduled run) |
| Full backup, **Ad-hoc** (`BackupKind.AdHoc`)          | Yes                           | Only when SiteAdmin clicks **Upload** on the row |
| Per-tenant logical snapshot (`PerTenantBackup`)       | Yes                           | No — the off-site copy is whole-deployment DR; per-tenant snapshots stay local |

Both access keys are encrypted at rest with the ASP.NET Core Data
Protection key ring backed by the `app-keys` volume. Losing `app-keys`
makes the stored keys undecryptable and silently disables off-site uploads
until the SiteAdmin re-enters them.

## One-time setup

1. **Create a bucket and a dedicated access key.**

   - **AWS S3:** create an IAM user with a policy scoped to the bucket
     covering `s3:GetBucketLocation`, `s3:ListBucket`, `s3:PutObject`,
     `s3:GetObject`, and `s3:DeleteObject`. Generate an access key pair.
   - **MinIO / S3-compatible:** create a service account with read/write
     on the bucket. Note the endpoint URL (e.g. `https://minio.example.com`).

2. **Open `/site-admin/settings`** as a SiteAdmin. Scroll to **Off-site
   backups (S3-compatible)** and fill in:

   | Field                         | What to enter                                                                 |
   |-------------------------------|-------------------------------------------------------------------------------|
   | Endpoint                      | Blank for AWS. URL for MinIO / other S3-compatible (`https://s3.example.com`). |
   | Region                        | The bucket region (`eu-west-1`, `us-east-1`, …). Optional for non-AWS, but most servers tolerate it being set. |
   | Bucket                        | The bucket name. Required.                                                    |
   | Prefix                        | Optional key prefix, e.g. `aldevtoolbox/prod/`. Leave blank to write to the bucket root. |
   | Access key id / Secret access key | The two credentials from step 1. Leave blank on subsequent edits to keep the stored value; tick **Clear stored …** to wipe. |
   | Force path-style addressing   | Tick for MinIO and most non-AWS providers. Leave unticked for AWS S3.         |
   | Retention (days)              | Objects under the prefix older than this are deleted on every prune pass. 1–3650. |
   | Upload scheduled backups …    | Tick to actually start uploading. Leave unticked to "save the credentials but stay off".|

   Save.

3. **Click `Test off-site connection`.** The button HEADs the bucket using
   the stored credentials. Look for `OK: Connected to bucket '<name>'.` in
   the banner. `FAIL:` shows the underlying S3 error code and message —
   common ones below.

4. (Optional) From `/site-admin/backups`, take an **ad-hoc** backup and
   click the **Upload** icon on the row. The row gains an `Off-site` badge
   when the upload succeeds. Open the bucket externally (AWS console,
   `mc ls`, `aws s3 ls`) to confirm the object key matches
   `<prefix>aldevtoolbox-<UTC-timestamp>-adhoc.dump`.

That's it. From the next scheduled-backup tick onward, every scheduled
full backup uploads automatically and prune runs at the end of the tick.
Manual ad-hoc backups are uploaded only on click.

## What "Upload" and "Prune" actually do

- **Upload** (`OffsiteBackupService.UploadAsync`): streams the local
  `pg_dump` file from the `app-backups` volume to
  `s3://<bucket>/<prefix><filename>`. On success, stamps the `backups`
  row with `offsite_uploaded_at` and `offsite_object_key` so the UI can
  show the badge and link.
- **Prune** (`OffsiteBackupService.PruneAsync`): lists objects under the
  configured prefix and deletes ones whose `LastModified` is older than
  `RetentionDays`. Idempotent — safe to re-run. Runs after every
  scheduled backup tick.

Off-site retention is *independent* from local retention. The local
`BackupRetentionCount` keeps the N most recent unpinned files on disk;
off-site `RetentionDays` keeps objects for a calendar window in the
bucket. Tune them separately.

## Restoring from an off-site copy

The **Restore** button on `/site-admin/backups` only restores from the
local `app-backups` volume. To restore from an off-site object, pull it
down first.

1. Locate the object key. The `/site-admin/backups` page shows the key
   on the `Off-site` badge tooltip; or list with your tool of choice:

   ```bash
   aws s3 ls s3://<bucket>/<prefix> --recursive
   # or, for MinIO with the `mc` client:
   mc ls myminio/<bucket>/<prefix> --recursive
   ```

2. Copy the dump into the running container's backups directory:

   ```bash
   # From a workstation that has the bucket credentials:
   aws s3 cp s3://<bucket>/<prefix><filename>.dump ./<filename>.dump
   docker compose cp ./<filename>.dump aldevtoolbox:/var/lib/aldevtoolbox/backups/<filename>.dump
   ```

   The filename must match the original — `BackupService` validates it
   against `[/\\]` and `..` segments before opening it.

3. Insert a row in the `backups` table so the UI can see the file. SSH
   into the `db` container and run:

   ```sql
   INSERT INTO backups (file_name, file_size_bytes, created_at, kind, is_pinned)
   VALUES (
     '<filename>.dump',
     <size-in-bytes>,
     timezone('UTC', now()),
     0,            -- 0 = Scheduled; pick the kind that matches the original
     true          -- pin it so retention pruning doesn't bin your DR copy
   );
   ```

   Reload `/site-admin/backups`. The row appears with the file size and
   creation time you provided.

4. Click **Restore** on the row and confirm. The app enters maintenance
   mode for the duration; non-SiteAdmin users see the maintenance page.

If the deployment is completely gone (lost host, lost `pg-data`, lost
`app-backups`), the recovery path is:

1. Stand up the compose stack on a fresh host. Let migrations run
   against an empty `pg-data` so the schema is in place.
2. Sign in as the bootstrap admin (`BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`).
3. Copy the dump in and create the `backups` row as in steps 2–3 above.
4. Restore. The bootstrap user's identity will be replaced by whatever
   was in the dump — sign in again with the original credentials.

## Disabling or rotating credentials

- **Pause uploads** without losing credentials: untick **Upload scheduled
  backups …**, save. The credentials stay encrypted in the DB; nothing
  uploads until you tick it again.
- **Rotate the secret key**: paste the new secret into **Secret access key**,
  save. The new ciphertext replaces the old one. Leaving the field blank
  keeps the current value, so a partial edit to e.g. the prefix won't
  wipe credentials by accident.
- **Wipe credentials**: tick **Clear stored access key** and **Clear stored
  secret key**, save. Uploads stop on the next scheduler tick.

## Troubleshooting

Symptoms → things to check.

- **Test connection returns `FAIL: S3 error: PermanentRedirect`.** The
  region doesn't match the bucket. Either set the correct region or use
  the bucket's regional endpoint URL.
- **Test connection returns `FAIL: S3 error: SignatureDoesNotMatch`.**
  Wrong secret key, or the clock on the host is skewed by more than 15
  minutes — S3 rejects requests with a stale timestamp.
- **Test connection returns `FAIL: S3 error: AccessDenied` on
  `GetBucketLocation`.** The IAM policy is missing
  `s3:GetBucketLocation`. Either grant it, or use a different identity
  with broader bucket-level read permission for the test.
- **Test connection passes, but uploads silently don't happen.** Open
  `/site-admin/backups` and confirm the backup row has no `Off-site`
  badge. Most common cause: **Upload scheduled backups …** is unticked.
  Next: the local file is missing under
  `/var/lib/aldevtoolbox/backups/` (the upload refuses rather than
  uploading nothing); check `docker compose logs aldevtoolbox` for
  `Refusing to upload`.
- **Uploads worked, then stopped after a redeploy.** The `app-keys`
  volume was lost or replaced — the encrypted credentials no longer
  decrypt. Look for
  `Failed to decrypt off-site credentials` in the logs. Re-enter the
  access key and secret on `/site-admin/settings` and save.
- **MinIO returns `XAmzContentSHA256Mismatch` on uploads.** Some older
  S3-compatible servers can't verify the streaming payload signature
  the SDK sends. The service sends `DisablePayloadSigning = true`
  whenever **Force path-style addressing** is ticked, which is the
  usual workaround; if you hit this with path-style off, tick path-style.
- **Prune runs but nothing is deleted.** Confirm `RetentionDays` is set
  and that the prefix matches the upload path — `ListObjectsV2` filters
  on `Prefix`, so retention can only see what's under it.
- **A bucket policy that requires server-side encryption rejects uploads
  with `AccessDenied`.** The service does not set `ServerSideEncryption`
  on the `PutObject` request. Either relax the policy, or configure
  bucket-default encryption so the server applies it on receipt.

## Acceptance check

After setup, all three of the following should be true:

- `/site-admin/settings` → **Test off-site connection** → `OK: Connected …`.
- The next scheduled backup row on `/site-admin/backups` shows the
  `Off-site` badge within ~30 s of the scheduled time.
- An external listing (`aws s3 ls`, `mc ls`, AWS console) shows the
  object under the configured prefix with the matching filename.
