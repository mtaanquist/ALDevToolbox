using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace ALDevToolbox.Data.Configurations.OAuth;

/// <summary>
/// Snake_case for OpenIddict's <c>oauth_scopes</c> table. ALDevToolbox
/// only declares two scopes at startup (<c>mcp</c> and <c>offline_access</c>),
/// but using the table — rather than inlining the scope list in code — keeps
/// the door open for future per-org scope customisation without a migration.
/// </summary>
internal sealed class OAuthScopeConfiguration : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreScope>
{
    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreScope> entity)
    {
        entity.ToTable("oauth_scopes");
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.ConcurrencyToken).HasColumnName("concurrency_token");
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.Descriptions).HasColumnName("descriptions");
        entity.Property(e => e.DisplayName).HasColumnName("display_name");
        entity.Property(e => e.DisplayNames).HasColumnName("display_names");
        entity.Property(e => e.Name).HasColumnName("name");
        entity.Property(e => e.Properties).HasColumnName("properties");
        entity.Property(e => e.Resources).HasColumnName("resources");
    }
}
