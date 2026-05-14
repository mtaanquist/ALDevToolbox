using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SignupRequestConfiguration : IEntityTypeConfiguration<SignupRequest>
{
    private readonly IOrganizationContext _orgContext;
    public SignupRequestConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<SignupRequest> entity)
    {
        entity.ToTable("signup_requests");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id");
        entity.Property(e => e.Email).HasColumnName("email").IsRequired();
        entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
        entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
        entity.Property(e => e.DecidedByUserId).HasColumnName("decided_by_user_id");
        entity.Property(e => e.Decision).HasColumnName("decision").HasConversion<string>().IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Decision });
        // Pending signups are unique per (org, email) — the service-level
        // duplicate check loses a concurrency race without this guard.
        entity.HasIndex(e => new { e.OrganizationId, e.Email })
            .IsUnique()
            .HasFilter("decision = 'Pending'")
            .HasDatabaseName("ux_signup_requests_org_email_pending");
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        // Defense in depth: pre-login flows (Program.cs decide endpoints)
        // already call IgnoreQueryFilters explicitly; this filter catches
        // any post-login read that forgets to scope by org.
        entity.ScopeToOrganization(_orgContext);
    }
}
