using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserRepositoryTokenConfiguration : IEntityTypeConfiguration<UserRepositoryToken>
{
    public void Configure(EntityTypeBuilder<UserRepositoryToken> entity)
    {
        entity.ToTable("user_repository_tokens");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Provider)
            .HasColumnName("provider")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(
                p => p.ToDiscriminator(),
                s => RepositoryProviders.FromDiscriminatorStrict(s));
        entity.Property(e => e.TokenEncrypted).HasColumnName("token_encrypted").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");

        // One token per user per provider per org — the upsert key.
        entity.HasIndex(e => new { e.UserId, e.OrganizationId, e.Provider }).IsUnique();

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating via
        // ScopeToOrganization<UserRepositoryToken>.
    }
}
