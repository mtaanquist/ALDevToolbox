namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One Release-import operation in its entirety: the label / kind / parent the
/// admin chose on the upload form, plus the list of <c>.app</c> files to ingest
/// into the new <see cref="Domain.Entities.ObjectExplorer.Release"/>. Each
/// upload optionally carries a paired <c>.Source.zip</c> for when the <c>.app</c>
/// doesn't embed source.
/// </summary>
public sealed record ReleaseImportRequest(
    string Label,
    string Kind,
    int? ParentReleaseId,
    int? ApplicationVersionId,
    IReadOnlyList<AppFileUpload> Uploads,
    string? Publisher = null,
    string? CustomerName = null);

/// <summary>
/// One <c>.app</c> file + optional paired source zip. Streams are owned by the
/// caller; the service reads them to completion but does not close them.
///
/// The three flag fields default to <c>false</c> so the per-file upload path
/// (where admins pick individual .apps without any path context) stays
/// backward compatible. The folder-ZIP upload path infers them from the
/// surrounding folder + filename conventions before construction.
/// </summary>
public sealed record AppFileUpload(
    string FileName,
    Stream AppStream,
    Stream? SourceZipStream,
    bool IsTest = false,
    bool IsInternal = false,
    bool IsLanguagePack = false);

/// <summary>
/// Summary statistics from an import run — surfaced on the admin UI's
/// completion screen (PR 5) and exposed to tests so they can pin the exact
/// counts without re-querying the database.
/// </summary>
public sealed record ReleaseImportSummary(
    int ReleaseId,
    int ModulesImported,
    int ModulesSkipped,
    int ObjectsImported,
    int ReferencesImported,
    int SourceFilesImported,
    int TranslationsImported);
