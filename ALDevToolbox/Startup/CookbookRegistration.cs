using ALDevToolbox.Services;

namespace ALDevToolbox.Startup;

/// <summary>The Cookbook tool: recipes and their suggestions.</summary>
public static class CookbookRegistration
{
    /// <summary>Registers the Cookbook services.</summary>
    public static IServiceCollection AddCookbook(this IServiceCollection services)
    {
        services.AddScoped<RecipeService>();
        services.AddScoped<RecipeSuggestionService>();
        return services;
    }
}
