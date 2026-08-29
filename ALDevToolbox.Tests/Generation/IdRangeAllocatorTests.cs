using ALDevToolbox.Services.Generation;
using ALDevToolbox.Tests.Builders;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Covers <see cref="IdRangeAllocator"/>, extracted from
/// <c>GenerationService</c> in #546 so the New Workspace preview can show the
/// ranges it is about to allocate instead of replaying the walk itself.
///
/// <para>The end-to-end generation tests already read these numbers back out of
/// generated <c>app.json</c> files, and they are the real guarantee that the
/// extraction changed nothing. What they cannot check is the part the extraction
/// created: that the allocator's <b>order</b> is the order the two emit loops
/// walk, because <c>BuildExtensionList</c> now consumes the result by index. An
/// off-by-one there would hand every extension its neighbour's ids and still
/// produce a perfectly valid workspace.</para>
/// </summary>
public sealed class IdRangeAllocatorTests
{
    private const int CoreFrom = 90000;
    private const int CoreTo = 90999;

    [Fact]
    public void The_first_unannotated_extension_takes_the_plans_core_range()
    {
        var template = TemplateBuilder.Default("runtime-x");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);

        ranges.Should().NotBeEmpty();
        ranges[0].From.Should().Be(CoreFrom);
        ranges[0].To.Should().Be(CoreTo);
    }

    [Fact]
    public void Modules_slice_forward_from_the_templates_module_start_and_honour_a_size_override()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var wide = ModuleBuilder.Default("wide", "Wide", idRangeSize: 500);
        var follow = ModuleBuilder.Default("follow", "Follow");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { wide, follow }, CoreFrom, CoreTo);

        var wideRange = ranges.Single(r => r.Path == wide.ExtensionName);
        var followRange = ranges.Single(r => r.Path == follow.ExtensionName);

        wideRange.From.Should().Be(template.ModuleIdRangeStart);
        wideRange.To.Should().Be(template.ModuleIdRangeStart + 499);
        followRange.From.Should().Be(wideRange.To + 1,
            "each module starts where the previous one ended - the cursor is the whole point");
    }

    /// <summary>
    /// The contract <c>BuildExtensionList</c> now depends on: allocation order
    /// is emit order. It consumes the list by index against the same two loops,
    /// so a reordering here would silently swap ranges between extensions.
    /// </summary>
    [Fact]
    public void Allocation_order_is_template_extensions_then_modules()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, CoreFrom, CoreTo);

        var templatePaths = template.WorkspaceExtensions
            .Where(e => e.Required)
            .OrderBy(e => e.Ordering)
            .Select(e => e.Path)
            .ToList();

        ranges.Select(r => r.Path).Should().Equal(
            templatePaths.Append(module.ExtensionName),
            "GenerationService reads this list by index against the same two loops");
    }

    [Fact]
    public void An_unticked_optional_extension_gets_no_range_and_consumes_no_cursor()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var optional = template.WorkspaceExtensions.FirstOrDefault(e => !e.Required);
        if (optional is null) return;   // the default fixture is all-required

        var without = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);
        var with = IdRangeAllocator.Allocate(
            template, new[] { optional.Path }, Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);

        without.Should().NotContain(r => r.Path == optional.Path);
        with.Should().Contain(r => r.Path == optional.Path);
    }

    /// <summary>
    /// The label is what reaches the preview row, and the handoff's generator
    /// screen writes it as <c>ID from-to</c>. Pinned because it is the one part
    /// of this type a reader sees.
    /// </summary>
    [Fact]
    public void A_range_reads_as_the_handoff_writes_it()
    {
        new AllocatedIdRange("Core", 50100, 50199).Label.Should().Be("ID 50100-50199");
    }
}
