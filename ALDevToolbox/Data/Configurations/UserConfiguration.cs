using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("users");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
        entity.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired();
        entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        entity.Property(e => e.IsSiteAdmin).HasColumnName("is_site_admin").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
        entity.HasIndex(e => new { e.OrganizationId, e.Email }).IsUnique();
        entity.HasIndex(e => e.Email);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
