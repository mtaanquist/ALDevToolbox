using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class EnvironmentUpgradeActionConfiguration : IEntityTypeConfiguration<EnvironmentUpgradeAction>
{
    public void Configure(EntityTypeBuilder<EnvironmentUpgradeAction> entity)
    {
        entity.ToTable("oe_environment_upgrade_actions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.EnvironmentId).HasColumnName("environment_id").IsRequired();

        // Text, not an int: the column reads plainly in psql and a third kind or a new
        // status never renumbers the rows already written. Same choice as Visibility.
        entity.Property(e => e.Kind).HasColumnName("kind")
            .HasConversion<string>().HasMaxLength(40).IsRequired();
        entity.Property(e => e.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        entity.Property(e => e.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(e => e.RequestedBy).HasColumnName("requested_by").HasMaxLength(320).IsRequired();
        entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
        entity.Property(e => e.ExecuteAfter).HasColumnName("execute_after").IsRequired();
        entity.Property(e => e.SentAt).HasColumnName("sent_at");
        entity.Property(e => e.Outcome).HasColumnName("outcome");
        entity.Property(e => e.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by").HasMaxLength(320);
        entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");

        // No concurrency-token column: see the entity's remarks. The one race on this
        // table — the worker claiming a row while somebody cancels it — is decided by a
        // conditional ExecuteUpdate, the pattern the rest of the codebase already uses.

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The history rides the customer's lifecycle: deleting the project for good
        // takes its upgrade history with it, like builds and deliveries.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Environment)
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both actors are SetNull: the feed must still say what happened after the
        // person who did it leaves, which is what the denormalised names are for.
        entity.HasOne(e => e.RequestedByUser)
            .WithMany()
            .HasForeignKey(e => e.RequestedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.CancelledByUser)
            .WithMany()
            .HasForeignKey(e => e.CancelledByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The activity feed: one environment's actions, newest first.
        entity.HasIndex(e => new { e.EnvironmentId, e.RequestedAt })
            .HasDatabaseName("ix_oe_env_upgrade_actions_env_requested");
        // The worker's due-scan (status-scoped).
        entity.HasIndex(e => new { e.Status, e.ExecuteAfter })
            .HasDatabaseName("ix_oe_env_upgrade_actions_status_due");
    }
}
