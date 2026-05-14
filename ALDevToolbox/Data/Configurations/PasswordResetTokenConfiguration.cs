using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    private readonly IOrganizationContext _orgContext;
    public PasswordResetTokenConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

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
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => e.UserId);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Mirror the User principal's org filter so EF's required-nav
        // model-validation passes; pre-login flows already bypass with
        // IgnoreQueryFilters().
        entity.HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
    }
}
