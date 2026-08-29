using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> entity)
    {
        entity.ToTable("team_members");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.TeamId).HasColumnName("team_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.IsManager).HasColumnName("is_manager").IsRequired();
        entity.Property(e => e.ManagesUpdates).HasColumnName("manages_updates").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // One row per person per team.
        entity.HasIndex(e => new { e.TeamId, e.UserId }).IsUnique();
        // "Which teams is this person on" — the /teams index and, from slice 2,
        // the visibility snapshot both read by user id.
        entity.HasIndex(e => e.UserId);

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        // The Team side of this FK is configured from TeamConfiguration.HasMany.
        entity.HasOne(e => e.User!)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
