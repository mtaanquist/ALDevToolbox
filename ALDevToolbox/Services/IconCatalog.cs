using System.Reflection;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services;

/// <summary>
/// Loads the vendored Lucide SVG icons (under <c>Resources/Icons/</c>, embedded
/// at build time) once at startup and exposes their inner contents for the
/// first-party <see cref="Components.Shared.Icon"/> component to splice into a
/// host <c>&lt;svg&gt;</c> element.
///
/// Vendored as a deliberate replacement for the unmaintained
/// <c>Lucide.Blazor</c> package (issue #47): a missing icon name returns
/// <see langword="null"/> and logs a warning rather than throwing
/// <see cref="KeyNotFoundException"/> during render.
/// </summary>
public sealed partial class IconCatalog
{
    private readonly Dictionary<string, string> _innerByName;
    private readonly ILogger<IconCatalog> _logger;

    public IconCatalog(ILogger<IconCatalog> logger)
    {
        _logger = logger;
        _innerByName = LoadFromAssembly(typeof(IconCatalog).Assembly);
        _logger.LogInformation("Loaded {Count} vendored Lucide icons.", _innerByName.Count);
    }

    /// <summary>Inner SVG markup (children of the root <c>&lt;svg&gt;</c>), or
    /// <see langword="null"/> if no icon with that name was vendored. The
    /// missing-icon case is logged as a warning so it shows up in the server
    /// log instead of crashing the render path.</summary>
    public string? GetInnerSvg(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_innerByName.TryGetValue(name, out var inner)) return inner;

        _logger.LogWarning("Missing icon: {IconName}. Drop the SVG into Resources/Icons/ at the pinned Lucide version (see VERSION.txt).", name);
        return null;
    }

    /// <summary>Every icon name available in the catalogue. Stable for the
    /// lifetime of the application — the catalogue is built once at startup.</summary>
    public IReadOnlyCollection<string> Names => _innerByName.Keys;

    private static Dictionary<string, string> LoadFromAssembly(Assembly assembly)
    {
        const string prefix = "ALDevToolbox.Resources.Icons.";
        const string suffix = ".svg";
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!resourceName.EndsWith(suffix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var match = InnerSvgRegex().Match(svg);
            if (!match.Success) continue;

            // Resource name is e.g. "ALDevToolbox.Resources.Icons.users-round.svg" — but dotted
            // logical names lose the hyphen distinction in nested folders. We embed icons under
            // a single folder, so the segment between prefix and suffix is the icon name itself
            // (kebab-case preserved because there are no further dots).
            var iconName = resourceName.Substring(prefix.Length, resourceName.Length - prefix.Length - suffix.Length);
            catalog[iconName] = match.Groups["inner"].Value.Trim();
        }

        return catalog;
    }

    [GeneratedRegex("<svg[^>]*>(?<inner>.*?)</svg>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InnerSvgRegex();
}
