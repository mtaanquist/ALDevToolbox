using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace ALDevToolbox.Data.Configurations.OAuth;

/// <summary>
/// Snake_case for OpenIddict's <c>oauth_authorizations</c> table. Each row is
/// a short-lived grant ticket (authorization-code or implicit), not a
/// long-lived "user trusts this app" record — that's what
/// <see cref="ALDevToolbox.Domain.Entities.OAuthConsent"/> is for.
/// </summary>
internal sealed class OAuthAuthorizationConfiguration : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreAuthorization>
{
    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreAuthorization> entity)
    {
        entity.ToTable("oauth_authorizations");
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.ConcurrencyToken).HasColumnName("concurrency_token");
        entity.Property(e => e.CreationDate).HasColumnName("creation_date");
        entity.Property(e => e.Properties).HasColumnName("properties");
        entity.Property(e => e.Scopes).HasColumnName("scopes");
        entity.Property(e => e.Status).HasColumnName("status");
        entity.Property(e => e.Subject).HasColumnName("subject");
        entity.Property(e => e.Type).HasColumnName("type");

        // The Application nav comes from UseOpenIddict() with a shadow
        // "ApplicationId" FK column; rename that shadow property in place
        // rather than declaring a new HasForeignKey, which would otherwise
        // create a second shadow column.
        entity.Property<string>("ApplicationId").HasColumnName("application_id");
    }
}
