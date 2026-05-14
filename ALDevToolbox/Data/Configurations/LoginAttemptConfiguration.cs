using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> entity)
    {
        entity.ToTable("login_attempts");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.Ip).HasColumnName("ip").IsRequired();
        entity.Property(e => e.Succeeded).HasColumnName("succeeded").IsRequired();
        entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
        entity.HasIndex(e => new { e.Email, e.Timestamp });
        entity.HasIndex(e => new { e.Ip, e.Timestamp });
    }
}
