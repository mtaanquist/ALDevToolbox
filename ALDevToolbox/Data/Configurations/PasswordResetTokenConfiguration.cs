using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> entity)
    {
        entity.ToTable("password_reset_tokens");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
        entity.Property(e => e.Purpose).HasColumnName("purpose").HasConversion<string>().IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
        entity.Property(e => e.FailedAttempts).HasColumnName("failed_attempts").HasDefaultValue(0);
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => e.UserId);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Query filter installed in AppDbContext.OnModelCreating — see the
        // note there. The filter must reference the DbContext's _orgContext
        // field so EF re-parameterises it per DbContext instance.
    }
}
