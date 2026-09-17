using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// #767: an enum property that carries a store default must also declare a
/// sentinel that is not one of its members.
///
/// <para>EF treats a property whose value equals its sentinel as "not set" on
/// insert and lets the store default answer for it. The sentinel defaults to the
/// CLR default, which for an enum is 0 — so whichever member happens to be zero
/// cannot be chosen explicitly, and a save that picks it silently stores the
/// store default instead. Where both are the same member that is invisible;
/// it becomes a real bug the moment either one moves, and it is already a real
/// bug wherever they differ.</para>
///
/// <para>An out-of-range sentinel makes every member a choice EF writes out, so
/// the store default goes back to being what it is for: backfilling rows that
/// predate the column. This test sweeps the whole model rather than naming the
/// four properties #767 found, because the trap is easy to walk back into and
/// says nothing at the call site when you do.</para>
/// </summary>
public sealed class EnumDefaultSentinelTests
{
    [Fact]
    public void Every_enum_property_with_a_store_default_uses_a_sentinel_outside_the_enum()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        var offenders = new List<string>();
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                // The annotation, not GetDefaultValue(): the latter reports the
                // CLR default for a required property that was never given a
                // store default, and those are not at risk - nothing overwrites
                // an explicit choice when there is nothing to fall back to.
                if (property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is null) continue;
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!clrType.IsEnum) continue;

                // A sentinel EF can never confuse with a choice is one that is
                // not a defined member. Null means "no sentinel configured", so
                // it falls back to the CLR default, which always is one.
                var sentinel = property.Sentinel;
                if (sentinel is not null && !Enum.IsDefined(clrType, sentinel)) continue;

                offenders.Add(
                    $"{entity.GetTableName()}.{property.GetColumnName()} ({clrType.Name}) "
                    + $"defaults to {property.GetDefaultValue()} with sentinel "
                    + $"{sentinel?.ToString() ?? "(none, so the CLR default)"}");
            }
        }

        offenders.Should().BeEmpty(
            "each of these has a store default that EF will apply over an explicitly chosen member; "
            + "add .HasSentinel((TheEnum)(-1)) next to the .HasDefaultValue(...) call");
    }
}
