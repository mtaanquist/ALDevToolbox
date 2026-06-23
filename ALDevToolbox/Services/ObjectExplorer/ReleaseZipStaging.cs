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
        var entries = WalkArchive(archive, isDvd);

        // Nested-DVD wrapper: some older BC downloads (e.g. "Update 15.1
        // Dynamics 365 Business Central 2019 Release Wave 2 DK") wrap the real
        // DVD in an outer ZIP whose only payload is a single nested
        // <Name>.DVD.zip. The outer walk then finds no apps, so descend into
        // the lone nested zip and walk that instead. Bounded so a pathological
        // wrapper-of-wrapper can't loop forever; in practice one level is all
        // Microsoft ships. See GitHub issue #303.
        var descend = 0;
        while (entries.Count == 0 && descend < MaxNestedZipDescend
            && TryFindSoleNestedZip(archive) is { } nested)
        {
            var inner = ExtractNestedZipToSelfDeletingArchive(nested);
            // Done with the wrapper. Disposing it early releases the outer
            // temp file's read handle; the caller still deletes that path in
            // its own cleanup. The inner archive owns a DeleteOnClose temp, so
            // the caller's existing `archive.Dispose()` reclaims it too — no
            // new cleanup plumbing needed at any call site.
            archive.Dispose();
            archive = inner;
            entries = WalkArchive(archive, isDvd);
            descend++;
        }

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
    /// How many times <see cref="OpenStagedZip"/> will descend into a sole
    /// nested zip before giving up. Microsoft only ever wraps the DVD one level
    /// deep; the small ceiling is purely a guard against a pathological
    /// wrapper-of-wrapper chain looping forever.
    /// </summary>
    private const int MaxNestedZipDescend = 4;

    /// <summary>
    /// Picks the walk strategy for an open archive. The DVD path is explicit;
    /// otherwise auto-detect a VS Code AL workspace (folders each holding an
    /// <c>app.json</c>) and scope to each app's own build output, so an admin
    /// can zip a multi-root workspace and upload it through the same box. Falls
    /// back to the flat whole-archive walk for a plain <c>applications/</c>
    /// folder zip (no <c>app.json</c>).
    /// </summary>
    private static IReadOnlyList<FolderZipEntry> WalkArchive(ZipArchive archive, bool isDvd) =>
        isDvd
            ? FolderZipWalker.WalkDvd(archive)
            : FolderZipWalker.LooksLikeWorkspace(archive)
                ? FolderZipWalker.WalkWorkspace(archive)
                : FolderZipWalker.Walk(archive);

    /// <summary>
    /// Finds the single nested <c>.zip</c> to descend into when the outer
    /// archive holds no apps, or <see langword="null"/> when the choice is
    /// ambiguous. Prefers an unambiguous sole zip; failing that, a sole
    /// <c>*.dvd.zip</c> (Microsoft's naming for the wrapped DVD) so a stray
    /// sibling zip alongside it doesn't block the descent. Multiple plausible
    /// candidates stay ambiguous on purpose — the caller's "no apps" diagnostic
    /// then names them so the admin can re-zip with just the DVD inside.
    /// Directory entries and zero-length placeholders are ignored.
    /// </summary>
    private static ZipArchiveEntry? TryFindSoleNestedZip(ZipArchive archive)
    {
        ZipArchiveEntry? soleZip = null;
        ZipArchiveEntry? soleDvdZip = null;
        var zipCount = 0;
        var dvdZipCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length == 0) continue;
            zipCount++;
            soleZip = entry;
            if (name.EndsWith(".dvd.zip", StringComparison.OrdinalIgnoreCase))
            {
                dvdZipCount++;
                soleDvdZip = entry;
            }
        }
        if (zipCount == 1) return soleZip;
        if (dvdZipCount == 1) return soleDvdZip;
        return null;
    }

    /// <summary>
    /// Extracts a nested zip entry to a temp file and returns a read-only
    /// <see cref="ZipArchive"/> over it. <see cref="ZipArchive"/> in read mode
    /// needs a seekable stream, which a compressed entry's
    /// <see cref="ZipArchiveEntry.Open"/> stream isn't, so the inner zip is
    /// spilled to disk first (the DVD can be a GB-plus — too large to buffer in
    /// memory). The temp file is opened <see cref="FileOptions.DeleteOnClose"/>
    /// so the OS reclaims it when the returned archive's stream is disposed,
    /// tying its lifetime to the archive the caller already disposes.
    /// </summary>
    private static ZipArchive ExtractNestedZipToSelfDeletingArchive(ZipArchiveEntry nested)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "oe-nested-" + Guid.NewGuid().ToString("N") + ".zip");
        var fs = new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose,
        });
        try
        {
            using (var source = nested.Open())
            {
                source.CopyTo(fs);
            }
            fs.Position = 0;
            // ZipArchive owns fs (leaveOpen defaults to false), so disposing the
            // archive disposes fs, which deletes the temp file.
            return new ZipArchive(fs, ZipArchiveMode.Read);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
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
        var nestedZips = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawApp = false;
        foreach (var entry in archive.Entries)
        {
            // Normalise backslash separators (some DVD ZIPs use them) before
            // taking the top segment.
            var full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                sawApp = true;
                var slash = full.IndexOf('/');
                topFolders.Add(slash > 0 ? full[..slash] : "(archive root)");
            }
            else if (!full.EndsWith('/') && full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                nestedZips.Add(full);
            }
        }

        if (sawApp) return "the .app files are under: " + string.Join(", ", topFolders);

        // A single nested zip is descended into automatically; reaching here
        // with several means we couldn't pick which one is the DVD, so name
        // them — the admin can re-zip with just the DVD inside.
        if (nestedZips.Count > 0)
        {
            return "the archive contains no .app files, only nested zip(s): "
                + string.Join(", ", nestedZips)
                + " — a single nested zip is opened automatically, but several are ambiguous; "
                + "re-zip with just the DVD inside";
        }
        return "the archive contains no .app files at all";
    }
}
