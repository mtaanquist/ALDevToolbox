namespace ALDevToolbox.Services.Configuration;

/// <summary>
/// Where the AL compiler is provisioned to and which version to install.
/// Read once at startup rather than in the provisioner's constructor (#733).
/// The variable names are the documented deployment interface.
/// </summary>
public sealed record AlCompilerOptions
{
    /// <summary>Install root, backed by the <c>app-altool</c> volume. <c>AL_COMPILER_DIR</c>.</summary>
    public string InstallDirectory { get; init; } = "/var/lib/aldevtoolbox/altool";

    /// <summary>Pin a compiler version instead of taking the newest. <c>AL_COMPILER_VERSION</c>.</summary>
    public string? VersionPin { get; init; }

    /// <summary>A pre-seeded <c>alc</c> for air-gapped installs. <c>AL_COMPILER_PATH</c>.</summary>
    public string? ExplicitAlcPath { get; init; }

    public static AlCompilerOptions FromConfiguration(IConfiguration configuration)
    {
        var defaults = new AlCompilerOptions();
        return new AlCompilerOptions
        {
            InstallDirectory = Blank(configuration["AL_COMPILER_DIR"]) ?? defaults.InstallDirectory,
            VersionPin = Blank(configuration["AL_COMPILER_VERSION"]),
            ExplicitAlcPath = Blank(configuration["AL_COMPILER_PATH"]),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
