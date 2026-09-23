namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A vendor Release a pipeline-build Release resolved its dependency symbols
/// from (#901, Part 4). The build fetches an AppSource app's symbols package,
/// ingests it once per (app id, version) as a shared <c>third_party</c> Release,
/// and links each project Release that compiled against it here.
///
/// <para>A link, not a second parent: the release chain still walks the single
/// <see cref="OeRelease.ParentReleaseId"/>, and this table adds the linked
/// Releases one step from the seed. Links are followed one level only - a
/// dependency Release's own links are never walked. See
/// <c>.design/object-explorer.md</c> ("Dependency links").</para>
///
/// <para>Carries its own <c>OrganizationId</c> so the standard query filter
/// applies directly. Both ends are always in that organisation - the build
/// writes the link inside its own org scope - and the raw chain SQL rechecks it
/// rather than trusting this row (<c>ReleaseAncestrySql.Chain</c>).</para>
/// </summary>
public class OeReleaseDependency
{
    public int Id { get; set; }

    /// <summary>Owning organisation (the same as both Releases'). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The Release that depends - a pipeline build's project Release.</summary>
    public int ReleaseId { get; set; }
    public OeRelease? Release { get; set; }

    /// <summary>The vendor Release it resolved symbols from.</summary>
    public int DependencyReleaseId { get; set; }
    public OeRelease? DependencyRelease { get; set; }

    public DateTime CreatedAt { get; set; }
}
