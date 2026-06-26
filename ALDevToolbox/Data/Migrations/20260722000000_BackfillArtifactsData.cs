using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Data-only migration that carries the Object-Explorer-era project data onto
    /// the Artifacts model (slice 4). Idempotent — every statement guards with
    /// NOT EXISTS / emptiness checks so a re-run (or running on a DB that already
    /// has some of the new rows) is a no-op. See <c>.design/artifacts.md</c>
    /// ("Migration"). The legacy <c>oe_project_build_results</c> table is left in
    /// place (the Object Explorer manage page still reads it); this migration
    /// copies its provenance onto <c>oe_project_build_repo_commits</c>.
    /// </summary>
    public partial class BackfillArtifactsData : Migration
    {
        /// <summary>
        /// The backfill, exposed as a const so the migration test replays the exact
        /// SQL rather than a copy (a regression in the body trips the test). Four
        /// independent, idempotent statements run as one batch.
        /// </summary>
        public const string BackfillSql = @"
            -- 1. Backfill project ownership: an ownerless project (created in the
            --    Object-Explorer era, before created_by_user_id existed) is adopted
            --    by the lowest-id active Admin in its org. An org with no active
            --    Admin leaves its projects ownerless (admin-managed) until reassigned.
            UPDATE oe_projects p
            SET created_by_user_id = (
                SELECT u.id FROM users u
                WHERE u.organization_id = p.organization_id
                  AND u.role = 'Admin' AND u.status = 'Active'
                ORDER BY u.id LIMIT 1)
            WHERE p.created_by_user_id IS NULL
              AND EXISTS (
                SELECT 1 FROM users u2
                WHERE u2.organization_id = p.organization_id
                  AND u2.role = 'Admin' AND u2.status = 'Active');

            -- 2. Synthesise a ProjectBuild for every existing project-kind Release
            --    that doesn't have one yet, linked back to the project via the import
            --    job's project_id -> release_id mapping. Older builds have no retained
            --    .app bytes, full logs, or changelog — those stay empty rather than
            --    being fabricated; only the release link + status + version + timing
            --    are recoverable.
            INSERT INTO oe_project_builds
                (organization_id, project_id, started_by_user_id, release_id, status, bc_version, started_at, finished_at)
            SELECT r.organization_id, j.project_id, NULL, r.id,
                   CASE r.status WHEN 'ready' THEN 'ready' WHEN 'failed' THEN 'failed' ELSE 'building' END,
                   r.bc_version, r.imported_at,
                   CASE WHEN r.status IN ('ready', 'failed') THEN r.imported_at ELSE NULL END
            FROM oe_releases r
            JOIN LATERAL (
                SELECT ij.project_id
                FROM oe_import_jobs ij
                WHERE ij.release_id = r.id AND ij.project_id IS NOT NULL
                ORDER BY ij.id LIMIT 1
            ) j ON TRUE
            WHERE r.kind = 'project'
              AND EXISTS (SELECT 1 FROM oe_projects p WHERE p.id = j.project_id)
              AND NOT EXISTS (SELECT 1 FROM oe_project_builds b WHERE b.release_id = r.id);

            -- 3. Migrate the per-app build report's source provenance
            --    (oe_project_build_results: repo_url / commit_sha / commit_date) onto
            --    the synthesised builds' commit set. One row per distinct (build,
            --    repo, commit); the repo display name is the URL's last path segment
            --    (sans .git). Only runs for builds with no commit set yet, so real
            --    (slice-2) builds are never touched.
            INSERT INTO oe_project_build_repo_commits
                (organization_id, project_build_id, project_repository_id, repo_url, repo_display_name, commit_hash, committed_at)
            SELECT DISTINCT ON (b.id, res.repo_url, res.commit_sha)
                   b.organization_id, b.id, NULL,
                   LEFT(COALESCE(res.repo_url, ''), 2000),
                   LEFT(COALESCE(NULLIF(regexp_replace(regexp_replace(res.repo_url, '\.git$', ''), '^.*/', ''), ''), ''), 250),
                   LEFT(COALESCE(res.commit_sha, ''), 64),
                   res.commit_date
            FROM oe_project_build_results res
            JOIN oe_project_builds b ON b.release_id = res.release_id
            WHERE res.repo_url IS NOT NULL AND res.repo_url <> ''
              AND NOT EXISTS (
                SELECT 1 FROM oe_project_build_repo_commits c WHERE c.project_build_id = b.id);

            -- 4. Seed each org's allowed-providers set from the providers its existing
            --    repositories actually use (the org PAT pair was retired in slice 1).
            --    An org with no repositories — or one whose set is already populated —
            --    is left alone; an empty set already means ""all providers allowed"".
            UPDATE organization_settings s
            SET allowed_repository_providers = sub.provs
            FROM (
                SELECT organization_id, array_agg(DISTINCT provider ORDER BY provider) AS provs
                FROM oe_project_repositories
                GROUP BY organization_id
            ) sub
            WHERE s.organization_id = sub.organization_id
              AND (s.allowed_repository_providers IS NULL OR cardinality(s.allowed_repository_providers) = 0);
        ";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(BackfillSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data backfill — not reversibly undone. The synthesised builds and
            // copied provenance are harmless to leave; the schema rollback lives in
            // the AddArtifactsBuildModel migration.
        }
    }
}
