using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Storage;

/// <summary>
/// Covers the persisted-snapshot path on <c>DatabaseUsageService</c> that
/// backs the sidebar <c>StorageBar</c> and the SiteAdmin storage page.
/// Computing usage live ran a <c>COUNT(*)</c> over every tenanted table on
/// each navigation; <c>UsageSnapshotScheduler</c> now recomputes on a timer
/// and the display surfaces read the cached row. These tests pin the
/// recompute/read contract — that recompute writes one row per org, that the
/// per-org read returns null until the first recompute, and that the
/// cross-org list LEFT JOINs so an org with no snapshot still appears.
///
/// Fresh database per test (not a shared fixture) so the "before recompute"
/// assertions see a clean snapshot table.
/// </summary>
public sealed class DatabaseUsageSnapshotTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RecomputeSnapshotsAsync_writes_one_row_per_organisation()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        var snapshots = await read.OrganizationUsageSnapshots.AsNoTracking().ToListAsync();
        snapshots.Select(s => s.OrganizationId)
            .Should().BeEquivalentTo(new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
        snapshots.Should().OnlyContain(s => s.ComputedAt != default);
    }

    [Fact]
    public async Task RecomputeSnapshotsAsync_is_idempotent_and_updates_in_place()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);
        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        var count = await read.OrganizationUsageSnapshots.CountAsync();
        // UPSERT, not insert: a second pass refreshes the same rows.
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetSnapshotForCurrentOrgAsync_returns_null_before_first_recompute()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        var row = await usage.GetSnapshotForCurrentOrgAsync(CancellationToken.None);

        row.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotForCurrentOrgAsync_reads_back_the_acting_org_after_recompute()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);
        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        var row = await usage.GetSnapshotForCurrentOrgAsync(CancellationToken.None);

        row.Should().NotBeNull();
        row!.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.ComputedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ListFromSnapshotsAsync_includes_every_org_even_without_a_snapshot()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        // No recompute yet: the LEFT JOIN still surfaces every org, with a
        // null ComputedAt (so the page can say "not computed yet").
        var before = await usage.ListFromSnapshotsAsync(CancellationToken.None);
        before.Select(r => r.OrganizationId)
            .Should().BeEquivalentTo(new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
        before.Should().OnlyContain(r => r.ComputedAt == null);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        var after = await usage.ListFromSnapshotsAsync(CancellationToken.None);
        after.Should().OnlyContain(r => r.ComputedAt != null);
    }
}
