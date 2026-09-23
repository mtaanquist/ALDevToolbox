namespace ALDevToolbox.Services.Configuration;

/// <summary>
/// Where a project build looks up the dependency symbols it was not handed, and
/// where it keeps what it fetched. Read once at startup, in the
/// <see cref="AlCompilerOptions"/> style; the variable names are the deployment
/// interface. See <c>.design/object-explorer-project-builds.md</c>, "Resolve symbols".
/// </summary>
public sealed record AlSymbolFeedOptions
{
    /// <summary>Microsoft's public, anonymous symbols-only feed for AppSource apps.</summary>
    public const string DefaultAppSourceFeed =
        "https://dynamicssmb2.pkgs.visualstudio.com/DynamicsBCPublicFeeds/_packaging/AppSourceSymbols/nuget/v3/index.json";

    /// <summary>Microsoft's first-party symbols feed, for Microsoft apps the resolved artifact does not carry.</summary>
    public const string DefaultMicrosoftFeed =
        "https://dynamicssmb2.pkgs.visualstudio.com/DynamicsBCPublicFeeds/_packaging/MSSymbols/nuget/v3/index.json";

    /// <summary>The AppSource feed's NuGet v3 service index, tried first. <c>AL_SYMBOLS_APPSOURCE_FEED</c>.</summary>
    public string AppSourceFeedUrl { get; init; } = DefaultAppSourceFeed;

    /// <summary>The Microsoft feed's NuGet v3 service index, tried second. <c>AL_SYMBOLS_MICROSOFT_FEED</c>.</summary>
    public string MicrosoftFeedUrl { get; init; } = DefaultMicrosoftFeed;

    /// <summary>
    /// Where fetched packages are kept between builds: a <c>symbol-cache</c>
    /// folder inside the compiler's install root, so it rides on the same
    /// <c>app-altool</c> volume (<c>AL_COMPILER_DIR</c>) and needs no new one.
    /// </summary>
    public string CacheDirectory { get; init; } = CacheUnder(new AlCompilerOptions().InstallDirectory);

    public static AlSymbolFeedOptions FromConfiguration(IConfiguration configuration) => new()
    {
        AppSourceFeedUrl = Blank(configuration["AL_SYMBOLS_APPSOURCE_FEED"]) ?? DefaultAppSourceFeed,
        MicrosoftFeedUrl = Blank(configuration["AL_SYMBOLS_MICROSOFT_FEED"]) ?? DefaultMicrosoftFeed,
        CacheDirectory = CacheUnder(AlCompilerOptions.FromConfiguration(configuration).InstallDirectory),
    };

    private static string CacheUnder(string compilerInstallDirectory) =>
        Path.Combine(compilerInstallDirectory, "symbol-cache");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
