using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
{
    /// <summary>
    /// The unique index on <c>(provider, issuer, subject)</c>, by name. It is
    /// deployment-wide while this table has no <c>organization_id</c>, so a save
    /// can lose to a row in another organisation that the tenant filter hides
    /// from the pre-check - the linking services name the constraint when they
    /// translate that violation into a message instead of a 500.
    /// </summary>
    internal const string IdentityIndexName = "IX_user_external_logins_provider_issuer_subject";

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
        // Named by convention, and by the migration that created it, as
        // IdentityIndexName above - which is the name the linking services
        // match a violation against.
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
