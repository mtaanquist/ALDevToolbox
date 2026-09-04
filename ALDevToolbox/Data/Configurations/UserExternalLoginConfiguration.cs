using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
{
    public void Configure(EntityTypeBuilder<UserExternalLogin> entity)
    {
        entity.ToTable("user_external_logins");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.Provider).HasColumnName("provider").IsRequired().HasMaxLength(32);
        entity.Property(e => e.Issuer).HasColumnName("issuer").IsRequired().HasMaxLength(64);
        entity.Property(e => e.Subject).HasColumnName("subject").IsRequired().HasMaxLength(128);
        entity.Property(e => e.DisplayIdentity).HasColumnName("display_identity").IsRequired().HasMaxLength(254);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");

        // GitHub account links only (issue #621). Nullable throughout because the
        // Entra rows that shared this table first hold no token of ours.
        entity.Property(e => e.AccessTokenEncrypted).HasColumnName("access_token_encrypted");
        entity.Property(e => e.RefreshTokenEncrypted).HasColumnName("refresh_token_encrypted");
        entity.Property(e => e.AccessTokenExpiresAt).HasColumnName("access_token_expires_at");
        entity.Property(e => e.IsOrgMember).HasColumnName("is_org_member");

        // An external identity maps to exactly one local user.
        entity.HasIndex(e => new { e.Provider, e.Issuer, e.Subject }).IsUnique();
        entity.HasIndex(e => e.UserId);

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating (nav-based,
        // via User.OrganizationId) — see the note there.
    }
}
