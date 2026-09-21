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
    /// <para>The sources are registered below: the endpoint works and answers empty
    /// groups. Solutions, Environments, Releases and Recipes land next.</para>
    /// </summary>
    public static IServiceCollection AddPalette(this IServiceCollection services)
    {
        services.AddScoped<PaletteSearchService>();
        services.AddScoped<IPaletteSource, RecipePaletteSource>();
        services.AddScoped<IPaletteSource, ReleasePaletteSource>();
        return services;
    }
}
