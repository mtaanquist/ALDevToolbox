using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class UserPasskeyConfiguration : IEntityTypeConfiguration<UserPasskey>
{
    public void Configure(EntityTypeBuilder<UserPasskey> entity)
    {
        entity.ToTable("user_passkeys");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.CredentialId).HasColumnName("credential_id").IsRequired();
        entity.Property(e => e.PublicKey).HasColumnName("public_key").IsRequired();
        entity.Property(e => e.SignCounter).HasColumnName("sign_counter").IsRequired();
        entity.Property(e => e.Transports).HasColumnName("transports").IsRequired();
        entity.Property(e => e.Aaguid).HasColumnName("aaguid");
        entity.Property(e => e.Nickname).HasColumnName("nickname").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
        entity.HasIndex(e => e.CredentialId).IsUnique();
        entity.HasIndex(e => e.UserId);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
