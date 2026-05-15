using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ALDevToolbox.Services.ObjectExplorer;

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

        // After the 40-byte prefix the bytes are a standard ZIP. Wrap a
        // non-owning MemoryStream view so the buffer survives ZipArchive's
        // dispose (it would otherwise dispose our underlying stream too).
        using var zipStream = new MemoryStream(bytes, NavxPrefixLength, bytes.Length - NavxPrefixLength, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifest = ReadManifest(archive);
        var symbols = ReadSymbolPackage(archive);
        var sourceFiles = manifest.IncludeSourceInSymbolFile
            ? ReadEmbeddedSource(archive)
            : Array.Empty<AppSourceFile>();

        return new AppPackage(manifest, symbols, sourceFiles, hash);
    }

    private static bool IsNavxHeader(byte[] bytes)
        => bytes.Length >= NavxPrefixLength
            && bytes[0] == 'N' && bytes[1] == 'A' && bytes[2] == 'V' && bytes[3] == 'X';

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
        var entry = FindEntry(archive, "SymbolReference.json")
            ?? throw NotFoundInArchive(archive, "SymbolReference.json");
        using var stream = entry.Open();
        // The file has a UTF-8 BOM; System.Text.Json handles it transparently.
        var raw = JsonSerializer.Deserialize<RawSymbolRoot>(stream, JsonOpts)
            ?? throw new InvalidDataException("SymbolReference.json deserialised to null.");

        var objects = new List<SymbolObject>();
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

        if (ns.Namespaces is not null)
        {
            foreach (var child in ns.Namespaces)
            {
                WalkNamespace(child, path, sink);
            }
        }
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
    /// Decodes the <c>#&lt;32 hex digits&gt;#&lt;target name&gt;</c> form
    /// used by symbol-package <c>TargetObject</c> / <c>Target</c> strings
    /// and by some Properties like <c>TableNo</c>. Returns
    /// <c>(null, null)</c> when the string isn't in that shape.
    /// </summary>
    private static (Guid? AppId, string? Name) ParseExtendsRef(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '#') return (null, null);
        var second = raw.IndexOf('#', 1);
        if (second != 33) return (null, null); // need exactly 32 hex digits between '#'s
        if (!Guid.TryParseExact(raw.AsSpan(1, 32), "N", out var guid)) return (null, null);
        var name = raw.Substring(34);
        return (guid, name);
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

            using var s = entry.Open();
            using var reader = new StreamReader(s);
            var content = reader.ReadToEnd();
            files.Add(new AppSourceFile(rel, content));
        }
        return files;
    }

    private static string NormalizeSourcePath(string fullName)
    {
        // Strip a leading "src/" once.
        if (fullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            fullName = fullName.Substring(4);
        // …and again if the .app double-nested.
        if (fullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            fullName = fullName.Substring(4);
        return "src/" + fullName;
    }

    // ── Raw deserialisation shapes (internal) ──────────────────────────
    // Mirror the SymbolReference.json layout closely; the public records above
    // are the cleaned-up flattened shape we hand to PR 3.

    private sealed class RawSymbolRoot
    {
        public string? RuntimeVersion { get; set; }
        public List<RawNamespace>? Namespaces { get; set; }
    }

    private sealed class RawNamespace
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
