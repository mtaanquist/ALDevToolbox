namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Where the repository is on disk, for the tests that read the app's own source -
/// stylesheets, icons, <c>.razor</c> files, migrations. Found by walking up from the test
/// binaries to the folder that holds the solution file, which works the same under
/// <c>dotnet test</c>, an IDE runner and CI.
/// <para>
/// The one place the landmark's name is written down. It used to be copied into some forty
/// tests, which is what made renaming the solution file look like a forty-file change
/// (it still says <c>ALDevToolbox</c> on purpose - see the naming table in CLAUDE.md).
/// </para>
/// </summary>
internal static class RepoRoot
{
    /// <summary>The file whose presence marks the repository root.</summary>
    public const string Marker = "ALDevToolbox.slnx";

    private static readonly Lazy<DirectoryInfo> Found = new(Find);

    /// <summary>The repository root. Throws, naming the landmark, when it cannot be found.</summary>
    public static DirectoryInfo Directory => Found.Value;

    /// <summary>A path under the repository root.</summary>
    public static string Combine(params string[] parts) =>
        Path.Combine([Directory.FullName, .. parts]);

    private static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Marker)))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException(
            $"Could not locate the repository root: no {Marker} above {AppContext.BaseDirectory}.");
    }
}
