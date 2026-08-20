using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The source-file viewer surface: file content + header, the per-file
/// outline, and the click-to-navigate helpers (declarations, go-to-definition,
/// resolvable spans, find-in-file). Split out of <see cref="ObjectExplorerService"/>
/// so the viewer/navigation logic stands on its own. The outline's
/// "implemented by" section is enriched via <see cref="ReferenceQueryService"/>
/// (the same query the object outline uses); cursor-resolution leans on the
/// static <see cref="Al.AlGoToDefinitionLocator"/>. All reads are
/// <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>.
/// </summary>
public sealed class SourceViewerService
{
    private readonly AppDbContext _db;
    private readonly ReferenceQueryService _references;

    public SourceViewerService(AppDbContext db, ReferenceQueryService references)
    {
        _db = db;
        _references = references;
    }

    // ── Source file viewer ─────────────────────────────────────────────

    public Task<SourceFileDetail?> GetFileAsync(long fileId, CancellationToken ct = default)
        => _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileDetail(f.Id, f.ModuleId, f.Path, f.FileContent!.Content, f.LineCount))
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Header projection for the source-file viewer's breadcrumb. Separate
    /// from <see cref="GetFileAsync"/> so the breadcrumb call doesn't have
    /// to drag the full Content blob through.
    /// </summary>
    public Task<SourceFileHeader?> GetFileHeaderAsync(long fileId, CancellationToken ct = default)
        => _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileHeader(
                f.Id, f.ModuleId, f.Module!.Name,
                f.Module.ReleaseId, f.Module.Release!.Label,
                f.Path, f.LineCount,
                // AL enforces one object per file in practice so picking the
                // first attached object's namespace is unambiguous. ModuleFile
                // has no inverse collection nav onto ModuleObject (the FK
                // direction is one-way, with SetNull on delete), so this is
                // a correlated subquery rather than a navigation traversal.
                // Skips gracefully when the file isn't backing an object.
                _db.OeModuleObjects.AsNoTracking()
                    .Where(o => o.SourceFileId == f.Id && o.Namespace != null)
                    .Select(o => o.Namespace)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Flattens objects + their symbols inside a single source file into one
    /// outline list ordered by line. Feeds the right-hand "outline" panel on
    /// the source viewer.
    /// </summary>
    public async Task<List<SourceFileOutlineItem>> GetFileOutlineAsync(long fileId, CancellationToken ct = default)
    {
        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber, o.ModuleId })
            .ToListAsync(ct);

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.LineNumber > 0)
            .Select(s => new { s.Id, s.ObjectId, s.Kind, s.Name, s.Signature, s.ReturnType, s.LineNumber, s.EndLine })
            .ToListAsync(ct);

        var items = new List<SourceFileOutlineItem>(objects.Count + symbols.Count);
        foreach (var o in objects)
        {
            items.Add(new SourceFileOutlineItem(o.Kind, o.Name, null, o.LineNumber, o.Id));
        }
        foreach (var s in symbols)
        {
            items.Add(new SourceFileOutlineItem(
                s.Kind, s.Name, s.Signature, s.LineNumber, null, s.Id, s.EndLine, s.ReturnType));
        }

        // For interface files, append synthetic "implemented_by" rows
        // for every codeunit in the visible module chain that declares
        // this interface in its `implements` clause. Synthetic items
        // carry LineNumber = int.MaxValue so they sort to the bottom of
        // the outline; the source-viewer's outline grouper buckets them
        // into a dedicated "IMPLEMENTED BY" section.
        var interfaceObj = objects.FirstOrDefault(o => string.Equals(o.Kind, "interface", StringComparison.OrdinalIgnoreCase));
        if (interfaceObj is not null)
        {
            var implementers = await _references.FindInterfaceImplementersAsync(
                interfaceObj.ModuleId, interfaceObj.Name, ct);
            foreach (var impl in implementers)
            {
                items.Add(new SourceFileOutlineItem(
                    Kind: "implemented_by",
                    Name: impl.SourceObjectName,
                    Signature: impl.SourceModuleName,
                    LineNumber: int.MaxValue,
                    ObjectId: impl.SourceObjectId));
            }
        }

        return items.OrderBy(i => i.LineNumber).ToList();
    }

    // ── Source-viewer navigation ──────────────────────────────────────

    /// <summary>
    /// Returns decoration ranges the source viewer can stamp onto each
    /// object-header token so it hovers, underlines, and surfaces the
    /// "Find references" right-click menu. The <c>SymbolId</c> on each row
    /// is the <c>oe_module_objects.id</c> — the page maps it back into a
    /// navigation to the object detail's Find-references panel.
    /// </summary>
    public async Task<List<ALDevToolbox.Components.Shared.CodeViewerDeclaration>> ListDeclarationsInFileAsync(
        long fileId, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return new();

        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber })
            .ToListAsync(ct);

        // Sub-symbol declarations (procedures, fields, triggers, event
        // subscribers). oe_module_symbols already stamps 1-based
        // line/column spans at import via AlSymbolExtractor, so we don't
        // need a re-scan here — symbol rows with LineNumber > 0 are
        // declared in source and can be made clickable directly.
        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId
                && s.LineNumber > 0
                && s.ColumnEnd > s.ColumnStart)
            .Select(s => new
            {
                s.Id, s.Kind, s.Name, s.LineNumber, s.ColumnStart, s.ColumnEnd,
                OwnerKind = s.Object!.Kind,
            })
            .ToListAsync(ct);

        var lines = OeSourceText.SplitLines(content);
        var result = new List<ALDevToolbox.Components.Shared.CodeViewerDeclaration>(objects.Count + symbols.Count);
        foreach (var obj in objects)
        {
            if (obj.LineNumber < 1 || obj.LineNumber > lines.Length) continue;
            var lineText = lines[obj.LineNumber - 1];

            // BC declarations typically quote the name —
            // `codeunit 80 "Sales-Post"`. Bare-identifier names (test code,
            // some old code) are matched as a fallback.
            int colStart, colEnd;
            var quoted = "\"" + obj.Name + "\"";
            var idx = lineText.IndexOf(quoted, StringComparison.Ordinal);
            if (idx >= 0)
            {
                colStart = idx + 1;
                colEnd = idx + 1 + quoted.Length;
            }
            else
            {
                idx = lineText.IndexOf(obj.Name, StringComparison.Ordinal);
                if (idx < 0) continue;
                colStart = idx + 1;
                colEnd = idx + 1 + obj.Name.Length;
            }

            result.Add(new ALDevToolbox.Components.Shared.CodeViewerDeclaration(
                SymbolId: obj.Id,
                Line: obj.LineNumber,
                ColumnStart: colStart,
                ColumnEnd: colEnd,
                Kind: obj.Kind,
                Name: obj.Name));
        }

        foreach (var sym in symbols)
        {
            result.Add(new ALDevToolbox.Components.Shared.CodeViewerDeclaration(
                SymbolId: sym.Id,
                Line: sym.LineNumber,
                ColumnStart: sym.ColumnStart,
                ColumnEnd: sym.ColumnEnd,
                Kind: sym.Kind,
                Name: sym.Name,
                IsMemberSymbol: true,
                OwnerKind: sym.OwnerKind));
        }

        // Objects are appended before member symbols, so the raw list isn't
        // ordered by position — a file shipping several objects (an extension
        // bundling multiple objects in one .al) interleaves their headers and
        // members out of order. The source viewer feeds these straight into
        // CodeMirror's RangeSetBuilder, which requires ascending `from`, so
        // sort by (line, column) before handing them over.
        return result.OrderBy(d => d.Line).ThenBy(d => d.ColumnStart).ToList();
    }

    /// <summary>
    /// Resolves a Cmd/Ctrl-click in the source viewer to a navigation
    /// target. Two strategies in order:
    ///
    /// 1. <b>Member-access</b>: when the clicked token matches a
    ///    <c>method_call</c> / <c>field_access</c> reference row on the same
    ///    file + line, follow <c>TargetSymbolId</c> to the
    ///    <see cref="ModuleSymbol"/> declaration and return its file + line.
    ///    This is the path that resolves <c>GLAcc."Account Type"</c> and
    ///    <c>ConfirmManagement.GetResponseOrDefault</c> — the dominant cases
    ///    that the legacy object-name fallback couldn't reach.
    /// 2. <b>Object-name</b>: same-Release lookup against
    ///    <c>oe_module_objects.Name</c>. Catches bare type literals like
    ///    <c>Customer</c> / <c>"Sales-Post"</c> that the extractor doesn't
    ///    emit member-rows for.
    ///
    /// Returns <c>null</c> when neither strategy matches — the page no-ops
    /// and shows the "No definition found" notice.
    /// </summary>
    public async Task<GoToDefinitionTarget?> GoToDefinitionAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { Content = f.FileContent!.Content, ReleaseId = f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (meta is null) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;
        var word = click.Word;

        // 1. Member-access strategy. Phase-2 extraction stamps
        //    method_call / field_access / event_publisher / label_use
        //    rows with (LineNumber, TargetMemberName, TargetSymbolId).
        //    Match the clicked word case-insensitively (AL identifiers
        //    are case-insensitive). Prefer rows with a resolved
        //    TargetSymbolId — those have a direct file + line via the
        //    symbol's owner object.
        var memberHit = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call"
                    || r.ReferenceKind == "field_access"
                    || r.ReferenceKind == "event_publisher"
                    || r.ReferenceKind == "label_use")
                && r.SourceObject!.SourceFileId == fileId
                && r.LineNumber == line
                && r.TargetMemberName != null
                && r.TargetMemberName.ToLower() == word.ToLower())
            .Where(r => r.TargetSymbolId != null)
            .Select(r => new
            {
                SymbolLine = r.TargetSymbol!.LineNumber,
                SymbolFileId = r.TargetSymbol!.Object!.SourceFileId,
            })
            .Where(x => x.SymbolFileId != null)
            .FirstOrDefaultAsync(ct);
        if (memberHit is not null)
        {
            return new GoToDefinitionTarget(memberHit.SymbolFileId!.Value, memberHit.SymbolLine);
        }

        // 2. Local-variable-declaration strategy. The click landed on
        //    an identifier that has a `VarName: Kind "TypeName"`
        //    declaration somewhere in the file — almost always a
        //    local var like `PaymentMethod: Record "Payment Method"`.
        //    The user expects Go-to-definition to land on the
        //    DECLARATION LINE in this file, not on the underlying
        //    type's source: typing `PaymentMethod` everywhere refers
        //    to the variable, so navigating to "where this variable
        //    was declared" is the IDE-conventional behaviour. The
        //    matching click on the underlined type-name token
        //    (`"Payment Method"` itself) still resolves through the
        //    object-name lookup below.
        //
        //    Earlier shape of this step navigated to the type — that
        //    was a temporary workaround for the bug where a bare
        //    variable name was getting object-name-looked-up and
        //    landing on an unrelated tableextension. With Go-to-def
        //    now ending on the declaration line, the user sees the
        //    type-name token right there and can Cmd-click it to
        //    reach the type source if they want it.
        var declLine = Services.Al.AlGoToDefinitionLocator
            .ResolveVariableDeclarationLine(meta.Content, word);
        if (declLine is int targetLine)
        {
            return new GoToDefinitionTarget(fileId, targetLine);
        }

        // 3. Object-name lookup across the visible release chain. Walks
        //    parent_release_id (child shadows parent) so a base object
        //    referenced from a project Release lands on the ancestor Release
        //    that defines it — e.g. clicking `Customer` in a Dansani file
        //    navigates to the base table in the BC parent Release. See
        //    ChainObjectResolution.
        var target = await ChainObjectResolution.ResolveObjectAsync(
            _db, meta.ReleaseId, word, kind: null, objectId: null, ct);
        if (target?.SourceFileId is null) return null;
        return new GoToDefinitionTarget(target.SourceFileId.Value, target.LineNumber);
    }

    /// <summary>
    /// Spans inside <paramref name="fileId"/> that the source viewer should
    /// underline as resolvable. Drives the IDE-style "what's clickable"
    /// affordance: every token underlined here will, on right-click or
    /// Cmd-click, resolve to a definition via <see cref="GoToDefinitionAsync"/>.
    ///
    /// Sources from phase-2 <c>method_call</c> / <c>field_access</c> reference
    /// rows: each row carries <c>LineNumber</c> + <c>TargetMemberName</c>; we
    /// re-scan the line to recover the 1-based column range. Same scanning
    /// strategy as <see cref="ListDeclarationsInFileAsync"/> — quoted first
    /// (<c>"Account Type"</c>), bare identifier fallback. Multiple references
    /// on the same line are handled by walking forward through the line text
    /// rather than always picking the first occurrence.
    ///
    /// Variable-declaration types (<c>variable_type</c>, <c>parameter_type</c>,
    /// <c>return_type</c>) aren't included — those reference rows don't carry
    /// a line number; symbol-package extraction doesn't yield source positions.
    /// </summary>
    public async Task<List<ALDevToolbox.Components.Shared.CodeViewerResolvable>>
        ListResolvablesInFileAsync(long fileId, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return new();

        // Pull every source-extracted reference on the file (LineNumber
        // set). Two row shapes contribute spans:
        //   - Member-scoped (method_call / field_access): the underlined
        //     token is the MEMBER name. Go-to-definition resolves via
        //     the row's TargetSymbolId when present, or falls back to
        //     object-name lookup.
        //   - Object-scoped (property_object from SourceTable,
        //     LookupPageID, …): the underlined token is the TARGET
        //     OBJECT name. Go-to-definition resolves via the object-name
        //     lookup. No member-symbol id needed.
        // The line-text scan below uses the per-row Name to find the
        // 1-based column span — same logic for both shapes.
        var rows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.LineNumber != null)
            .Select(r => new
            {
                Line = r.LineNumber!.Value,
                Column = r.ColumnNumber,
                Name = r.TargetMemberName ?? r.TargetObjectName,
                SymbolId = r.TargetSymbolId,
            })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);
        // NB: don't early-return when there are no member-access rows — the
        // `extends_target` second pass below still has work to do. An extension
        // object whose body has no resolved method_call / field_access rows
        // (e.g. a pageextension that only adds fields) would otherwise lose the
        // underline + go-to-definition on its `extends "Base"` target.

        var lines = OeSourceText.SplitLines(content);
        var result = new List<ALDevToolbox.Components.Shared.CodeViewerResolvable>(rows.Count);
        // Group by line so the text-search fallback below can walk forward
        // through multiple references on the same line without re-finding
        // the first occurrence each time. Rows with `Column` set bypass
        // the search entirely.
        foreach (var byLine in rows.GroupBy(r => r.Line))
        {
            if (byLine.Key < 1 || byLine.Key > lines.Length) continue;
            var lineText = lines[byLine.Key - 1];
            // Track per-name search cursors for the text-search path so
            // successive occurrences of the same identifier on one line
            // each get their own span.
            var cursors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in byLine)
            {
                // Fast path: the extractor stamped the column at emission
                // time. Use it directly and skip the text-search — the
                // search lands on the leftmost occurrence which is wrong
                // when the same identifier appears twice on a line (e.g.
                // `field("No."; Rec."No.")` should underline the RHS
                // Rec."No.", not the LHS control name).
                if (row.Column is { } colStart && colStart >= 1
                    && colStart <= lineText.Length + 1)
                {
                    var col0 = colStart - 1;
                    var nameLen = row.Name!.Length;
                    // The stored column points at the FIRST char of the
                    // identifier. If the source has a quote there, the
                    // underline span needs to include the quotes too.
                    var matchLen = (col0 < lineText.Length && lineText[col0] == '"')
                        ? nameLen + 2
                        : nameLen;
                    result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                        Line: byLine.Key,
                        ColumnStart: colStart,
                        ColumnEnd: colStart + matchLen,
                        SymbolId: row.SymbolId));
                    continue;
                }

                // Fallback for legacy rows imported before column_number
                // existed: walk the line text forward to find the name.
                var quoted = "\"" + row.Name + "\"";
                var cursor = cursors.TryGetValue(row.Name!, out var c) ? c : 0;
                int idx;
                int fallbackLen;
                idx = lineText.IndexOf(quoted, cursor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    fallbackLen = quoted.Length;
                }
                else
                {
                    idx = Services.Al.AlGoToDefinitionLocator.IndexOfWord(lineText, row.Name!, cursor);
                    if (idx < 0) continue;
                    fallbackLen = row.Name!.Length;
                }
                cursors[row.Name!] = idx + fallbackLen;
                result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                    Line: byLine.Key,
                    ColumnStart: idx + 1,
                    ColumnEnd: idx + 1 + fallbackLen,
                    SymbolId: row.SymbolId));
            }
        }

        // Second pass: `extends_target` rows. The importer doesn't stamp
        // a line/column on them (the extends target sits in the object
        // header, not somewhere in the body), so they fall outside the
        // LineNumber != null filter above. Recover the range by joining
        // each row to its source object's header line and scanning that
        // line for the extends keyword + target name. The user
        // reported the `tableextension … extends "Gen. Journal Line"`
        // base name showing no underline; this is what restores it.
        var extendsRows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.ReferenceKind == "extends_target"
                && r.SourceObject!.LineNumber > 0
                && r.TargetObjectName != null)
            .Select(r => new
            {
                Line = r.SourceObject!.LineNumber,
                Name = r.TargetObjectName!,
            })
            .ToListAsync(ct);
        foreach (var row in extendsRows)
        {
            if (row.Line < 1 || row.Line > lines.Length) continue;
            var span = Services.Al.AlGoToDefinitionLocator.FindExtendsTargetSpan(lines[row.Line - 1], row.Name);
            if (span is null) continue;
            result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                Line: row.Line,
                ColumnStart: span.Value.Start,
                ColumnEnd: span.Value.End));
        }

        return result;
    }

    /// <summary>
    /// Describes one symbol for the source viewer's hover card. Read-only and
    /// tenant-scoped through the usual EF query filter; returns null when the
    /// id doesn't resolve inside the caller's org.
    /// </summary>
    public async Task<SymbolCard?> DescribeSymbolAsync(long symbolId, CancellationToken ct = default)
    {
        return await _db.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Id == symbolId)
            .Select(sym => new SymbolCard(
                sym.Id,
                sym.Name,
                sym.Kind,
                sym.Signature,
                sym.Object!.Kind,
                sym.Object!.Name,
                sym.Module!.Name,
                sym.Object!.SourceFile!.Path,
                sym.Object!.SourceFileId,
                sym.LineNumber))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// "Find in this file" — extracts the word at the supplied click position
    /// and returns every line of the same file that contains it.
    /// </summary>
    public async Task<FileWordSearch?> FindInFileAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;

        var word = click.Word;
        var occurrences = new List<FileWordOccurrence>();
        var lines = OeSourceText.SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(word, StringComparison.Ordinal))
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.Length > 200) trimmed = trimmed[..200] + "…";
                occurrences.Add(new FileWordOccurrence(i + 1, trimmed));
            }
        }
        return new FileWordSearch(word, occurrences);
    }

    // ── Explorer tree ──────────────────────────────────────────────────

    /// <summary>
    /// The left-hand explorer tree, opened just far enough to show
    /// <paramref name="fileId"/>: every module in the release at depth 0, and
    /// under the one holding this file, the folder chain down to it with each
    /// level's siblings.
    ///
    /// Deliberately *not* the whole tree. A Base Application module carries
    /// thousands of source files; the closed carets fetch their children from
    /// <see cref="GetTreeChildrenAsync"/> on first open instead.
    /// </summary>
    public async Task<List<OeTreeNode>> GetExplorerTreeAsync(
        long fileId, string grouping = TreeGrouping.Folder, CancellationToken ct = default)
    {
        var file = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.ModuleId, f.Path, f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (file is null) return [];

        // The same three exclusions ObjectExplorerService.ListModulesAsync
        // applies by default. Without them the tree padded a DVD release with
        // test apps, internal apps and every language pack, and its count
        // disagreed with the release page the reader had just come from. The
        // open file's own module is always kept, whatever it is — the tree
        // cannot omit the branch it exists to show.
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == file.ReleaseId
                     && (m.Id == file.ModuleId
                         || (!m.IsTest && !m.IsInternal && !m.IsLanguagePack)))
            .OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.Version, HasFiles = m.Files.Any() })
            .ToListAsync(ct);

        // Only the folder grouping needs the apps around it - that view answers
        // "where does this live". The others answer "what is in here", which is
        // one app's files and nothing else; the search box and switching back
        // to folders are how you cross apps.
        if (grouping != TreeGrouping.Folder)
        {
            return await ListModuleTreeAsync(file.ModuleId, grouping, fileId, ct);
        }

        var nodes = new List<OeTreeNode>(modules.Count + 32);
        foreach (var m in modules)
        {
            nodes.Add(new OeTreeNode(
                Kind: "module",
                Name: m.Name,
                Path: string.Empty,
                ModuleId: m.Id,
                Depth: 0,
                // A module whose .app shipped without embedded source has no
                // files at all. Claiming a caret for it produced a node that
                // opened, showed nothing, and latched itself as loaded so it
                // could never be tried again.
                HasChildren: m.HasFiles,
                IsOpen: m.Id == file.ModuleId,
                IsActive: false,
                Badge: m.Version));

            if (m.Id != file.ModuleId) continue;
            await AppendOpenChainAsync(nodes, m.Id, file.Path, fileId, ct);
        }
        return nodes;
    }

    /// <summary>
    /// Walks the open file's folder chain, splicing each level's children in
    /// directly beneath the folder they belong to so the flat list reads as a
    /// pre-order tree. Stops as soon as a level doesn't contain the next
    /// chain segment — a path that no longer matches the file rows (a stale
    /// deep link, say) leaves the tree open as far as it was true.
    /// </summary>
    private async Task AppendOpenChainAsync(
        List<OeTreeNode> nodes, long moduleId, string path, long fileId, CancellationToken ct)
    {
        var segments = path.Split('/');
        var prefix = string.Empty;
        var insertAt = nodes.Count;

        for (var level = 0; level < segments.Length; level++)
        {
            var children = await GetTreeChildrenAsync(moduleId, prefix, ct);
            if (children.Count == 0) return;

            var isLeafLevel = level == segments.Length - 1;
            var block = children
                .Select(c => c with
                {
                    Depth = level + 1,
                    IsOpen = !isLeafLevel && c.Kind == "folder" && c.Name == segments[level],
                    IsActive = c.FileId == fileId,
                })
                .ToList();

            nodes.InsertRange(insertAt, block);
            if (isLeafLevel) return;

            var openIndex = block.FindIndex(c => c.IsOpen);
            if (openIndex < 0) return;

            insertAt += openIndex + 1;
            prefix += segments[level] + "/";
        }
    }

    /// <summary>
    /// The immediate children of one folder in one module: sub-folders first,
    /// then files, each alphabetical. <paramref name="prefix"/> is empty for
    /// the module root and otherwise ends in <c>/</c>.
    ///
    /// A file row reads as its object's name rather than its file name, which
    /// is what the tree is for — the file name stays on
    /// <see cref="OeTreeNode.FileName"/> for the row's tooltip. Files without
    /// an object (<c>app.json</c>, a permission XML) keep their file name.
    ///
    /// Both halves filter in SQL: the folder half projects each path's first
    /// remaining segment and takes the distinct set, so expanding <c>src/</c>
    /// on a 7,000-file module returns a dozen rows rather than 7,000 paths to
    /// group in memory.
    ///
    /// <c>StartsWith</c> is safe against a folder name holding a LIKE
    /// metacharacter: EF parameterises it as an already-escaped pattern
    /// (<c>src/Mobile\_WMS/%</c>), so <c>src/Mobile_WMS/</c> does not swallow
    /// <c>src/MobileXWMS/</c>. Pinned by
    /// <c>A_folder_name_holding_a_like_metacharacter_does_not_match_its_neighbour</c>
    /// — do not hand-roll the pattern here, which is what would break it.
    /// </summary>
    public async Task<List<OeTreeNode>> GetTreeChildrenAsync(
        long moduleId, string prefix, CancellationToken ct = default)
    {
        prefix ??= string.Empty;
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        var skip = prefix.Length;

        var folders = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId && f.Path.StartsWith(prefix))
            .Select(f => f.Path.Substring(skip))
            .Where(tail => tail.Contains("/"))
            .Select(tail => tail.Substring(0, tail.IndexOf("/")))
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId
                     && f.Path.StartsWith(prefix)
                     && !f.Path.Substring(skip).Contains("/"))
            .Select(f => new
            {
                f.Id,
                Tail = f.Path.Substring(skip),
                Obj = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.LineNumber)
                    .Select(o => new { o.Kind, o.Name, o.ObjectId })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var nodes = new List<OeTreeNode>(folders.Count + Math.Min(files.Count, MaxFilesPerFolder) + 1);
        // Re-sorted here rather than trusting the database's ORDER BY: the file
        // half sorts in memory with an ordinal comparer, and a folder listing
        // whose two halves disagree on where an underscore goes reads as a bug.
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var name in folders)
        {
            nodes.Add(new OeTreeNode(
                Kind: "folder",
                Name: name,
                Path: prefix + name + "/",
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: true,
                IsOpen: false,
                IsActive: false));
        }

        // Sorted by the name the row actually shows. Keying on `Obj.Name`
        // directly sorted a file whose object row has a blank name under the
        // empty string, so it jumped to the top of the list displaying a file
        // name that belonged further down.
        var ordered = files
            .Select(f => new { f.Id, f.Tail, f.Obj, Display = DisplayName(f.Obj?.Name, f.Tail) })
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var f in ordered.Take(MaxFilesPerFolder))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: f.Display,
                Path: prefix + f.Tail,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: f.Id,
                ObjectKind: f.Obj?.Kind,
                FileName: f.Tail,
                Badge: f.Obj?.ObjectId?.ToString()));
        }

        // Say what was left out rather than truncating in silence. The legacy
        // C/AL ingest writes every table into one `CAL/Table/` folder, which
        // is thousands of rows in a 280px rail; the search box reaches them
        // and the tree does not have to.
        var hidden = ordered.Count - MaxFilesPerFolder;
        if (hidden > 0)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: $"{hidden:N0} more - search to find them",
                Path: prefix,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>
    /// How many files one folder draws before it says "and N more". Folders
    /// this large do not occur in an AL layout; they occur in the C/AL ingest,
    /// which slices every object of a kind into one folder.
    /// </summary>
    private const int MaxFilesPerFolder = 400;

    private static string DisplayName(string? objectName, string fileName) =>
        string.IsNullOrWhiteSpace(objectName) ? fileName : objectName;

    /// <summary>
    /// How the explorer arranges one app's files. The folder view is the tree
    /// the handoff draws; the other two exist because a vendor's folder layout
    /// is somebody else's filing system, and the reader usually knows what
    /// kind of object they are after rather than which folder it was filed in.
    /// </summary>
    public static class TreeGrouping
    {
        /// <summary>Apps, then the module's own folders, then files.</summary>
        public const string Folder = "folder";

        /// <summary>One section per AL object kind, files inside.</summary>
        public const string Kind = "kind";

        /// <summary>One alphabetical list of the app's files.</summary>
        public const string None = "none";

        public static string Parse(string? raw) => raw?.ToLowerInvariant() switch
        {
            Kind => Kind,
            None => None,
            _ => Folder,
        };
    }

    /// <summary>
    /// One app's files arranged by <paramref name="grouping"/>, without the
    /// app rows around them. Feeds both the first paint (through
    /// <see cref="GetExplorerTreeAsync"/>) and a live change of grouping,
    /// which is why it returns depths rather than a single level.
    /// </summary>
    public async Task<List<OeTreeNode>> ListModuleTreeAsync(
        long moduleId, string grouping, long? activeFileId = null, CancellationToken ct = default)
    {
        var files = await ListModuleFilesAsync(moduleId, ct);
        var mark = (OeTreeNode n) => n with { IsActive = n.FileId != null && n.FileId == activeFileId };

        if (TreeGrouping.Parse(grouping) != TreeGrouping.Kind)
        {
            return files.Select(mark).ToList();
        }

        // Sections in a fixed reading order rather than alphabetical: an AL
        // developer looks for tables and pages far more often than for a
        // permission set, and the overflow row (if the app was capped) belongs
        // at the end whatever happens.
        var nodes = new List<OeTreeNode>(files.Count + 12);
        var groups = files
            .Where(f => f.Kind == "file")
            .GroupBy(f => KindSectionTitle(f.ObjectKind))
            .OrderBy(g => KindSectionRank(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            nodes.Add(new OeTreeNode(
                Kind: "section",
                Name: group.Key,
                Path: group.Key,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: true,
                IsOpen: true,
                IsActive: false,
                Badge: group.Count().ToString("N0")));
            nodes.AddRange(group.Select(f => mark(f with { Depth = 1 })));
        }

        nodes.AddRange(files.Where(f => f.Kind == "overflow"));
        return nodes;
    }

    /// <summary>
    /// Plural section heading for an object kind. Files with no object at all
    /// (<c>app.json</c>, a permission XML) group under "Other files", which is
    /// what they are.
    /// </summary>
    private static string KindSectionTitle(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "table" => "Tables",
        "tableextension" => "Table extensions",
        "page" => "Pages",
        "pageextension" => "Page extensions",
        "codeunit" => "Codeunits",
        "report" => "Reports",
        "reportextension" => "Report extensions",
        "query" => "Queries",
        "xmlport" => "XMLports",
        "enum" => "Enums",
        "enumextension" => "Enum extensions",
        "interface" => "Interfaces",
        "permissionset" => "Permission sets",
        "permissionsetextension" => "Permission set extensions",
        "controladdin" => "Control add-ins",
        "profile" => "Profiles",
        "menusuite" => "Menu suites",
        "" => "Other files",
        var other => char.ToUpperInvariant(other[0]) + other[1..] + "s",
    };

    private static int KindSectionRank(string title) => title switch
    {
        "Tables" => 0,
        "Table extensions" => 1,
        "Pages" => 2,
        "Page extensions" => 3,
        "Codeunits" => 4,
        "Reports" => 5,
        "Report extensions" => 6,
        "Enums" => 7,
        "Enum extensions" => 8,
        "Queries" => 9,
        "XMLports" => 10,
        "Interfaces" => 11,
        "Other files" => 99,
        _ => 50,
    };

    /// <summary>
    /// Every file in one module as a flat list, for the explorer's flat mode.
    /// The folder tree is the right shape for a vendor layout that groups by
    /// domain; it is the wrong shape when you know the object's name and the
    /// folders are just noise between you and it.
    ///
    /// Capped like a folder listing, and for the same reason — a Base
    /// Application module is thousands of files, and this is the one view that
    /// asks for all of them at once.
    /// </summary>
    public async Task<List<OeTreeNode>> ListModuleFilesAsync(
        long moduleId, CancellationToken ct = default)
    {
        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId)
            .Select(f => new
            {
                f.Id,
                f.Path,
                Obj = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.LineNumber)
                    .Select(o => new { o.Kind, o.Name, o.ObjectId })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return BuildFileNodes(moduleId, files.Select(f =>
            (f.Id, Tail: FileNameOf(f.Path), f.Path, f.Obj?.Kind, f.Obj?.Name, f.Obj?.ObjectId)), ct: ct);
    }

    /// <summary>
    /// Files across the whole release whose object name or path matches
    /// <paramref name="query"/>. Feeds the explorer's search box, which
    /// replaces the tree with its results while it has content.
    ///
    /// Matches the object name first because that is what an AL developer
    /// types; the path is a fallback for the files that have no object
    /// (<c>app.json</c>, a permission XML).
    /// </summary>
    public async Task<List<OeTreeNode>> SearchTreeAsync(
        int releaseId, string query, CancellationToken ct = default)
    {
        var needle = (query ?? string.Empty).Trim();
        if (needle.Length < 2) return [];

        var rows = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Select(f => new
            {
                f.Id,
                f.Path,
                ModuleName = f.Module!.Name,
                f.ModuleId,
                Obj = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.LineNumber)
                    .Select(o => new { o.Kind, o.Name, o.ObjectId })
                    .FirstOrDefault(),
            })
            .Where(f => (f.Obj != null && EF.Functions.ILike(f.Obj.Name, "%" + needle + "%"))
                     || EF.Functions.ILike(f.Path, "%" + needle + "%"))
            .OrderBy(f => f.Obj != null ? f.Obj.Name : f.Path)
            .Take(MaxSearchResults + 1)
            .ToListAsync(ct);

        var nodes = new List<OeTreeNode>(rows.Count);
        foreach (var r in rows.Take(MaxSearchResults))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: DisplayName(r.Obj?.Name, FileNameOf(r.Path)),
                Path: r.Path,
                ModuleId: r.ModuleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: r.Id,
                ObjectKind: r.Obj?.Kind,
                FileName: r.Path,
                // The app, not the object id: a search crosses apps, and which
                // one a hit came from is the thing you cannot tell from a name.
                Badge: r.ModuleName));
        }

        if (rows.Count > MaxSearchResults)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: "More matches than fit - narrow the search",
                Path: string.Empty,
                ModuleId: 0,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>How many search hits the explorer lists before it says so.</summary>
    private const int MaxSearchResults = 200;

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// Shared row-building for the flat views, so a file reads the same
    /// whichever list it turns up in.
    /// </summary>
    private static List<OeTreeNode> BuildFileNodes(
        long moduleId,
        IEnumerable<(long Id, string Tail, string Path, string? Kind, string? Name, int? ObjectId)> files,
        CancellationToken ct = default)
    {
        var ordered = files
            .Select(f => (f.Id, f.Tail, f.Path, f.Kind, f.ObjectId, Display: DisplayName(f.Name, f.Tail)))
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodes = new List<OeTreeNode>(Math.Min(ordered.Count, MaxFilesPerFolder) + 1);
        foreach (var f in ordered.Take(MaxFilesPerFolder))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: f.Display,
                Path: f.Path,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: f.Id,
                ObjectKind: f.Kind,
                FileName: f.Tail,
                Badge: f.ObjectId?.ToString()));
        }

        var hidden = ordered.Count - MaxFilesPerFolder;
        if (hidden > 0)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: $"{hidden:N0} more - search to find them",
                Path: string.Empty,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>
    /// How many AL objects the module holds. Drives the count chip in the
    /// explorer pane's head and the module name in the status line.
    /// </summary>
    public Task<int> CountModuleObjectsAsync(long moduleId, CancellationToken ct = default)
        => _db.OeModuleObjects.AsNoTracking().CountAsync(o => o.ModuleId == moduleId, ct);

}
