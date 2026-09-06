using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Services.Al;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// The pure parsing half of the Object Explorer ingest: the AL object
/// header / SourceTable regexes, the symbol-package type mapping, the
/// .Source.zip reader, and the per-file declaration scan whose results
/// <see cref="ReleaseImportService"/> writes into the <c>oe_*</c> rows.
///
/// Everything here is static and side-effect free (no DbContext, no
/// logging), extracted from ReleaseImportService so the parsing rules
/// can be read — and pinned by unit tests — without the ingest
/// orchestration around them.
/// </summary>
internal static class ReleaseSourceScanner
{
    internal static readonly Dictionary<string, string> TypeKeywordToObjectKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Codeunit"]  = "codeunit",
        ["Record"]    = "table",
        ["Page"]      = "page",
        ["Report"]    = "report",
        ["XmlPort"]   = "xmlport",
        ["Query"]     = "query",
        ["Interface"] = "interface",
        ["Enum"]      = "enum",
    };

    // Matches the opening line of an AL object declaration. The kind is in
    // group 1, the optional numeric id in 2, the unquoted name in 3 (or
    // bare-identifier name in 4 for ids-only kinds like interfaces). Compiled
    // once because we scan every .al file for every imported module.
    //
    // The trailing `(?!\s*=)` rejects permissionset permission entries like
    // <c>query "Sent Emails" = X,</c> — those share the `kind "Name"` shape
    // but assign permissions instead of opening a body. The bare-name
    // alternative requires a letter / underscore start (AL identifier
    // rules) so the regex can't backtrack past a failing quoted-name
    // lookahead and accept the numeric id as the name (e.g.
    // <c>page 21 "Customer Card" = X,</c> would otherwise match with
    // bare="21"). Without these two guards, the permissionset file
    // claims every object kind/name it permissions, and real object
    // files lose the link (e.g. `query 8889 "Sent Emails"` pointing at
    // `EmailObjects.PermissionSet.al` instead of `SentEmails.Query.al`,
    // with the outline showing permissionset entries instead of the
    // query's columns).
    internal static readonly Regex ObjectHeaderRegex = new(
        """^\s*(codeunit|table|page|report|xmlport|query|controladdin|enum|interface|permissionset|tableextension|pageextension|reportextension|enumextension|permissionsetextension)\s+(?:(\d+)\s+)?(?:"(?<quoted>[^"]+)"|(?<bare>[A-Za-z_]\w*))(?!\s*=)(?:\s+extends\s+(?:"(?<exquoted>[^"]+)"|(?<exbare>[A-Za-z_]\w*)))?""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // `SourceTable = "X";` (or `SourceTable = X;` for unquoted ids).
    // Used as a fallback to populate `oe_module_objects.source_table_name`
    // for reports whose source-table property nests inside `requestpage
    // { … }` and doesn't always surface on the symbol package's
    // top-level Properties list. Source-side scan runs once per file
    // alongside the existing header scan in <see cref="ScanFileDeclarations"/>.
    internal static readonly Regex SourceTablePropertyRegex = new(
        """^\s*SourceTable\s*=\s*(?:"(?<quoted>[^"]+)"|(?<bare>[A-Za-z_]\w*))\s*;""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // ── Resolution helpers ──────────────────────────────────────────────

    /// <summary>
    /// Maps a parser-side <see cref="SymbolTypeRef"/> into the
    /// <c>(kind, id, name, typeKeyword)</c> tuple the entity rows need.
    /// Returns nulls for non-AL types so callers can skip the reference row.
    /// </summary>
    internal static (string? Kind, int? Id, string? Name, string? TypeKeyword) ResolveVariableTarget(SymbolTypeRef type, Guid importingAppId)
    {
        // Preserve the TypeKeyword regardless of whether it maps to an
        // AL catalog object kind. The chain walker uses TypeKeyword to
        // route DotNet variables down a silence branch (no AL members
        // to resolve) — without preserving "DotNet" here, every
        // `OfficeHost.Foo()` / `HttpStatusCode.X()` chain through a
        // DotNet-typed global lands as head-var-type-unresolved
        // because the catalog doesn't know the .NET type name. Same
        // applies to any non-mapped keyword the AL grammar might
        // surface in the future: we lose nothing by storing it.
        if (!TypeKeywordToObjectKind.TryGetValue(type.Name, out var kind))
        {
            return (null, null, null, type.Name);
        }
        if (string.IsNullOrEmpty(type.ObjectName))
        {
            return (null, null, null, type.Name);
        }
        return (kind, type.ObjectId, type.ObjectName, type.Name);
    }

    /// <summary>
    /// Pulls the SourceTable property value out of a page /
    /// pageextension's symbol-package properties. The value shape has
    /// drifted across BC versions:
    /// <list type="bullet">
    ///   <item>Legacy (pre-28.x): <c>#&lt;32hex&gt;#&lt;name&gt;</c>
    ///         hash-ref — same shape as <c>TableNo</c> / <c>ExtendsTarget</c>.
    ///         <see cref="ParseHashRef"/> extracts the name.</item>
    ///   <item>Modern (28.x+): bare numeric object id (<c>"36"</c> for
    ///         Sales Header). We pass it through and let
    ///         <see cref="ResolveNumericSourceTableNamesAsync"/> swap
    ///         it for the table's name after all tables in the release
    ///         are imported.</item>
    ///   <item>Some packages emit the bare name. Pass-through too.</item>
    /// </list>
    /// Returns null for kinds without the property. Pageextensions
    /// don't carry SourceTable in their own properties — they inherit
    /// it from the base page, which a second-pass copy
    /// (<see cref="PropagateSourceTableToPageExtensionsAsync"/>) fills.
    /// </summary>
    /// <summary>Trims a free-text field and collapses empty / whitespace-only input to null.</summary>
    internal static string? NullIfBlank(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    internal static string? ExtractSourceTableName(SymbolObject symObj)
    {
        // Three properties bind Rec to a specific table:
        //   - SourceTable on a page / pageextension
        //   - TableNo on a codeunit (sets Rec when the codeunit is run
        //     as the OnRun trigger receiver — `codeunit "Gen. Jnl.-Post"`
        //     with `TableNo = "Gen. Journal Line"` runs against a
        //     journal-line record, with Rec bound to that table inside
        //     OnRun and any procedures called from it)
        // We funnel all three through the same column on oe_module_objects
        // (source_table_name); the AL extractor binds Rec to whatever
        // table is named there regardless of which property populated it.
        var propName = symObj.Kind switch
        {
            "page" or "pageextension" => "SourceTable",
            "codeunit" => "TableNo",
            // Reports declare the request-page's data source inside
            // a nested `requestpage { SourceTable = "X"; }` block;
            // the symbol package flattens that onto the report's
            // own Properties list (BC ships it as a top-level
            // SourceTable hash-ref). Pick it up here so the walker
            // can bind `Rec` to that table — Whse. Change Unit of
            // Measure / VAT Report Suggest Lines / similar shapes.
            "report" or "reportextension" => "SourceTable",
            _ => null,
        };
        if (propName is null) return null;

        var prop = symObj.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));
        if (prop is null) return null;
        var (_, name) = ParseHashRef(prop.Value);
        // Some symbol packages emit the bare table name when the table
        // lives in the same module; modern BC ships the numeric object
        // id. Accept all three shapes — ResolveNumericSourceTableNamesAsync
        // normalises the numeric form to a name after import.
        return name ?? (string.IsNullOrEmpty(prop.Value) ? null : prop.Value);
    }

    internal static (Guid? AppId, string? Name) ParseHashRef(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '#') return (null, null);
        var second = raw.IndexOf('#', 1);
        if (second != 33) return (null, null);
        if (!Guid.TryParseExact(raw.AsSpan(1, 32), "N", out var guid)) return (null, null);
        return (guid, raw.Substring(34));
    }

    internal static string RenderSignature(SymbolMethod method)
    {
        if (method.Parameters.Count == 0) return "()";
        var parts = method.Parameters.Select(p => $"{p.Name}: {p.Type.ObjectName ?? p.Type.Name}");
        return "(" + string.Join(", ", parts) + ")";
    }

    internal static string SerializeDeps(IReadOnlyList<AppDependency> deps)
    {
        if (deps.Count == 0) return "[]";
        // Name / Publisher / Version are free-form text from an untrusted .app
        // manifest and can carry control characters that a hand-rolled escaper
        // (which only handled \ and ") would emit as invalid JSON, breaking the
        // later deserialize. Let System.Text.Json escape correctly.
        var projected = deps.Select(d => new
        {
            id = d.AppId.ToString(),
            name = d.Name,
            publisher = d.Publisher,
            version = d.Version,
        });
        return JsonSerializer.Serialize(projected);
    }

    // ── Source-zip handling ─────────────────────────────────────────────

    internal static IEnumerable<(string Path, string Content)> ReadSourceZip(Stream zipStream)
    {
        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;

            // Funnel every layout through the shared canonicaliser so the
            // BC 28.x "<App Name>/src/..." wrapper and the BC 25.x
            // "src/..." form land on the same key as the symbol package's
            // ReferenceSourceFileName.
            var path = AppPackageReader.CanonicalizeSourcePath(entry.FullName);

            using var s = AppPackageReader.OpenCapped(entry);
            using var reader = new StreamReader(s);
            yield return (path, reader.ReadToEnd());
        }
    }

    // ── Source declaration scan ─────────────────────────────────────────

    /// <summary>
    /// One <c>(kind, name)</c> declaration found by scanning a
    /// <c>.al</c> file's header. The file reference plus the 1-based
    /// declaration line are both captured so the per-object loop can
    /// link <c>ModuleObject.SourceFileId</c> and stamp
    /// <c>ModuleObject.LineNumber</c> in one lookup.
    /// </summary>
    internal readonly record struct DeclarationHit(OeModuleFile File, int Line, string? ExtendsName, string? SourceTable);

    private sealed class DeclarationKeyComparer : IEqualityComparer<(string Kind, string Name)>
    {
        public static readonly DeclarationKeyComparer Instance = new();
        public bool Equals((string Kind, string Name) x, (string Kind, string Name) y)
            => string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Kind, string Name) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    /// <summary>
    /// Walks every <c>.al</c> file once and indexes its top-level
    /// <c>&lt;kind&gt; [&lt;id&gt;] &lt;name&gt;</c> declaration by
    /// <c>(Kind, Name)</c>. The map drives the symbol-package
    /// <c>(Kind, Name) → ModuleFile</c> link, sidestepping the
    /// fragile <c>ReferenceSourceFileName</c> path-string lookup —
    /// the symbol package's path strings aren't consistent within a
    /// single BC release (some modules ship them with a nested
    /// <c>src/</c>, others with a project-folder prefix, others raw),
    /// so canonicalising the .Source.zip side alone can't bridge the
    /// gap. The header declaration is deterministic and AL enforces
    /// one object per file in practice. Multi-object files lose only
    /// the second-and-later objects' links (first match wins) —
    /// rare on first-party modules and acceptable for v1.
    /// </summary>
    internal static (string? ExtendsName, string? SourceTable) ScanFileForHeaderMetadata(string content)
    {
        // Exposed for unit tests. Mirrors the per-file logic in
        // <see cref="ScanFileDeclarations"/> for the first header found,
        // returning the source-side extends + SourceTable metadata so
        // tests can pin the parsing without a TestDb fixture.
        bool sawHeader = false;
        string? extendsName = null;
        string? sourceTable = null;
        foreach (var rawLine in content.Split('\n'))
        {
            if (!sawHeader)
            {
                var m = ObjectHeaderRegex.Match(rawLine);
                if (m.Success)
                {
                    sawHeader = true;
                    if (m.Groups["exquoted"].Success) extendsName = m.Groups["exquoted"].Value;
                    else if (m.Groups["exbare"].Success) extendsName = m.Groups["exbare"].Value;
                    continue;
                }
            }
            if (sourceTable is null)
            {
                var st = SourceTablePropertyRegex.Match(rawLine);
                if (st.Success)
                {
                    sourceTable = st.Groups["quoted"].Success
                        ? st.Groups["quoted"].Value
                        : st.Groups["bare"].Value;
                }
            }
        }
        return (extendsName, sourceTable);
    }

    internal static Dictionary<(string Kind, string Name), DeclarationHit> ScanFileDeclarations(
        IReadOnlyDictionary<string, OeModuleFile> filesByPath,
        IReadOnlyDictionary<string, string> sourceFiles)
    {
        var result = new Dictionary<(string, string), DeclarationHit>(DeclarationKeyComparer.Instance);
        foreach (var (path, file) in filesByPath)
        {
            // Source text now lives in the shared content store, not on the
            // file entity — read it from the in-memory upload map by path.
            if (!sourceFiles.TryGetValue(path, out var content)) continue;
            int line = 0;
            // First-header-wins; SourceTable found anywhere in the file
            // is attributed to that first header. AL practice is one
            // object per file, so this lines up with the rest of the
            // import pipeline's first-match assumption.
            (string Kind, string Name)? firstHeaderKey = null;
            string? firstHeaderSourceTable = null;
            foreach (var rawLine in content.Split('\n'))
            {
                line++;
                var m = ObjectHeaderRegex.Match(rawLine);
                if (m.Success)
                {
                    var kind = m.Groups[1].Value.ToLowerInvariant();
                    var name = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value;
                    // Source-side `extends` capture. Used as a fallback for
                    // interface inheritance (`interface "Cost Adjustment With
                    // Params" extends "Inventory Adjustment"`) where the
                    // symbol package doesn't surface the extended-interface
                    // metadata via the usual Target / TargetObject path.
                    string? extends = null;
                    if (m.Groups["exquoted"].Success) extends = m.Groups["exquoted"].Value;
                    else if (m.Groups["exbare"].Success) extends = m.Groups["exbare"].Value;
                    if (result.TryAdd((kind, name), new DeclarationHit(file, line, extends, null)))
                    {
                        firstHeaderKey ??= (kind, name);
                    }
                    continue;
                }
                // Source-side SourceTable capture. Used as a fallback for
                // reports whose request-page-level SourceTable property
                // doesn't surface on the symbol package's top-level
                // properties list (Whse. Change Unit of Measure /
                // VAT Report Suggest Lines shape).
                if (firstHeaderSourceTable is null)
                {
                    var st = SourceTablePropertyRegex.Match(rawLine);
                    if (st.Success)
                    {
                        firstHeaderSourceTable = st.Groups["quoted"].Success
                            ? st.Groups["quoted"].Value
                            : st.Groups["bare"].Value;
                    }
                }
            }
            if (firstHeaderKey is (string k, string n) && firstHeaderSourceTable is not null
                && result.TryGetValue((k, n), out var existing)
                && existing.SourceTable is null)
            {
                result[(k, n)] = existing with { SourceTable = firstHeaderSourceTable };
            }
        }
        return result;
    }

    /// <summary>
    /// Runs <see cref="AlSymbolExtractor.Extract"/> over every imported
    /// source file. The extractor is regex-based and cheap — one pass per
    /// file at import time replaces the historical "filled in by a
    /// source-scan pass later" placeholder and unblocks the outline panel.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<AlSymbol>> ExtractSubSymbolsByFile(
        IReadOnlyDictionary<string, string> sourceFiles)
    {
        var result = new Dictionary<string, IReadOnlyList<AlSymbol>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in sourceFiles)
        {
            result[path] = AlSymbolExtractor.Extract(content);
        }
        return result;
    }
}
