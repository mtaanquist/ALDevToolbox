using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal static class EntityTypeBuilderExtensions
{
    /// <summary>
    /// Installs an EF query filter that scopes reads to the current organisation.
    /// Pre-login flows must call <c>IgnoreQueryFilters()</c> explicitly.
    /// </summary>
    public static void ScopeToOrganization<T>(this EntityTypeBuilder<T> entity, IOrganizationContext orgContext)
        where T : class
    {
        entity.HasQueryFilter(e =>
            EF.Property<int>(e, "OrganizationId") == orgContext.OrganizationIdForFilter);
    }
}
