using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace ALDevToolbox.Data.Configurations.OAuth;

/// <summary>
/// Forces snake_case for OpenIddict's <c>oauth_applications</c> table so it
/// matches the naming convention used everywhere else in the schema. Runs
/// after <c>modelBuilder.UseOpenIddict()</c> registers the default model.
///
/// <para>
/// Org attribution (<c>created_by_user_id</c>, <c>org_id</c>) is stored in
/// OpenIddict's free-form <c>Properties</c> JSON column rather than dedicated
/// columns — the OpenIddict manager already round-trips that field, and the
/// admin/site-admin client list reads it via <c>IOpenIddictApplicationManager</c>.
/// </para>
/// </summary>
internal sealed class OAuthApplicationConfiguration : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreApplication>
{
    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreApplication> entity)
    {
        entity.ToTable("oauth_applications");
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.ApplicationType).HasColumnName("application_type");
        entity.Property(e => e.ClientId).HasColumnName("client_id");
        entity.Property(e => e.ClientSecret).HasColumnName("client_secret");
        entity.Property(e => e.ClientType).HasColumnName("client_type");
        entity.Property(e => e.ConcurrencyToken).HasColumnName("concurrency_token");
        entity.Property(e => e.ConsentType).HasColumnName("consent_type");
        entity.Property(e => e.DisplayName).HasColumnName("display_name");
        entity.Property(e => e.DisplayNames).HasColumnName("display_names");
        entity.Property(e => e.JsonWebKeySet).HasColumnName("json_web_key_set");
        entity.Property(e => e.Permissions).HasColumnName("permissions");
        entity.Property(e => e.PostLogoutRedirectUris).HasColumnName("post_logout_redirect_uris");
        entity.Property(e => e.Properties).HasColumnName("properties");
        entity.Property(e => e.RedirectUris).HasColumnName("redirect_uris");
        entity.Property(e => e.Requirements).HasColumnName("requirements");
        entity.Property(e => e.Settings).HasColumnName("settings");
    }
}
