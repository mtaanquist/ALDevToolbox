using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// Schema-level invariants from issue #74: the audit chain refuses cascading
/// deletes, two concurrent pending signups for the same (org, email) can't
/// both survive, and the filter is lifted once a request is decided.
/// </summary>
public sealed class DataIntegrityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_second_pending_signup_for_the_same_org_and_email_fails()
    {
        await using var ctx = _db.NewContext();
        ctx.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "race@example.com",
            RequestedAt = DateTime.UtcNow,
            Decision = SignupDecision.Pending,
        });
        await ctx.SaveChangesAsync();

        ctx.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "race@example.com",
            RequestedAt = DateTime.UtcNow,
            Decision = SignupDecision.Pending,
        });

        Func<Task> save = () => ctx.SaveChangesAsync();
        var ex = await save.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Approved_and_rejected_rows_do_not_collide_with_the_unique_pending_index()
    {
        // The filter is `decision = 'Pending'`, so a decided row for the same
        // (org, email) must not block a new pending request — admins
        // legitimately re-invite a user whose previous request was rejected.
        await using var ctx = _db.NewContext();
        ctx.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "again@example.com",
            RequestedAt = DateTime.UtcNow,
            Decision = SignupDecision.Rejected,
            DecidedAt = DateTime.UtcNow,
        });
        ctx.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "again@example.com",
            RequestedAt = DateTime.UtcNow,
            Decision = SignupDecision.Pending,
        });
        Func<Task> save = () => ctx.SaveChangesAsync();
        await save.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_system_settings_row_with_an_id_other_than_one_is_refused()
    {
        // The singleton invariant is enforced by CHECK (id = 1), not just by
        // convention that SystemSettingsService is the only writer. #402
        await using var ctx = _db.NewContext();
        ctx.Add(new SystemSettings { Id = 2, UpdatedAt = DateTime.UtcNow });

        Func<Task> save = () => ctx.SaveChangesAsync();
        var ex = await save.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23514"); // check_violation
    }

    [Fact]
    public async Task Deleting_an_organisation_with_audit_rows_is_refused()
    {
        // The audit chain is durable: organisation rows can't be hard-deleted
        // while their audit history references them. The previous SetNull
        // cascade silently wiped subject and actor from every row at once.
        int orgId;
        await using (var ctx = _db.NewContext())
        {
            var org = new Organization
            {
                Name = "Deletable",
                Slug = "deletable",
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Organizations.Add(org);
            await ctx.SaveChangesAsync();
            orgId = org.Id;

            ctx.AuditLog.Add(new AuditLogEntry
            {
                Timestamp = DateTime.UtcNow,
                ChangedBy = "tester",
                OrganizationId = orgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Created,
            });
            await ctx.SaveChangesAsync();
        }

        await using var del = _db.NewContext();
        var toDelete = await del.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == orgId);
        del.Organizations.Remove(toDelete);
        Func<Task> save = () => del.SaveChangesAsync();
        var ex = await save.Should().ThrowAsync<DbUpdateException>();
        // Postgres surfaces ON DELETE RESTRICT as 23001 (restrict_violation),
        // not the more general 23503 (foreign_key_violation). Accept either —
        // the contract is "the delete is refused", not the specific sql state.
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().BeOneOf("23001", "23503");
    }
}
