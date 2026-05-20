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

`/site-admin/backups` lists every dump-shaped object under the configured
prefix in an **Off-site catalogue** section. The flow is fully UI-driven
— no shelling into the container, no SQL by hand.

1. Open `/site-admin/backups` as a SiteAdmin. Below the local backups
   table you'll see **Off-site catalogue** with each remote object's file
   name, last-modified timestamp, size, and whether a local row with the
   same file name already exists. If the catalogue is empty, take a
   scheduled or ad-hoc backup first and upload it.

2. Pick the row you want and click its **Download** icon
   (archive-restore). The button posts to
   `/site-admin/backups/offsite/download`, which enqueues a job on the
   background worker and redirects you straight back to the page.

3. An **Off-site downloads** panel appears at the top of the page with a
   live progress bar — bytes downloaded over total size, refreshed every
   second by a small in-page script polling
   `/site-admin/backups/offsite/jobs/{id}`. The download runs in a
   `BackgroundService`, so the bar keeps moving even if you close the
   browser tab. The status flips to **Completed** when the dump lands
   on the `app-backups` volume.

4. When every active job is terminal the page reloads. The downloaded
   file appears in the local backups table with an `Off-site` badge,
   pinned automatically so retention can't prune it before you've
   restored.

5. Click **Restore** on the new row and confirm. The app enters
   maintenance mode for the duration; non-SiteAdmin requests get a
   static maintenance page until `pg_restore` finishes.

If the deployment is completely gone (lost host, lost `pg-data`, lost
`app-backups`), the recovery path is just as short:

1. Stand up the compose stack on a fresh host pointed at the same
   bucket. Set `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` so
   you can sign in against the empty database; migrations run against an
   empty `pg-data` and the bucket is untouched.
2. Sign in as the bootstrap admin. Configure off-site under
   `/site-admin/settings` with the same bucket, prefix, and credentials.
3. Open `/site-admin/backups`. The catalogue lists every object that
   survived the loss. Download the snapshot you want, wait for the
   progress bar to finish, click **Restore**, confirm.
4. The bootstrap user's identity will be replaced by whatever the dump
   carried — sign in again with the original credentials.

### Job lifetime and host restarts

Restore jobs live in memory on the running container. If the host
crashes or you redeploy mid-download, the job is lost — but the
download writes to `<filename>.partial` and is only renamed to the final
name on success, so a half-finished file never gets registered as a
restorable row. Just re-queue the download from the catalogue.

Terminal jobs (Completed, Failed) linger on the page for an hour so a
SiteAdmin who refreshed at the wrong moment can still see the outcome,
then evict themselves on the next page render.

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
- **Download job stuck in Queued.** The single-threaded restore worker
  is serialising — only one download runs at a time. The earlier job
  finishes first, then the next moves to Running. If no other job is
  active, check `docker compose logs aldevtoolbox` for
  `OffsiteRestoreWorker` errors.
- **Download fails with "A local backup named '...' already exists".**
  Either delete or rename the local row (unpin first if needed), or
  rename the off-site object out of the way. The service refuses to
  overwrite a row that may already be on disk.
- **Catalogue says "Already local: Yes" but the Download button is
  disabled.** That's the same collision-prevention guard. The local row
  is already there — restore from it directly.
- **A bucket policy that requires server-side encryption rejects uploads
  with `AccessDenied`.** The service does not set `ServerSideEncryption`
  on the `PutObject` request. Either relax the policy, or configure
  bucket-default encryption so the server applies it on receipt.

## Acceptance check

After setup, all four of the following should be true:

- `/site-admin/settings` → **Test off-site connection** → `OK: Connected …`.
- The next scheduled backup row on `/site-admin/backups` shows the
  `Off-site` badge within ~30 s of the scheduled time.
- An external listing (`aws s3 ls`, `mc ls`, AWS console) shows the
  object under the configured prefix with the matching filename.
- `/site-admin/backups` shows the object under **Off-site catalogue**;
  clicking **Download** brings up a progress bar in **Off-site
  downloads** that runs to completion and produces a new local row
  carrying the `Off-site` badge.
