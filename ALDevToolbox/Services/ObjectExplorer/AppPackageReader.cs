using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Thrown when <see cref="AppPackageReader"/> recognises an
/// NEA-encrypted (signed / marketplace) <c>.app</c>. The importer
/// catches this specifically to surface a field-keyed
/// <c>PlanValidationException</c> instead of a 500 page.
/// </summary>
public sealed class NeaEncryptedAppException : Exception
{
    public NeaEncryptedAppException(string message) : base(message) { }
}

/// <summary>
/// Parses a Business Central <c>.app</c> archive into an in-memory
/// <see cref="AppPackage"/>. The reader does no DB writes and has no
/// dependencies on services or DI — see <c>.design/object-explorer.md</c>
/// for how PR 3 consumes the result.
///
/// A <c>.app</c> file is a standard ZIP with a 40-byte <c>NAVX</c>-prefixed
/// header. The prefix carries a signature + sha-1 hash + reserved bytes; the
/// reader strips it and treats the remainder as a ZIP archive containing:
/// <list type="bullet">
///   <item><c>NavxManifest.xml</c> — app identity and policy flags</item>
///   <item><c>SymbolReference.json</c> — the symbol tree (UTF-8 BOM)</item>
///   <item><c>src/</c> (optional) — embedded source when <c>IncludeSourceInSymbolFile="true"</c></item>
///   <item>translations, layouts, images — ignored at this layer</item>
/// </list>
///
/// Microsoft also ships <em>Ready2Run</em> wrapper <c>.app</c> files (modern
/// DK / W1 DVDs use this for any module that ships a Ready2Run image — AMC
/// Banking is the visible example). The wrapper is itself a NAVX-prefixed
/// ZIP whose root contains <c>readytorunappmanifest.json</c> plus one nested
/// <c>.app</c> that is the real Navx archive. When we see that shape we
/// recurse into the nested <c>.app</c> and preserve the outer file's hash so
/// the importer's idempotency check still keys off what the operator
/// uploaded.
/// </summary>
public static class AppPackageReader
{
    private const int NavxPrefixLength = 40;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads the supplied <c>.app</c> byte stream end-to-end. The stream is
    /// fully consumed into a <see cref="MemoryStream"/> because <see cref="ZipArchive"/>
    /// needs seekable input and SHA-256 hashing wants the same bytes — doing
    /// both off one buffer avoids reading the source twice. Throws
    /// <see cref="InvalidDataException"/> when the input isn't a recognisable
    /// BC <c>.app</c> file.
    /// </summary>
    public static async Task<AppPackage> ReadAsync(Stream appFileStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appFileStream);

        var bytes = await ReadFullyAsync(appFileStream, ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        if (!IsNavxHeader(bytes))
        {
            throw new InvalidDataException(
                "Input does not look like a Business Central .app file — missing NAVX header.");
        }

        // NEA-encrypted apps (signed for AppSource / marketplace distribution)
        // carry a "NAVX.NEA" magic immediately after the 32-byte NAVX header
        // hash block, where a plain .app would have ZIP bytes. We can't open
        // the inner archive without the publisher's decryption key, so reject
        // up front with a recognisable message — otherwise the next line hands
        // an encrypted payload to ZipArchive and the operator sees a generic
        // "End of Central Directory record could not be found" stack trace.
        if (IsNeaEncrypted(bytes))
        {
            throw new NeaEncryptedAppException(
                "This .app is NEA-encrypted (signed for AppSource / marketplace distribution) "
                + "and can't be ingested — the Object Explorer needs an unencrypted .app. "
                + "Ask the publisher for the unsigned build, or use the per-tenant .app from "
                + "the BC artifacts/DVD instead of the AppSource download.");
        }

        // After the 40-byte prefix the bytes are a standard ZIP. Wrap a
        // non-owning MemoryStream view so the buffer survives ZipArchive's
        // dispose (it would otherwise dispose our underlying stream too).
        using var zipStream = new MemoryStream(bytes, NavxPrefixLength, bytes.Length - NavxPrefixLength, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        if (IsReadyToRunWrapper(archive))
        {
            var innerBytes = await ExtractReadyToRunInnerAppAsync(archive, ct).ConfigureAwait(false);
            await using var innerStream = new MemoryStream(innerBytes, writable: false);
            var inner = await ReadAsync(innerStream, ct).ConfigureAwait(false);
            // Keep the outer hash: ReleaseImportService.ImportOneAppAsync
            // dedupes on the bytes the operator uploaded.
            return inner with { AppFileHash = hash };
        }

        var manifest = ReadManifest(archive);
        var symbols = ReadSymbolPackage(archive);
        var sourceFiles = manifest.IncludeSourceInSymbolFile
            ? ReadEmbeddedSource(archive)
            : Array.Empty<AppSourceFile>();

        return new AppPackage(manifest, symbols, sourceFiles, hash);
    }

    // Note: the .app's `Translations/` folder is intentionally NOT
    // walked during ReadAsync. BC base-app XLIFFs run multi-hundred-MB
    // per language and `XDocument.Load` (the heart of AlXliffParser)
    // is a DOM parser that allocates an XElement per node — for a
    // 200&#160;MB XLIFF that's roughly 2&#160;GB of XLinq objects,
    // which tipped the import container straight into
    // OutOfMemoryException. The two earlier attempts at this
    // (`byte[]` buffering in commit 84b6001, then inline parsing
    // also at 84b6001's followups) didn't help because the DOM-load
    // cost dominates either way. Translations now arrive only via
    // the explicit upload paths on `TranslationImportService` —
    // admins choose when to pay that cost, on a per-file basis, and
    // a single bad XLIFF can't sink the whole release ingest.
    //
    // The right structural fix is a streaming XmlReader-based
    // rewrite of AlXliffParser; until that lands, no caller in this
    // layer pulls translations out of an .app automatically.

    // ── Ready2Run wrapper ───────────────────────────────────────────────

    private static bool IsReadyToRunWrapper(ZipArchive archive)
        => FindEntry(archive, "NavxManifest.xml") is null
            && FindEntry(archive, "readytorunappmanifest.json") is not null;

    /// <summary>
    /// Pulls the single root-level nested <c>.app</c> entry out of a
    /// Ready2Run wrapper into a fresh byte array. The wrapper holds exactly
    /// one <c>.app</c> at the archive root (any deeper <c>publishedartifacts/...</c>
    /// entries are build-machine path artefacts, not real apps). Refuses
    /// ambiguous archives with a diagnostic that lists what was found.
    /// </summary>
    private static async Task<byte[]> ExtractReadyToRunInnerAppAsync(ZipArchive archive, CancellationToken ct)
    {
        ZipArchiveEntry? inner = null;
        foreach (var e in archive.Entries)
        {
            if (e.FullName.IndexOf('/') >= 0) continue;
            if (!e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (inner is not null)
            {
                throw NotFoundInArchive(archive,
                    "a single root-level nested .app (Ready2Run wrapper has multiple candidates)");
            }
            inner = e;
        }
        if (inner is null)
        {
            throw NotFoundInArchive(archive,
                "a root-level nested .app inside the Ready2Run wrapper");
        }

        using var innerStream = OpenCapped(inner);
        using var buffer = new MemoryStream();
        await innerStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static bool IsNavxHeader(byte[] bytes)
        => bytes.Length >= NavxPrefixLength
            && bytes[0] == 'N' && bytes[1] == 'A' && bytes[2] == 'V' && bytes[3] == 'X';

    // NEA-encrypted .app files have the 8-byte ASCII magic "NAVX.NEA" at
    // offset 36 — sitting inside the otherwise-fixed 40-byte NAVX prefix
    // immediately after the 20-byte hash block and 4 bytes of zero padding.
    // The Microsoft signed-app tooling (and AppSource downloads) emits this
    // shape; the unsigned developer build doesn't.
    private const int NeaMagicOffset = 36;
    private static readonly byte[] NeaMagic = "NAVX.NEA"u8.ToArray();

    private static bool IsNeaEncrypted(byte[] bytes)
    {
        if (bytes.Length < NeaMagicOffset + NeaMagic.Length) return false;
        for (int i = 0; i < NeaMagic.Length; i++)
        {
            if (bytes[NeaMagicOffset + i] != NeaMagic[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Case-insensitive entry lookup. <see cref="ZipArchive.GetEntry"/> is
    /// strictly case-sensitive, but the <c>.app</c> archives Microsoft ships
    /// have drifted casing across BC versions (NavxManifest vs navxmanifest
    /// vs NAVXMANIFEST seen in the wild). Iterating once is cheap given the
    /// typical .app has a few hundred entries at most.
    /// </summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        foreach (var e in archive.Entries)
        {
            if (string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a diagnostic <see cref="InvalidDataException"/> that includes a
    /// truncated listing of what's actually inside the archive. Without this,
    /// "missing X" errors on opaque .apps were untractable — the only way to
    /// figure out the real entry name was to extract the file by hand.
    /// </summary>
    private static InvalidDataException NotFoundInArchive(ZipArchive archive, string expected)
    {
        const int sampleSize = 25;
        var sample = archive.Entries
            .Take(sampleSize)
            .Select(e => e.FullName)
            .ToList();
        var suffix = archive.Entries.Count > sampleSize ? $" (+{archive.Entries.Count - sampleSize} more)" : string.Empty;
        var listing = sample.Count == 0 ? "(empty archive)" : string.Join(", ", sample) + suffix;
        return new InvalidDataException(
            $".app archive is missing {expected}. Archive entries: [{listing}].");
    }

    private static async Task<byte[]> ReadFullyAsync(Stream s, CancellationToken ct)
    {
        if (s is MemoryStream alreadyMs && alreadyMs.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == alreadyMs.Length)
        {
            // Caller handed us a backing buffer we can use directly.
            return seg.Array!.Length == seg.Count ? seg.Array : seg.AsMemory().ToArray();
        }

        using var buffer = new MemoryStream();
        await s.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Per-entry uncompressed ceiling for nested archive entries. The upload
    /// is capped (MaxUploadBytes), but a single ZIP entry can deflate at a
    /// huge ratio, so a bomb inside an otherwise-small <c>.app</c> could still
    /// exhaust memory. Bound each entry well above any real BC payload (a
    /// base-app source file is a few hundred KB; the nested <c>.app</c> a few
    /// hundred MB) yet far below "exhaust the host".
    /// </summary>
    public const long MaxEntryBytes = 1L * 1024 * 1024 * 1024; // 1 GiB

    /// <summary>
    /// Opens <paramref name="entry"/> for reading through a wrapper that aborts
    /// once <see cref="MaxEntryBytes"/> bytes have been produced. Also rejects
    /// up front when the central-directory <see cref="ZipArchiveEntry.Length"/>
    /// already declares an over-limit entry — but the streaming guard is the
    /// real defence, because that declared length can lie. Preserves the
    /// behaviour of callers that wrap the result in a <see cref="StreamReader"/>
    /// or <see cref="Stream.CopyToAsync(Stream)"/>.
    /// </summary>
    public static Stream OpenCapped(ZipArchiveEntry entry, long maxBytes = MaxEntryBytes)
    {
        if (entry.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"Archive entry '{entry.FullName}' reports {entry.Length:N0} bytes, over the {maxBytes:N0}-byte limit.");
        }
        return new LengthCappedStream(entry.Open(), maxBytes, entry.FullName);
    }

    /// <summary>
    /// Read-only pass-through stream that throws once more than
    /// <c>maxBytes</c> have been read from the inner (decompressing) stream —
    /// a zip-bomb tripwire that doesn't trust the archive's declared sizes.
    /// </summary>
    private sealed class LengthCappedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly string _entryName;
        private long _read;

        public LengthCappedStream(Stream inner, long maxBytes, string entryName)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _entryName = entryName;
        }

        private int Track(int read)
        {
            _read += read;
            if (_read > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"Archive entry '{_entryName}' exceeded the {_maxBytes:N0}-byte limit while decompressing (possible zip bomb).");
            }
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Track(_inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Track(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── Manifest ────────────────────────────────────────────────────────

    private static AppManifest ReadManifest(ZipArchive archive)
    {
        var entry = FindEntry(archive, "NavxManifest.xml")
            ?? throw NotFoundInArchive(archive, "NavxManifest.xml");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        // The manifest's default namespace is set on the root <Package>;
        // every child element is qualified with it.
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var app = doc.Root?.Element(ns + "App")
            ?? throw new InvalidDataException("NavxManifest.xml is missing the <App> element.");

        var appId = Guid.Parse(Attr(app, "Id"));
        var name = Attr(app, "Name");
        var publisher = Attr(app, "Publisher");
        var version = Attr(app, "Version");
        var target = AttrOptional(app, "Target");
        var runtime = AttrOptional(app, "Runtime");

        var policy = doc.Root?.Element(ns + "ResourceExposurePolicy");
        var includeSource = ParseBoolAttr(policy, "IncludeSourceInSymbolFile", defaultValue: false);
        var allowDebugging = ParseBoolAttr(policy, "AllowDebugging", defaultValue: false);
        var allowSrcDownload = ParseBoolAttr(policy, "AllowDownloadingSource", defaultValue: false);

        var deps = new List<AppDependency>();
        var depsRoot = doc.Root?.Element(ns + "Dependencies");
        if (depsRoot is not null)
        {
            foreach (var dep in depsRoot.Elements(ns + "Dependency"))
            {
                if (!Guid.TryParse(AttrOptional(dep, "Id") ?? string.Empty, out var depId)) continue;
                deps.Add(new AppDependency(
                    AppId: depId,
                    Name: AttrOptional(dep, "Name") ?? string.Empty,
                    Publisher: AttrOptional(dep, "Publisher") ?? string.Empty,
                    Version: AttrOptional(dep, "MinVersion") ?? AttrOptional(dep, "Version") ?? string.Empty));
            }
        }

        return new AppManifest(
            AppId: appId,
            Name: name,
            Publisher: publisher,
            Version: version,
            Target: target,
            Runtime: runtime,
            IncludeSourceInSymbolFile: includeSource,
            AllowDebugging: allowDebugging,
            AllowDownloadingSource: allowSrcDownload,
            Dependencies: deps);
    }

    private static string Attr(XElement el, string name)
        => el.Attribute(name)?.Value
            ?? throw new InvalidDataException($"NavxManifest.xml: <{el.Name.LocalName}> missing required attribute '{name}'.");

    private static string? AttrOptional(XElement el, string name) => el.Attribute(name)?.Value;

    private static bool ParseBoolAttr(XElement? el, string name, bool defaultValue)
    {
        if (el is null) return defaultValue;
        var v = el.Attribute(name)?.Value;
        if (string.IsNullOrEmpty(v)) return defaultValue;
        return bool.TryParse(v, out var parsed) ? parsed : defaultValue;
    }

    // ── SymbolReference.json ────────────────────────────────────────────

    private static SymbolPackage ReadSymbolPackage(ZipArchive archive)
    {
        // The symbol package is *optional*. Translation-only language packs and
        // a few system .apps ship with just the manifest + payload files and no
        // SymbolReference.json — there's no code to symbolise. Treat the
        // missing case as "this .app contributes a Module row but zero objects"
        // and let the importer continue. The manifest is the only file that
        // gets the strict-missing treatment.
        var entry = FindEntry(archive, "SymbolReference.json");
        if (entry is null)
        {
            return new SymbolPackage(RuntimeVersion: null, Objects: Array.Empty<SymbolObject>());
        }
        using var stream = entry.Open();
        // The file has a UTF-8 BOM; System.Text.Json handles it transparently.
        var raw = JsonSerializer.Deserialize<RawSymbolRoot>(stream, JsonOpts)
            ?? throw new InvalidDataException("SymbolReference.json deserialised to null.");

        var objects = new List<SymbolObject>();

        // BC 22 introduced AL namespaces and moved every object collection
        // under a `Namespaces` tree. Pre-namespace releases (BC 14 through
        // ~21) put the collections directly on the symbol-file root, with no
        // `Namespaces` wrapper at all. Emit any root-level objects under the
        // empty namespace first — a no-op for modern files where those lists
        // are null — then walk the nested namespace tree for newer files.
        EmitNamespaceObjects(raw, path: string.Empty, objects);
        if (raw.Namespaces is not null)
        {
            foreach (var ns in raw.Namespaces)
            {
                WalkNamespace(ns, currentPath: string.Empty, objects);
            }
        }
        return new SymbolPackage(raw.RuntimeVersion, objects);
    }

    private static void WalkNamespace(RawNamespace ns, string currentPath, List<SymbolObject> sink)
    {
        var path = string.IsNullOrEmpty(currentPath)
            ? (ns.Name ?? string.Empty)
            : $"{currentPath}.{ns.Name}";

        EmitNamespaceObjects(ns, path, sink);

        if (ns.Namespaces is not null)
        {
            foreach (var child in ns.Namespaces)
            {
                WalkNamespace(child, path, sink);
            }
        }
    }

    private static void EmitNamespaceObjects(RawNamespace ns, string path, List<SymbolObject> sink)
    {
        EmitObjects("codeunit",                 ns.Codeunits,               path, sink);
        EmitObjects("table",                    ns.Tables,                  path, sink);
        EmitObjects("page",                     ns.Pages,                   path, sink);
        EmitObjects("report",                   ns.Reports,                 path, sink);
        EmitObjects("xmlport",                  ns.XmlPorts,                path, sink);
        EmitObjects("query",                    ns.Queries,                 path, sink);
        EmitObjects("controladdin",             ns.ControlAddIns,           path, sink);
        EmitObjects("enum",                     ns.EnumTypes,               path, sink);
        EmitObjects("dotnetpackage",            ns.DotNetPackages,          path, sink);
        EmitObjects("interface",                ns.Interfaces,              path, sink);
        EmitObjects("permissionset",            ns.PermissionSets,          path, sink);
        EmitObjects("permissionsetextension",   ns.PermissionSetExtensions, path, sink);
        EmitObjects("reportextension",          ns.ReportExtensions,        path, sink);
        EmitObjects("pageextension",            ns.PageExtensions,          path, sink);
        EmitObjects("tableextension",           ns.TableExtensions,         path, sink);
        EmitObjects("enumextension",            ns.EnumExtensions,          path, sink);
    }

    private static void EmitObjects(string kind, IReadOnlyList<RawObject>? items, string ns, List<SymbolObject> sink)
    {
        if (items is null) return;
        foreach (var raw in items)
        {
            var (extendsAppId, extendsName) = ParseExtendsRef(raw.TargetObject ?? raw.Target);
            sink.Add(new SymbolObject(
                Kind: kind,
                ObjectId: raw.Id,
                Name: raw.Name ?? string.Empty,
                Namespace: ns,
                ReferenceSourceFileName: raw.ReferenceSourceFileName,
                ExtendsAppId: extendsAppId,
                ExtendsObjectName: extendsName,
                Properties: ToProperties(raw.Properties),
                Methods: ToMethods(raw.Methods),
                Variables: ToVariables(raw.Variables),
                Fields: ToFields(raw.Fields)));
        }
    }

    /// <summary>
    /// Decodes a symbol-package extends-target string into its
    /// <c>(AppId?, BaseObjectName?)</c> components. Two shapes occur
    /// in practice:
    /// <list type="bullet">
    ///   <item><c>#&lt;32 hex&gt;#&lt;name&gt;</c> — the cross-app
    ///     form (e.g. OIOUBL extending Base App's <c>Customer</c>).
    ///     Returns the decoded AppId and the unwrapped name.</item>
    ///   <item><c>&lt;name&gt;</c> — same-app extensions and
    ///     ReportExtensions ship without the <c>#appid#</c> wrapper
    ///     (BC's Base App writes <c>Target = "Cancel FA Ledger
    ///     Entries"</c> for ReportExtensions; same-app
    ///     TableExtensions ship the base name as-is).
    ///     Returns (null, name).</item>
    ///   <item>null / empty / malformed <c>#</c>-prefix — returns
    ///     (null, null).</item>
    /// </list>
    /// <para>The name is returned verbatim, including any internal
    /// dots, spaces, dashes, slashes, ampersands, or trailing
    /// periods — <c>Gen. Journal Line</c>, <c>Sales Cr.Memo Header</c>,
    /// <c>Whse.-Source - Create Document</c>, <c>Country/Region</c>,
    /// <c>Purchases &amp; Payables Setup</c>, <c>Vendor Templ.</c>
    /// are all real BC 28.1 targets and round-trip unchanged. We do
    /// <em>not</em> attempt to strip an AL namespace prefix: a sweep
    /// over every <c>Target</c>, <c>TargetObject</c>, and
    /// <c>Subtype.Name</c> string across BaseApp, BusinessFoundation,
    /// SystemApp, QualityManagement, EDocument Core, Intrastat Core,
    /// OIOUBL, and DK Core found zero values shaped like
    /// <c>Microsoft.Foo.Bar</c>, so there's nothing to strip in
    /// practice and a heuristic would only buy ambiguity for AL names
    /// that happen to look like one.</para>
    /// <para>If a future BC release or a partner app starts emitting
    /// namespace-qualified targets, the symptom will be missing
    /// <c>extends_target</c> lookups (the chain walker's
    /// <c>_extensionsByBaseName</c> joins on
    /// <c>base.name = ext.extends_object_name</c>, which only matches
    /// when both sides are bare). The right fix at that point is
    /// <em>not</em> to reintroduce a guess: we already store
    /// <c>oe_module_objects.namespace</c> for every base object, so
    /// the importer can split a qualified target on the longest
    /// namespace prefix that matches a real namespace in the same
    /// release rather than walking dots and hoping. The previous
    /// heuristic-driven strip is preserved in the git history at
    /// commit <c>69ba173</c> (refined "dot followed by identifier
    /// start") and <c>0bf279c</c> (original "dot followed by
    /// whitespace") if the next maintainer wants a reference shape
    /// to start from.</para>
    /// <para>See <c>.design/object-explorer.md</c> ("TargetObject /
    /// Target are read verbatim") for the rationale and the rollback
    /// plan.</para>
    /// </summary>
    internal static (Guid? AppId, string? Name) ParseExtendsRef(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, null);

        if (raw[0] != '#')
        {
            return (null, raw);
        }

        var second = raw.IndexOf('#', 1);
        if (second != 33) return (null, null); // need exactly 32 hex digits between '#'s
        if (!Guid.TryParseExact(raw.AsSpan(1, 32), "N", out var guid)) return (null, null);
        var name = raw.Substring(34);
        return string.IsNullOrEmpty(name) ? (null, null) : ((Guid?)guid, name);
    }

    private static IReadOnlyList<SymbolProperty> ToProperties(IReadOnlyList<RawProperty>? raws)
        => raws is null
            ? Array.Empty<SymbolProperty>()
            : raws.Select(p => new SymbolProperty(p.Name ?? string.Empty, p.Value ?? string.Empty)).ToList();

    private static IReadOnlyList<SymbolMethod> ToMethods(IReadOnlyList<RawMethod>? raws)
    {
        if (raws is null) return Array.Empty<SymbolMethod>();
        var list = new List<SymbolMethod>(raws.Count);
        foreach (var m in raws)
        {
            list.Add(new SymbolMethod(
                Name: m.Name ?? string.Empty,
                ReturnType: ToTypeRef(m.ReturnTypeDefinition),
                Parameters: ToParameters(m.Parameters),
                IsInternal: m.IsInternal ?? false));
        }
        return list;
    }

    private static IReadOnlyList<SymbolParameter> ToParameters(IReadOnlyList<RawParameter>? raws)
    {
        if (raws is null) return Array.Empty<SymbolParameter>();
        var list = new List<SymbolParameter>(raws.Count);
        foreach (var p in raws)
        {
            list.Add(new SymbolParameter(
                Name: p.Name ?? string.Empty,
                Type: ToTypeRef(p.TypeDefinition) ?? new SymbolTypeRef(string.Empty, null, null, null)));
        }
        return list;
    }

    private static IReadOnlyList<SymbolVariable> ToVariables(IReadOnlyList<RawVariable>? raws)
    {
        if (raws is null) return Array.Empty<SymbolVariable>();
        var list = new List<SymbolVariable>(raws.Count);
        foreach (var v in raws)
        {
            list.Add(new SymbolVariable(
                Name: v.Name ?? string.Empty,
                Type: ToTypeRef(v.TypeDefinition) ?? new SymbolTypeRef(string.Empty, null, null, null)));
        }
        return list;
    }

    private static IReadOnlyList<SymbolField> ToFields(IReadOnlyList<RawField>? raws)
    {
        if (raws is null) return Array.Empty<SymbolField>();
        var list = new List<SymbolField>(raws.Count);
        foreach (var f in raws)
        {
            list.Add(new SymbolField(
                Id: f.Id ?? 0,
                Name: f.Name ?? string.Empty,
                Type: ToTypeRef(f.TypeDefinition) ?? new SymbolTypeRef(string.Empty, null, null, null),
                Properties: ToProperties(f.Properties)));
        }
        return list;
    }

    private static SymbolTypeRef? ToTypeRef(RawTypeDefinition? raw)
    {
        if (raw is null) return null;
        Guid? moduleId = null;
        if (!string.IsNullOrEmpty(raw.Subtype?.ModuleId) && Guid.TryParse(raw.Subtype.ModuleId, out var parsed))
        {
            moduleId = parsed;
        }
        return new SymbolTypeRef(
            Name: raw.Name ?? string.Empty,
            ModuleId: moduleId,
            ObjectId: raw.Subtype?.Id,
            ObjectName: raw.Subtype?.Name);
    }

    // ── Embedded source ─────────────────────────────────────────────────

    /// <summary>
    /// Pulls every <c>.al</c> file out of the archive's <c>src/</c> tree.
    /// The .app uses a double prefix (<c>src/src/...</c>) — the inner one is
    /// the project's own source root inside the build output. We keep paths
    /// relative to that inner root, matching the layout a paired
    /// <c>.Source.zip</c> uses.
    /// </summary>
    private static IReadOnlyList<AppSourceFile> ReadEmbeddedSource(ZipArchive archive)
    {
        var files = new List<AppSourceFile>();
        foreach (var entry in archive.Entries)
        {
            // Skip directory entries.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            // Only AL source files. .rdlc / .xlf / images / etc. are explicitly out of scope.
            if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;

            // Normalise the in-archive path. Microsoft's .app files use
            // src/src/<actual> but the paired .Source.zip uses src/<actual>.
            // Keep the inner-most one so both ingestion paths see the same
            // shape.
            var rel = NormalizeSourcePath(entry.FullName);

            using var s = OpenCapped(entry);
            using var reader = new StreamReader(s);
            var content = reader.ReadToEnd();
            files.Add(new AppSourceFile(rel, content));
        }
        return files;
    }

    private static string NormalizeSourcePath(string fullName) => CanonicalizeSourcePath(fullName);

    /// <summary>
    /// Coerces a raw archive entry path into the canonical
    /// <c>src/&lt;relative&gt;</c> shape the importer keys ModuleFile rows
    /// by. Microsoft has shipped at least four layouts in the wild:
    /// <list type="bullet">
    ///   <item><c>src/Codeunits/Foo.al</c> — first-party Source.zip in BC 25.x</item>
    ///   <item><c>src/src/Codeunits/Foo.al</c> — double-nested inside the <c>.app</c></item>
    ///   <item><c>Base Application/src/Codeunits/Foo.al</c> — project-folder
    ///         wrapper used by BC 28.x first-party Source.zips</item>
    ///   <item><c>Codeunits/Foo.al</c> — partner shape without any <c>src/</c></item>
    /// </list>
    /// All four flatten to <c>src/Codeunits/Foo.al</c> so symbol-package
    /// <c>ReferenceSourceFileName</c> lookups land on the same key
    /// regardless of where the file originally came from.
    /// </summary>
    public static string CanonicalizeSourcePath(string fullName)
    {
        // Normalise backslashes; some zipping tools emit them on Windows.
        fullName = fullName.Replace('\\', '/');

        // If a "/src/" segment sits anywhere in the path, take the suffix
        // starting at that segment — drops any project-folder or
        // publisher-prefix wrapper Microsoft adds in BC 28.x Source.zips.
        var slashSrc = fullName.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        if (slashSrc >= 0)
        {
            fullName = fullName.Substring(slashSrc + 1);
        }

        // Collapse any leading "src/" repetitions to a single layer.
        while (fullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            fullName = fullName.Substring(4);
        }

        return "src/" + fullName;
    }

    // ── Raw deserialisation shapes (internal) ──────────────────────────
    // Mirror the SymbolReference.json layout closely; the public records above
    // are the cleaned-up flattened shape we hand to PR 3.

    // Pre-namespace BC (≤ ~21, e.g. BC 14) stores object collections directly
    // on the root; BC 22+ nests them under `Namespaces`. Inheriting from
    // RawNamespace lets the root be walked as the implicit empty namespace
    // without a second copy of every collection property.
    private sealed class RawSymbolRoot : RawNamespace
    {
        public string? RuntimeVersion { get; set; }
    }

    private class RawNamespace
    {
        public string? Name { get; set; }
        public List<RawNamespace>? Namespaces { get; set; }
        public List<RawObject>? Codeunits { get; set; }
        public List<RawObject>? Tables { get; set; }
        public List<RawObject>? Pages { get; set; }
        public List<RawObject>? Reports { get; set; }
        public List<RawObject>? XmlPorts { get; set; }
        public List<RawObject>? Queries { get; set; }
        public List<RawObject>? ControlAddIns { get; set; }
        public List<RawObject>? EnumTypes { get; set; }
        public List<RawObject>? DotNetPackages { get; set; }
        public List<RawObject>? Interfaces { get; set; }
        public List<RawObject>? PermissionSets { get; set; }
        public List<RawObject>? PermissionSetExtensions { get; set; }
        public List<RawObject>? ReportExtensions { get; set; }
        public List<RawObject>? PageExtensions { get; set; }
        public List<RawObject>? TableExtensions { get; set; }
        public List<RawObject>? EnumExtensions { get; set; }
    }

    private sealed class RawObject
    {
        public string? Name { get; set; }
        public int? Id { get; set; }
        public string? ReferenceSourceFileName { get; set; }
        public string? TargetObject { get; set; }
        public string? Target { get; set; }
        public List<RawProperty>? Properties { get; set; }
        public List<RawMethod>? Methods { get; set; }
        public List<RawVariable>? Variables { get; set; }
        public List<RawField>? Fields { get; set; }
    }

    private sealed class RawProperty
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    private sealed class RawMethod
    {
        public string? Name { get; set; }
        public RawTypeDefinition? ReturnTypeDefinition { get; set; }
        public List<RawParameter>? Parameters { get; set; }
        public bool? IsInternal { get; set; }
    }

    private sealed class RawParameter
    {
        public string? Name { get; set; }
        public RawTypeDefinition? TypeDefinition { get; set; }
    }

    private sealed class RawVariable
    {
        public string? Name { get; set; }
        public RawTypeDefinition? TypeDefinition { get; set; }
    }

    private sealed class RawField
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public RawTypeDefinition? TypeDefinition { get; set; }
        public List<RawProperty>? Properties { get; set; }
    }

    private sealed class RawTypeDefinition
    {
        public string? Name { get; set; }
        public RawSubtype? Subtype { get; set; }
    }

    private sealed class RawSubtype
    {
        public string? ModuleId { get; set; }
        public string? Name { get; set; }
        public int? Id { get; set; }
    }
}
