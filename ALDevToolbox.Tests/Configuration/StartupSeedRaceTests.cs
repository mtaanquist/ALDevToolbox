using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Startup seeds the platform-default <see cref="OrganizationFile"/> rows for
/// any org that has none. Two boots sharing one database (parallel
/// WebApplicationFactory test hosts, or would-be multi-replica startups) can
/// both read "this org has no files" and both insert the same
/// <c>(organization_id, path)</c> rows — the unique index rejects the loser.
/// <see cref="StartupTasks.IsUniqueViolation"/> is what lets the losing boot
/// treat that as a benign "someone else already seeded it" rather than crash.
/// </summary>
public sealed class StartupSeedRaceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_concurrent_duplicate_org_file_insert_surfaces_as_a_unique_violation()
    {
        // A fresh org with no files yet, so both writers stage the full set.
        int orgId;
        await using (var seed = _db.NewContext())
        {
            var org = new Organization
            {
                Name = "Race",
                Slug = "race-" + Guid.NewGuid().ToString("N")[..8],
                IsPending = false,
                CreatedAt = DateTime.UtcNow,
            };
            seed.Organizations.Add(org);
            await seed.SaveChangesAsync();
            orgId = org.Id;
        }

        // The losing boot reads the empty file list and stages its inserts...
        await using var loser = _db.NewContext();
        await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(loser, orgId, DateTime.UtcNow);

        // ...then the winning boot commits the same rows first.
        await using (var winner = _db.NewContext())
        {
            await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(winner, orgId, DateTime.UtcNow);
            await winner.SaveChangesAsync();
        }

        // The loser's save now collides on IX_organization_files_organization_id_path.
        var act = async () => await loser.SaveChangesAsync();
        var thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        StartupTasks.IsUniqueViolation(thrown).Should().BeTrue(
            "a duplicate (organization_id, path) insert is SQLSTATE 23505, which startup treats as a lost seed race");
    }

    [Fact]
    public void IsUniqueViolation_does_not_swallow_other_database_faults()
    {
        // A foreign-key violation (or any non-23505 fault) is a real error and
        // must propagate, not be mistaken for a benign seed race.
        var fk = new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation);
        StartupTasks.IsUniqueViolation(new DbUpdateException("save failed", fk)).Should().BeFalse();

        // A non-Postgres inner exception likewise isn't a unique violation.
        StartupTasks.IsUniqueViolation(new DbUpdateException("save failed", new InvalidOperationException()))
            .Should().BeFalse();
    }
}
