using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Tests <see cref="AppPackageReader"/> against two real Microsoft <c>.app</c>
/// samples committed under <c>Fixtures/ObjectExplorer/</c>:
///
///   - DK Core 25.18 — codeunits + page extensions + report extensions, no tables.
///     Smaller (~80 KB) and validates the basic shape.
///   - OIOUBL 25.18 — codeunits + a table + table extensions + page extensions +
///     reports. Larger (~200 KB) and validates field / extension / cross-module
///     reference handling.
///
/// The reader is pure — no DB, no DI — so these tests are tight in-memory
/// reads. PR 3 will exercise the next layer (the import service) which turns
/// the parsed result into rows.
/// </summary>
public sealed class AppPackageReaderTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    [Fact]
    public async Task ReadAsync_parses_dk_core_app_manifest()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        pkg.Manifest.AppId.Should().Be(Guid.Parse("40d64215-8abc-4d96-87dc-2894e5431115"));
        pkg.Manifest.Name.Should().Be("DK Core");
        pkg.Manifest.Publisher.Should().Be("Microsoft");
        pkg.Manifest.Version.Should().Be("25.18.48229.0");
        pkg.Manifest.Target.Should().Be("Cloud");
        pkg.Manifest.Runtime.Should().Be("14.0");
        pkg.Manifest.IncludeSourceInSymbolFile.Should().BeTrue();
        pkg.AppFileHash.Should().HaveLength(64);
    }

    [Fact]
    public async Task ReadAsync_walks_namespace_tree_into_flat_object_list()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        // The DK Core symbol tree is Microsoft.Finance.Core; objects live at
        // the leaf with that namespace path.
        var codeunits = pkg.Symbols.Objects.Where(o => o.Kind == "codeunit").ToList();
        var pageExts  = pkg.Symbols.Objects.Where(o => o.Kind == "pageextension").ToList();
        var reportExts = pkg.Symbols.Objects.Where(o => o.Kind == "reportextension").ToList();

        codeunits.Should().HaveCount(4);
        pageExts.Should().HaveCount(3);
        reportExts.Should().HaveCount(7);

        codeunits.Should().OnlyContain(o => o.Namespace == "Microsoft.Finance.Core");
    }

    [Fact]
    public async Task ReadAsync_captures_extends_target_with_decoded_app_id()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        var pageExt = pkg.Symbols.Objects.SingleOrDefault(o => o.Name == "CompanyInformationExt");
        pageExt.Should().NotBeNull("DK Core's CompanyInformationExt should be in the symbol tree");
        pageExt!.Kind.Should().Be("pageextension");
        pageExt.ExtendsObjectName.Should().Be("Company Information");
        pageExt.ExtendsAppId.Should().Be(Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"),
            because: "the #...# prefix on TargetObject decodes to Base App's AppId");
    }

    [Fact]
    public async Task ReadAsync_resolves_cross_module_variable_subtypes()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        // CopyDepreciationBookExt has a Codeunit-typed variable that resolves
        // back to Base App's "Cancel FA Ledger Entries" (id 5624). This is
        // the gold property we depend on for cross-module reference resolution.
        var reportExt = pkg.Symbols.Objects.Single(o => o.Name == "CopyDepreciationBookExt");
        var variable = reportExt.Variables.Single(v => v.Name == "CancelFALedgEntries");

        variable.Type.Name.Should().Be("Codeunit");
        variable.Type.ObjectName.Should().Be("Cancel FA Ledger Entries");
        variable.Type.ObjectId.Should().Be(5624);
        variable.Type.ModuleId.Should().Be(Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"));
    }

    [Fact]
    public async Task ReadAsync_extracts_embedded_source_with_normalised_paths()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        // DK Core has IncludeSourceInSymbolFile=true, so .al files come along.
        pkg.SourceFiles.Should().NotBeEmpty();
        pkg.SourceFiles.Should().Contain(f =>
            f.Path == "src/Codeunits/DKCoreEventSubscribers.Codeunit.al"
            && f.Content.Contains("codeunit 13601"));

        // Paths normalise the double "src/src/..." prefix that .app uses; we
        // store them as if they came from the paired .Source.zip (single src/).
        pkg.SourceFiles.Should().OnlyContain(f =>
            f.Path.StartsWith("src/", StringComparison.Ordinal)
            && f.Path.EndsWith(".al", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadAsync_handles_empty_dependencies_block()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        // Microsoft's first-party .app files actually carry an empty
        // <Dependencies /> — the Base App dependency comes through the
        // Application/Platform attributes on <App>, not as an explicit
        // dependency entry. The parser must not crash on the empty form.
        // Validating non-empty Dependencies needs a synthetic .app or a
        // partner-built sample; defer until we have one.
        pkg.Manifest.Dependencies.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_parses_oioubl_table_with_fields_and_methods()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        var profile = pkg.Symbols.Objects.SingleOrDefault(o => o.Kind == "table" && o.Name == "OIOUBL-Profile");
        profile.Should().NotBeNull();
        profile!.ObjectId.Should().Be(13630);
        profile.Fields.Should().HaveCount(2);
        profile.Fields.Should().ContainSingle(f => f.Name == "OIOUBL-Code" && f.Id == 13630);
        profile.Fields.Should().ContainSingle(f => f.Name == "OIOUBL-Profile ID" && f.Id == 13631);

        // Two methods on the table — one with a return type, one without.
        profile.Methods.Should().HaveCount(2);
        var getter = profile.Methods.Single(m => m.Name == "GetOIOUBLProfileID");
        getter.ReturnType!.Name.Should().Be("Text");
        getter.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_parses_oioubl_tableextension_with_extends_target()
    {
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var pkg = await AppPackageReader.ReadAsync(stream);

        var tex = pkg.Symbols.Objects.SingleOrDefault(o =>
            o.Kind == "tableextension" && o.Name == "OIOUBL-Fin. Charge Memo Line");
        tex.Should().NotBeNull();
        tex!.ExtendsObjectName.Should().Be("Finance Charge Memo Line");
        tex.ExtendsAppId.Should().Be(Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"));
        tex.Fields.Should().ContainSingle(f => f.Name == "OIOUBL-Account Code");
    }

    [Fact]
    public async Task ReadAsync_rejects_input_without_navx_header()
    {
        // Just a plain ZIP — no NAVX prefix.
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "DK Core.Source.zip"));
        var act = async () => await AppPackageReader.ReadAsync(stream);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*NAVX*");
    }

    [Fact]
    public async Task ReadAsync_treats_missing_symbol_package_as_empty()
    {
        // Translation-only language packs and a handful of system .apps ship
        // with the manifest only — no SymbolReference.json because there's no
        // code to symbolise. The reader must surface those as a Module with
        // zero objects rather than refusing the whole upload.
        var bytes = BuildSyntheticAppFile(manifestEntryName: "NavxManifest.xml", includeSymbolReference: false);
        await using var stream = new MemoryStream(bytes);

        var pkg = await AppPackageReader.ReadAsync(stream);
        pkg.Manifest.Name.Should().Be("Synthetic");
        pkg.Symbols.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_tolerates_case_drift_on_manifest_filename()
    {
        // Microsoft has shipped .apps with both NavxManifest.xml and the
        // lowercase navxmanifest.xml across BC versions. Synthesise a tiny
        // .app-shaped archive with the lowercase variant to pin the lookup
        // tolerance — production exits early on a missing manifest, so
        // anything more than parsing the App element here is overkill.
        var bytes = BuildSyntheticAppFile(manifestEntryName: "navxmanifest.xml");
        await using var stream = new MemoryStream(bytes);

        var pkg = await AppPackageReader.ReadAsync(stream);
        pkg.Manifest.AppId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        pkg.Manifest.Name.Should().Be("Synthetic");
    }

    [Fact]
    public async Task ReadAsync_includes_archive_entry_listing_when_manifest_is_actually_missing()
    {
        // No manifest at any case — the error message must include the
        // archive's entry list so operators can see what's actually inside.
        var bytes = BuildSyntheticAppFile(manifestEntryName: null);
        await using var stream = new MemoryStream(bytes);

        var act = async () => await AppPackageReader.ReadAsync(stream);
        (await act.Should().ThrowAsync<InvalidDataException>())
            .WithMessage("*missing NavxManifest.xml*")
            .WithMessage("*Archive entries:*")
            .WithMessage("*SymbolReference.json*");
    }

    /// <summary>
    /// Builds a minimal .app-shaped byte payload: 40-byte NAVX header +
    /// a ZIP containing the manifest (under the supplied entry name) and a
    /// trivial SymbolReference.json. Returns the full byte blob the reader
    /// will see.
    /// </summary>
    private static byte[] BuildSyntheticAppFile(string? manifestEntryName, bool includeSymbolReference = true)
    {
        // Build the ZIP into its own buffer first; ZipArchive in Create mode
        // writes its central directory using offsets relative to the start
        // of the stream, so the .app's leading NAVX header must come from
        // a second concat step rather than sit in front of the ZipArchive.
        byte[] zipBytes;
        using (var zipMs = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                if (manifestEntryName is not null)
                {
                    var manifestEntry = zip.CreateEntry(manifestEntryName);
                    using var w = new StreamWriter(manifestEntry.Open());
                    w.Write(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                        + "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
                        + "<App Id=\"11111111-1111-1111-1111-111111111111\" Name=\"Synthetic\" Publisher=\"Test\" Version=\"1.0.0.0\" />"
                        + "</Package>");
                }
                if (includeSymbolReference)
                {
                    var sym = zip.CreateEntry("SymbolReference.json");
                    using var sw = new StreamWriter(sym.Open());
                    sw.Write("{\"RuntimeVersion\":\"14.0\",\"Namespaces\":[]}");
                }
            }
            zipBytes = zipMs.ToArray();
        }

        var result = new byte[40 + zipBytes.Length];
        // 4-byte NAVX magic + 36 padding bytes.
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        Buffer.BlockCopy(zipBytes, 0, result, 40, zipBytes.Length);
        return result;
    }

    [Fact]
    public async Task ReadAsync_unwraps_ready2run_wrapper_and_preserves_outer_hash()
    {
        // Microsoft's modern DVDs ship "Ready2Run" wrapper .app files: a
        // NAVX-prefixed ZIP whose root holds readytorunappmanifest.json plus
        // one nested .app that is the real Navx archive. AMC Banking was the
        // visible failure (NavxManifest.xml not found on the wrapper). The
        // reader has to peel off the wrapper, recurse, and surface the inner
        // manifest while keeping the outer hash so the importer's idempotency
        // check still keys off the operator's upload.
        var innerPath = Path.Combine(FixtureRoot, "Microsoft_DK_Core.app");
        var innerBytes = await File.ReadAllBytesAsync(innerPath);

        var wrapperBytes = BuildReadyToRunWrapper(innerBytes, innerAppFileName: "inner_28.1.49838.app");

        // Sanity-check: the wrapper is recognisably different from the inner.
        wrapperBytes.SequenceEqual(innerBytes).Should().BeFalse();

        await using var wrapperStream = new MemoryStream(wrapperBytes);
        var pkg = await AppPackageReader.ReadAsync(wrapperStream);

        // Inner manifest came through — same identity as reading the bare .app directly.
        pkg.Manifest.AppId.Should().Be(Guid.Parse("40d64215-8abc-4d96-87dc-2894e5431115"));
        pkg.Manifest.Name.Should().Be("DK Core");
        pkg.Manifest.Publisher.Should().Be("Microsoft");

        // Hash is over the *wrapper* bytes, not the inner blob, so the
        // importer's (release, app, version, hash) idempotency check matches
        // what the operator actually uploaded.
        var expectedOuterHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(wrapperBytes));
        pkg.AppFileHash.Should().Be(expectedOuterHash);

        var innerHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(innerBytes));
        pkg.AppFileHash.Should().NotBe(innerHash);
    }

    [Fact]
    public async Task ReadAsync_rejects_ready2run_wrapper_with_multiple_root_apps()
    {
        // A wrapper with two root-level .app entries is ambiguous — refuse
        // it with a diagnostic rather than guessing which one is the real
        // payload.
        var innerBytes = await File.ReadAllBytesAsync(
            Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var wrapperBytes = BuildReadyToRunWrapper(
            innerBytes,
            innerAppFileName: "first.app",
            extraRootAppName: "second.app");

        await using var stream = new MemoryStream(wrapperBytes);
        var act = async () => await AppPackageReader.ReadAsync(stream);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Ready2Run*");
    }

    /// <summary>
    /// Builds a synthetic Ready2Run wrapper around a real inner .app: a
    /// NAVX-prefixed ZIP containing readytorunappmanifest.json plus the
    /// inner .app at the archive root. <paramref name="extraRootAppName"/>
    /// produces an additional root-level .app entry for the ambiguity test.
    /// </summary>
    private static byte[] BuildReadyToRunWrapper(byte[] innerAppBytes, string innerAppFileName, string? extraRootAppName = null)
    {
        byte[] zipBytes;
        using (var zipMs = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = zip.CreateEntry("readytorunappmanifest.json");
                using (var w = new StreamWriter(manifest.Open()))
                {
                    w.Write("{\"format\":\"ready2run\",\"inner\":\"" + innerAppFileName + "\"}");
                }

                var inner = zip.CreateEntry(innerAppFileName);
                using (var s = inner.Open())
                {
                    s.Write(innerAppBytes, 0, innerAppBytes.Length);
                }

                if (extraRootAppName is not null)
                {
                    var extra = zip.CreateEntry(extraRootAppName);
                    using var s = extra.Open();
                    s.Write(innerAppBytes, 0, innerAppBytes.Length);
                }
            }
            zipBytes = zipMs.ToArray();
        }

        var result = new byte[40 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        Buffer.BlockCopy(zipBytes, 0, result, 40, zipBytes.Length);
        return result;
    }

    [Fact]
    public async Task ReadAsync_produces_identical_hash_on_replay()
    {
        var path = Path.Combine(FixtureRoot, "Microsoft_DK_Core.app");
        string hashA;
        await using (var s = File.OpenRead(path))
        {
            hashA = (await AppPackageReader.ReadAsync(s)).AppFileHash;
        }
        string hashB;
        await using (var s = File.OpenRead(path))
        {
            hashB = (await AppPackageReader.ReadAsync(s)).AppFileHash;
        }
        hashA.Should().Be(hashB, "the hash is over the raw bytes — identical input must give identical output");
    }
}
