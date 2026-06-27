namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One downloadable deliverable from a <see cref="ProjectBuild"/> — a compiled
/// <c>.app</c> whose bytes are <em>retained</em> (unlike the old flow, where
/// uploads streamed into ingest and weren't kept). <c>*.dep.app</c> packaging
/// artifacts are excluded at build time, so they never appear as a download.
/// Served by <c>ArtifactEndpoints</c>. See <c>.design/artifacts.md</c>.
/// </summary>
public class ProjectBuildArtifact
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the build). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectBuildId { get; set; }
    public ProjectBuild? ProjectBuild { get; set; }

    /// <summary>The deliverable's file name, e.g. <c>CRONUS_My Extension_1.2.3.0.app</c>.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The extension's app.json <c>name</c> (display label).</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>The extension's app.json <c>version</c>.</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>The extension's app.json <c>runtime</c>, when declared; null otherwise.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>Size of <see cref="Content"/> in bytes (denormalised so listings don't read the blob).</summary>
    public long SizeBytes { get; set; }

    /// <summary>The compiled <c>.app</c> bytes. Loaded only by the download endpoint, never in listings.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; }
}
