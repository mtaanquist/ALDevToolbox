using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;

namespace ALDevToolbox.Startup;

/// <summary>
/// The command palette's search backbone. See
/// <c>.design/command-palette.md</c>.
/// </summary>
public static class PaletteRegistration
{
    /// <summary>
    /// Registers the search service and its sources.
    ///
    /// <para><b>Adding a source is one class and one line here.</b> Order the
    /// line however you like — <see cref="PaletteSearchService"/> sorts by
    /// <see cref="IPaletteSource.Order"/>, so registration order never decides
    /// what a user sees. Scoped, because a source reads through the request's
    /// <c>DbContext</c> and the caller's organisation context.</para>
    ///
    /// <para>With no source registered the endpoint still works and answers empty
    /// groups, which is how the backbone shipped before its sources did.</para>
    /// </summary>
    public static IServiceCollection AddPalette(this IServiceCollection services)
    {
        services.AddScoped<PaletteSearchService>();
        services.AddScoped<IPaletteSource, SolutionPaletteSource>();
        services.AddScoped<IPaletteSource, EnvironmentPaletteSource>();
        services.AddScoped<IPaletteSource, RecipePaletteSource>();
        services.AddScoped<IPaletteSource, ReleasePaletteSource>();
        return services;
    }
}
