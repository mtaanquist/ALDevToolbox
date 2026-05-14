using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> entity)
    {
        entity.ToTable("invites");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().IsRequired();
        entity.Property(e => e.WelcomeMessage).HasColumnName("welcome_message");
        entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        entity.Property(e => e.AcceptedAt).HasColumnName("accepted_at");
        entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
        entity.Property(e => e.InvitedByUserId).HasColumnName("invited_by_user_id").IsRequired();
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => new { e.OrganizationId, e.Email });
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.InvitedByUser)
            .WithMany()
            .HasForeignKey(e => e.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        // Query filter installed in AppDbContext.OnModelCreating — see the
        // note there. The filter must reference the DbContext's _orgContext
        // field so EF re-parameterises it per DbContext instance.
    }
}
