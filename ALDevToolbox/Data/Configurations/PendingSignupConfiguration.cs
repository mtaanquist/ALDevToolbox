using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class PendingSignupConfiguration : IEntityTypeConfiguration<PendingSignup>
{
    public void Configure(EntityTypeBuilder<PendingSignup> entity)
    {
        entity.ToTable("pending_signups");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.LinkTokenHash).HasColumnName("link_token_hash").IsRequired();
        entity.Property(e => e.CodeHash).HasColumnName("code_hash").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        entity.Property(e => e.VerifiedAt).HasColumnName("verified_at");
        entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
        entity.HasIndex(e => e.LinkTokenHash).IsUnique();
        // Plain lookup index for the verify-by-code / find-verified / cleanup
        // queries that scan by email (including verified rows the partial index
        // below doesn't cover). Named so EF keeps it distinct from the partial
        // unique index on the same column.
        entity.HasIndex(new[] { nameof(PendingSignup.Email) }, "ix_pending_signups_email");
        // At most one active (unverified, uncompleted) pending signup per email.
        // The service supersedes stale rows on each StartAsync; this partial
        // unique index catches the concurrency race the same way
        // ux_signup_requests_org_email_pending does for SignupRequest.
        entity.HasIndex(e => e.Email)
            .IsUnique()
            .HasFilter("verified_at IS NULL AND completed_at IS NULL")
            .HasDatabaseName("ux_pending_signups_email_active");
        // No organization_id, no query filter: a pre-auth, org-less row read via
        // IgnoreQueryFilters() like invites / password_reset_tokens.
    }
}
