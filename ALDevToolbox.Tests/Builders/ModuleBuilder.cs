using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Tests.Builders;

/// <summary>Helpers for constructing <see cref="Module"/> rows in tests.</summary>
public static class ModuleBuilder
{
    public const int DefaultOrganizationId = 1;

    public static Module Default(string key = "test-module", string name = "Test Module", int? idRangeSize = null, int organizationId = DefaultOrganizationId) => new()
    {
        OrganizationId = organizationId,
        Key = key,
        Name = name,
        IdRangeSize = idRangeSize,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static Module WithDependency(this Module module, string id, string name, string publisher, string version)
    {
        module.Dependencies.Add(new ModuleDependency
        {
            OrganizationId = module.OrganizationId,
            Ordering = module.Dependencies.Count,
            DepId = id,
            DepName = name,
            DepPublisher = publisher,
            DepVersion = version,
        });
        return module;
    }
}
