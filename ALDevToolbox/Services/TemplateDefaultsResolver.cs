using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services;

/// <summary>
/// Pure helpers shared by the two project-creation pages
/// (<c>Components/Pages/NewWorkspace.razor</c> and
/// <c>Components/Pages/NewExtension.razor</c>) for deriving
/// application/runtime defaults from a picked template.
/// <para>
/// Static because there is no DB access, no async, and no per-request
/// state — the codebase's <c>Services/</c> = "scoped DI service" pattern
/// (CLAUDE.md §Services) doesn't fit. Lives next to <c>PiperTransform</c>
/// as the second small static helper; both follow the same shape.
/// </para>
/// </summary>
public static class TemplateDefaultsResolver
{
    /// <summary>
    /// Picks the <c>(application, runtime)</c> pair the form should snap
    /// to when a user selects a template. Prefers the curated
    /// <see cref="ApplicationVersion"/> catalogue (Milestone P2.4) when
    /// the template points at a live entry; falls back to the template's
    /// free-text <c>default_application</c> + <c>runtime</c> otherwise.
    /// <para>
    /// Runtime is normalised to <c>Major.Minor</c> in the fallback path —
    /// <c>app.json</c> rejects a bare major. The curated path is already
    /// well-formed because the catalogue stores both shapes explicitly.
    /// </para>
    /// </summary>
    public static (string Application, string Runtime) ResolveApplicationAndRuntime(
        RuntimeTemplate template,
        IReadOnlyList<ApplicationVersion>? applicationVersions)
    {
        var curated = ResolveCuratedVersion(template, applicationVersions);
        if (curated is not null)
        {
            return (curated.Application, curated.Runtime);
        }

        return (template.Defaults.Application, NormalizeRuntime(template.Runtime));
    }

    /// <summary>
    /// Returns the curated <see cref="ApplicationVersion"/> the template
    /// should preselect, or <c>null</c> when the template has no FK or
    /// the referenced entry is no longer in the active catalogue
    /// (soft-deleted, deprecated, or filtered out).
    /// </summary>
    public static ApplicationVersion? ResolveCuratedVersion(
        RuntimeTemplate template,
        IReadOnlyList<ApplicationVersion>? applicationVersions)
    {
        if (template.DefaultApplicationVersion is null || applicationVersions is null) return null;
        var key = template.DefaultApplicationVersion.Key;
        return applicationVersions.FirstOrDefault(v => v.Key == key);
    }

    // Major-only ("15") → "15.0". Anything already containing "." is kept.
    // Private because the only caller is the fallback branch above; tests
    // pin the rule through ResolveApplicationAndRuntime's contract.
    private static string NormalizeRuntime(string runtime)
        => runtime.Contains('.') ? runtime : $"{runtime}.0";
}
