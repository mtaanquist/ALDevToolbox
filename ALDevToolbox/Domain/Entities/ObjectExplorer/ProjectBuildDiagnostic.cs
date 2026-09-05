namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One compiler message from a <see cref="ProjectBuild"/>, parsed out of the AL
/// compiler's output into a row.
///
/// <para>The build log has always carried the same text, but only as text: a
/// reader could see it and nothing else could. Rows make the two things the
/// compile gate needs possible - counting the errors and warnings a build
/// produced, and drawing each one against its own line of its own file as a
/// check-run annotation on the pull request. Parsed for every build, manual or
/// pull-request, because a count on the build page is worth having either way.
/// See <c>.design/github-integration-phase2.md</c> (#627).</para>
/// </summary>
public class ProjectBuildDiagnostic
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the build). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectBuildId { get; set; }
    public ProjectBuild? ProjectBuild { get; set; }

    /// <summary>The repository the file belongs to, when the clone it came from is still identifiable; null otherwise.</summary>
    public int? ProjectRepositoryId { get; set; }
    public ProjectRepository? ProjectRepository { get; set; }

    /// <summary>
    /// The file, relative to the repository root and with forward slashes. The
    /// compiler emits an absolute path into the build machine's temp directory,
    /// which names nothing GitHub knows about - so the clone root is stripped
    /// before the row is written.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>1-based line the compiler pointed at. 0 when it named no line.</summary>
    public int Line { get; set; }

    /// <summary>1-based column the compiler pointed at. 0 when it named no column.</summary>
    public int Column { get; set; }

    /// <summary>One of <c>error</c>, <c>warning</c> or <c>info</c>. See <see cref="ProjectBuildDiagnosticSeverity"/>.</summary>
    public string Severity { get; set; } = ProjectBuildDiagnosticSeverity.Error;

    /// <summary>The compiler's own code (<c>AL0118</c>, <c>AA0005</c>), or empty when it gave none.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The message text, without the location and code prefix the compiler printed it behind.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Preserves the compiler's own order so the annotations read as the build reported them.</summary>
    public int Ordering { get; set; }
}

/// <summary>The severities the AL compiler reports, as stored.</summary>
public static class ProjectBuildDiagnosticSeverity
{
    /// <summary>Compilation failed because of this. Fails the check run.</summary>
    public const string Error = "error";

    /// <summary>Compilation succeeded despite it. Annotated, never fatal.</summary>
    public const string Warning = "warning";

    /// <summary>Informational - the compiler's <c>info</c> level, and anything else it labels.</summary>
    public const string Info = "info";
}
