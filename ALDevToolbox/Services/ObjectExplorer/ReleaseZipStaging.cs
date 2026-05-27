using System.IO.Compression;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Turns a ZIP staged on disk (an uploaded <c>applications/</c> folder, or a
/// downloaded BC DVD) into the <see cref="AppFileUpload"/> list the importer
/// consumes. Shared by the background <see cref="ReleaseImportWorker"/> and the
/// synchronous amend endpoint so both pair <c>.app</c> + <c>.Source.zip</c> the
/// same way.
/// </summary>
public static class ReleaseZipStaging
{
    /// <summary>
    /// Opens <paramref name="tempZipPath"/> and builds one upload per app.
    /// <paramref name="isDvd"/> picks the DVD-subset walk (Applications/ +
    /// System.app, test apps dropped) over the whole-archive walk. The returned
    /// <see cref="ZipArchive"/> owns the entry streams added to
    /// <paramref name="openedStreams"/>; the caller disposes both and deletes
    /// the temp file once the importer has finished reading.
    /// </summary>
    public static (List<AppFileUpload> Uploads, ZipArchive Archive) OpenStagedZip(
        string tempZipPath, bool isDvd, List<Stream> openedStreams)
    {
        var archive = new ZipArchive(File.OpenRead(tempZipPath), ZipArchiveMode.Read);
        var entries = isDvd ? FolderZipWalker.WalkDvd(archive) : FolderZipWalker.Walk(archive);

        var uploads = new List<AppFileUpload>(entries.Count);
        foreach (var entry in entries)
        {
            var appStream = entry.AppEntry.Open();
            openedStreams.Add(appStream);

            Stream? sourceStream = null;
            if (entry.SourceZipEntry is not null)
            {
                sourceStream = entry.SourceZipEntry.Open();
                openedStreams.Add(sourceStream);
            }

            uploads.Add(new AppFileUpload(
                FileName: entry.FileName,
                AppStream: appStream,
                SourceZipStream: sourceStream,
                IsTest: entry.IsTest,
                IsInternal: entry.IsInternal,
                IsLanguagePack: entry.IsLanguagePack));
        }
        return (uploads, archive);
    }

    /// <summary>
    /// Builds a short human description of where <c>.app</c> files actually sit
    /// in the archive, for the "no apps found" failure message — so an
    /// unrecognised DVD layout tells us the folder name to add to
    /// <c>FolderZipWalker.DvdAppFolderNames</c> instead of failing opaquely.
    /// Reports the distinct top-level path segments of every <c>.app</c> entry.
    /// </summary>
    public static string DescribeAppLocations(ZipArchive archive)
    {
        var topFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawApp = false;
        foreach (var entry in archive.Entries)
        {
            // Normalise backslash separators (some DVD ZIPs use them) before
            // taking the top segment.
            var full = entry.FullName.Replace('\\', '/');
            if (!full.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            sawApp = true;
            var slash = full.IndexOf('/');
            topFolders.Add(slash > 0 ? full[..slash] : "(archive root)");
        }

        if (!sawApp) return "the archive contains no .app files at all";
        return "the .app files are under: " + string.Join(", ", topFolders);
    }
}
