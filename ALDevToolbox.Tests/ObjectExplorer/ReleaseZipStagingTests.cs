using System.IO.Compression;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="ReleaseZipStaging.OpenStagedZip"/>, focused on the
/// nested-DVD wrapper handling: some older BC downloads (e.g. "Update 15.1
/// Dynamics 365 Business Central 2019 Release Wave 2 DK") wrap the real DVD in
/// an outer ZIP whose only payload is a single nested <c>&lt;Name&gt;.DVD.zip</c>.
/// The staging layer descends into that lone nested zip so the importer sees
/// the apps. The walk/selection rules themselves live in
/// <see cref="FolderZipWalker"/> and are covered by their own tests.
/// </summary>
public sealed class ReleaseZipStagingTests
{
    [Fact]
    public void OpenStagedZip_descends_into_a_sole_nested_dvd_zip()
    {
        var inner = BuildZipBytes(
            "BC/Applications/DKCore/Source/Microsoft_DK Core.app",
            "BC/Applications/DKCore/Source/DK Core.Source.zip",
            "BC/ModernDev/PFiles/Microsoft Dynamics NAV/150/AL Development Environment/System.app");
        var outerPath = WriteOuterZipToTemp(("Dynamics.365.BC.38701.DK.DVD.zip", inner));

        RunOpenStagedZip(outerPath, isDvd: true, names =>
            names.Should().BeEquivalentTo("Microsoft_DK Core.app", "System.app"));
    }

    [Fact]
    public void OpenStagedZip_does_not_descend_when_the_outer_archive_already_has_apps()
    {
        // A normal DVD zip (apps directly inside) must not be diverted into a
        // descent just because it happens to carry .Source.zip siblings.
        var outerPath = WriteRawZipToTemp(
            "BC/Applications/DKCore/Source/Microsoft_DK Core.app",
            "BC/Applications/DKCore/Source/DK Core.Source.zip");

        RunOpenStagedZip(outerPath, isDvd: true, names =>
            names.Should().BeEquivalentTo("Microsoft_DK Core.app"));
    }

    [Fact]
    public void OpenStagedZip_picks_the_sole_dvd_zip_when_other_zips_sit_alongside()
    {
        // A stray sibling zip (e.g. a prerequisites bundle) next to the DVD zip
        // must not block the descent — the *.dvd.zip is the unambiguous pick.
        var inner = BuildZipBytes(
            "BC/Applications/BaseApp/Source/Microsoft_Base Application.app");
        var outerPath = WriteOuterZipToTemp(
            ("Dynamics.365.BC.38701.DK.DVD.zip", inner),
            ("Prerequisites.zip", BuildZipBytes("setup/readme.txt")));

        RunOpenStagedZip(outerPath, isDvd: true, names =>
            names.Should().BeEquivalentTo("Microsoft_Base Application.app"));
    }

    [Fact]
    public void OpenStagedZip_stays_empty_when_several_nested_zips_are_ambiguous()
    {
        // Two non-DVD zips and no apps: we can't tell which is the DVD, so the
        // descent is refused and the importer reports "no apps" (the worker
        // surfaces DescribeAppLocations naming the candidates).
        var outerPath = WriteOuterZipToTemp(
            ("First.zip", BuildZipBytes("a/x.txt")),
            ("Second.zip", BuildZipBytes("b/y.txt")));

        RunOpenStagedZip(outerPath, isDvd: true, names => names.Should().BeEmpty());
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the staged zip, asserts on the resulting upload file names, and
    /// disposes every handle the call hands back so the (self-deleting) inner
    /// temp file and the outer temp file are both reclaimed.
    /// </summary>
    private static void RunOpenStagedZip(string outerPath, bool isDvd, Action<IEnumerable<string>> assert)
    {
        var openedStreams = new List<Stream>();
        ZipArchive? archive = null;
        try
        {
            (var uploads, archive) = ReleaseZipStaging.OpenStagedZip(outerPath, isDvd, openedStreams);
            assert(uploads.Select(u => u.FileName));
        }
        finally
        {
            foreach (var s in openedStreams) s.Dispose();
            archive?.Dispose();
            if (File.Exists(outerPath)) File.Delete(outerPath);
        }
    }

    /// <summary>Builds an in-memory zip with the given (empty-bodied) entry paths.</summary>
    private static byte[] BuildZipBytes(params string[] paths)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in paths) z.CreateEntry(p);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Writes an outer zip to a temp file whose entries are the supplied
    /// (name, content-bytes) pairs — used to stage a nested-zip wrapper on disk.
    /// </summary>
    private static string WriteOuterZipToTemp(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "oe-stagingtest-" + Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = z.CreateEntry(name);
            using var s = e.Open();
            s.Write(content);
        }
        return path;
    }

    /// <summary>Writes a flat zip (empty-bodied entries) straight to a temp file.</summary>
    private static string WriteRawZipToTemp(params string[] paths)
    {
        var path = Path.Combine(Path.GetTempPath(), "oe-stagingtest-" + Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var p in paths) z.CreateEntry(p);
        return path;
    }
}
