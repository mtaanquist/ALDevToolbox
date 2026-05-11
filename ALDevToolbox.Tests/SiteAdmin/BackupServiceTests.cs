using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="BackupService"/>. Exercises the full
/// shell-out path against the per-test Postgres instance — so the suite
/// only runs when <c>pg_dump</c> / <c>pg_restore</c> are on PATH (the
/// runtime image ships them; <see cref="PgToolFactAttribute"/> skips when
/// they aren't installed locally).
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _backupsDir;
    private readonly MaintenanceModeState _maintenance = new();
    private readonly string? _previousBackupsDir;

    public BackupServiceTests()
    {
        _backupsDir = Path.Combine(Path.GetTempPath(), "aldt-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupsDir);
        _previousBackupsDir = Environment.GetEnvironmentVariable("BACKUPS_DIR");
        Environment.SetEnvironmentVariable("BACKUPS_DIR", _backupsDir);
        // BackupService treats SiteAdmin as required for AdHoc operations.
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BACKUPS_DIR", _previousBackupsDir);
        try { Directory.Delete(_backupsDir, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    [PgToolFact]
    public async Task Create_writes_file_and_inserts_db_row()
    {
        var svc = NewService();

        var backup = await svc.CreateAsync(BackupKind.AdHoc);

        backup.FileName.Should().EndWith(BackupService.BackupFileSuffix);
        backup.Kind.Should().Be(BackupKind.AdHoc);
        File.Exists(Path.Combine(_backupsDir, backup.FileName)).Should().BeTrue();

        await using var ctx = _db.NewContext();
        var stored = await ctx.Backups.AsNoTracking().SingleAsync();
        stored.FileSizeBytes.Should().BeGreaterThan(0);
        stored.IsPinned.Should().BeFalse();
    }

    [PgToolFact]
    public async Task Backup_then_restore_round_trips_a_marker_row()
    {
        var svc = NewService();

        // Mark the DB with a sentinel row before backup.
        await using (var ctx = _db.NewContext())
        {
            ctx.Organizations.Add(new Organization
            {
                Name = "Marker", Slug = "marker-pre-backup",
                IsPending = false, IsSeeded = true, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var backup = await svc.CreateAsync(BackupKind.AdHoc);

        // Drop the marker, write a different one, then restore.
        await using (var ctx = _db.NewContext())
        {
            var marker = await ctx.Organizations.IgnoreQueryFilters()
                .FirstAsync(o => o.Slug == "marker-pre-backup");
            ctx.Organizations.Remove(marker);
            ctx.Organizations.Add(new Organization
            {
                Name = "Replacement", Slug = "marker-post-backup",
                IsPending = false, IsSeeded = true, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await svc.RestoreAsync(backup.Id);

        await using (var ctx = _db.NewContext())
        {
            (await ctx.Organizations.IgnoreQueryFilters()
                .AnyAsync(o => o.Slug == "marker-pre-backup"))
                .Should().BeTrue("the restore brought back the pre-backup marker");
            (await ctx.Organizations.IgnoreQueryFilters()
                .AnyAsync(o => o.Slug == "marker-post-backup"))
                .Should().BeFalse("post-backup writes are discarded on restore");
        }
        _maintenance.IsActive.Should().BeFalse("maintenance mode is lifted after a successful restore");
    }

    [PgToolFact]
    public async Task Retention_prunes_oldest_unpinned_backups_and_keeps_pinned()
    {
        // Configure retention=3 to keep the test fast.
        await using (var ctx = _db.NewContext())
        {
            var settings = await ctx.SystemSettings.FirstAsync(s => s.Id == 1);
            settings.BackupRetentionCount = 3;
            await ctx.SaveChangesAsync();
        }
        var svc = NewService();

        // Take 5 backups. Pin the oldest so retention can't touch it.
        var first = await svc.CreateAsync(BackupKind.AdHoc);
        await svc.SetPinnedAsync(first.Id, pinned: true);
        // PruneRetentionAsync runs at the tail of CreateAsync. Spread the
        // remaining 4 with slightly-different created_at values so the
        // OrderByDescending is deterministic — Postgres timestamps tick at
        // microsecond resolution; force the ordering explicitly.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(20);
            await svc.CreateAsync(BackupKind.AdHoc);
        }

        await using var read = _db.NewContext();
        var rows = await read.Backups.AsNoTracking()
            .OrderBy(b => b.CreatedAt).ToListAsync();
        // 1 pinned + 3 most-recent unpinned = 4 rows survive.
        rows.Should().HaveCount(4);
        rows.Should().Contain(b => b.Id == first.Id && b.IsPinned,
            "pinned backups are exempt from retention");
        // The first unpinned backup (oldest after the pinned one) must be
        // the one pruned — there were 4 unpinned, retention is 3.
        rows.Count(b => !b.IsPinned).Should().Be(3);
    }

    [PgToolFact]
    public async Task Delete_refuses_pinned_backups()
    {
        var svc = NewService();
        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        await svc.SetPinnedAsync(backup.Id, pinned: true);

        Func<Task> act = () => svc.DeleteAsync(backup.Id);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("BackupId");
    }

    private BackupService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ctx.Database.GetConnectionString(),
            })
            .Build();
        return new BackupService(ctx, _db.OrgContext, _maintenance, config,
            NullLogger<BackupService>.Instance, TimeProvider.System);
    }
}
