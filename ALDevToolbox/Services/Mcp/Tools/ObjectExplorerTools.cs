using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="ObjectExplorerService"/>. The tools
/// accept a release Label ("BC 28.1") or numeric id — natural-language
/// queries reach for the human form first, but agents that already have
/// an id can pass it through. Owner-by-name resolution for
/// <c>find_references</c> looks the target up in <c>oe_module_objects</c>
/// so the agent only has to supply the table/page/codeunit name.
/// </summary>
[McpServerToolType]
public sealed class ObjectExplorerTools
{
    private const int MaxResults = 200;

    private readonly ObjectExplorerService _explorer;
    private readonly AppDbContext _db;

    public ObjectExplorerTools(ObjectExplorerService explorer, AppDbContext db)
    {
        _explorer = explorer;
        _db = db;
    }

    [McpServerTool(Name = "list_releases", ReadOnly = true)]
    [Description("Lists every BC release (the version snapshots from which Object Explorer was populated). Returns each release's id, Label (e.g. 'BC 28.1'), BC version, and status.")]
    public async Task<IReadOnlyList<ReleaseListItem>> ListReleasesAsync(CancellationToken ct = default) =>
        await _explorer.ListReleasesAsync(includeSoftDeleted: false, ct);

    [McpServerTool(Name = "search_objects", ReadOnly = true)]
    [Description("Searches a BC release for AL objects (tables, pages, codeunits, reports, etc.) by name or id. Returns the owning module name + source file pointer for each hit.")]
    public async Task<IReadOnlyList<ReleaseObjectMatch>> SearchObjectsAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Substring of the object name (case-insensitive) or a numeric object id.")] string namePattern,
        [Description("Optional AL kind filter ('table', 'page', 'codeunit', 'report', 'query', 'enum', 'interface', 'tableextension', 'pageextension'). Null returns every kind.")] string? kind = null,
        CancellationToken ct = default)
    {
        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        return await _explorer.SearchObjectsInReleaseAsync(
            releaseId,
            new ObjectListFilter(Kind: kind, Search: namePattern),
            MaxResults,
            ct);
    }

    [McpServerTool(Name = "search_procedures", ReadOnly = true)]
    [Description("Searches a BC release for procedures (methods / triggers / event publishers) by name. Returns the owning object + module for each hit.")]
    public async Task<IReadOnlyList<ReleaseProcedureMatch>> SearchProceduresAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Substring of the procedure name.")] string namePattern,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        return await _explorer.SearchProceduresInReleaseAsync(releaseId, namePattern, moduleId, MaxResults, ct);
    }

    [McpServerTool(Name = "search_content", ReadOnly = true)]
    [Description("Searches AL source content across a BC release for a free-text query. Returns the line containing the hit; truncated at ~500 characters per line.")]
    public async Task<IReadOnlyList<ReleaseContentMatch>> SearchContentAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Free-text search query. Matched literally against .al source bodies.")] string query,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        var rows = await _explorer.SearchContentInReleaseAsync(releaseId, query, moduleId, MaxResults, 3, ct);
        // Snippet is whatever the line carries; cap to keep responses bounded.
        return rows.Select(r => r.Snippet.Length > 500
            ? r with { Snippet = r.Snippet[..500] + "…" }
            : r).Take(MaxResults).ToList();
    }

    [McpServerTool(Name = "find_references", ReadOnly = true)]
    [Description("Finds every place in a BC release that references a specific table, page, or codeunit — optionally narrowed to a specific field or procedure on it. Resolves the target by name; pass 'Sales Line' + field 'No.' to answer 'which procedures change the No. field on Sales Line?'-style questions.")]
    public async Task<IReadOnlyList<ReferenceMatch>> FindReferencesAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Name of the target object — table, page, codeunit, etc. Resolved case-insensitively against oe_module_objects.")] string targetObject,
        [Description("AL kind of the target object. Defaults to 'table'.")] string targetKind = "table",
        [Description("Optional field name on the target table — narrows the search to references that touch this field.")] string? targetField = null,
        [Description("Optional procedure name on the target object — narrows the search to references that call this procedure.")] string? targetProcedure = null,
        CancellationToken ct = default)
    {
        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);

        var kind = targetKind.Trim().ToLowerInvariant();
        var owner = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                        && o.Kind == kind
                        && o.Name.ToLower() == targetObject.Trim().ToLower())
            .Include(o => o.Module)
            .FirstOrDefaultAsync(ct);
        if (owner is null || owner.Module is null)
        {
            throw new McpException($"Could not find a {kind} named '{targetObject}' in release {releaseLabelOrId}. Try search_objects to discover the exact name.");
        }

        string? memberName = null;
        string? memberKind = null;
        if (!string.IsNullOrWhiteSpace(targetField))
        {
            memberName = targetField.Trim();
            memberKind = "field";
        }
        else if (!string.IsNullOrWhiteSpace(targetProcedure))
        {
            memberName = targetProcedure.Trim();
            memberKind = "procedure";
        }

        var query = new FindReferencesQuery(
            TargetAppId: owner.Module.AppId,
            TargetObjectKind: owner.Kind,
            TargetObjectId: owner.ObjectId,
            TargetObjectName: owner.Name,
            TargetMemberName: memberName,
            TargetMemberKind: memberKind);

        var matches = await _explorer.FindReferencesAsync(releaseId, query, ct);
        return matches.Count > MaxResults ? matches.Take(MaxResults).ToList() : matches;
    }

    [McpServerTool(Name = "get_object_outline", ReadOnly = true)]
    [Description("Returns the outline of one AL object (table, page, codeunit, etc.) in a BC release — its declared procedures, triggers, event publishers, and fields with their line numbers and symbol ids. Use this to discover what an object exposes before calling get_procedure_source or list_procedure_calls. The returned symbol ids disambiguate procedure name collisions (page actions and table fields each carry an OnAction / OnValidate trigger with the same name).")]
    public async Task<ObjectOutline> GetObjectOutlineAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Name of the target object — resolved case-insensitively against oe_module_objects.")] string objectName,
        [Description("AL kind of the target object. Defaults to 'codeunit'.")] string objectKind = "codeunit",
        CancellationToken ct = default)
    {
        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        var outline = await _explorer.GetObjectOutlineAsync(releaseId, objectKind, objectName, ct);
        if (outline is null)
        {
            throw new McpException($"Could not find a {objectKind.Trim().ToLowerInvariant()} named '{objectName}' in release {releaseLabelOrId}. Try search_objects to discover the exact name.");
        }
        return outline;
    }

    [McpServerTool(Name = "get_procedure_source", ReadOnly = true)]
    [Description("Returns the AL source text of one procedure / trigger body on an object — declaration through matching end. Capped at 200 lines with a truncation marker when the body is longer (call list_procedure_calls or narrow the question in that case). Accept either (objectName, objectKind, procedureName) for the unambiguous case, or symbolId from a prior get_object_outline call when the name is ambiguous (page-action OnAction triggers, table-field OnValidate triggers).")]
    public async Task<ProcedureSource> GetProcedureSourceAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Symbol id from get_object_outline. Preferred when set — disambiguates OnAction / OnValidate collisions and avoids a name resolution round-trip.")] long? symbolId = null,
        [Description("Name of the owning object. Required when symbolId is null.")] string? objectName = null,
        [Description("AL kind of the owning object. Required when symbolId is null.")] string? objectKind = null,
        [Description("Name of the procedure / trigger. Required when symbolId is null.")] string? procedureName = null,
        CancellationToken ct = default)
    {
        var resolvedSymbolId = symbolId ?? await ResolveProcedureSymbolIdAsync(releaseLabelOrId, objectName, objectKind, procedureName, ct);
        var source = await _explorer.GetProcedureSourceAsync(resolvedSymbolId, MaxProcedureSourceLines, ct);
        if (source is null)
        {
            throw new McpException($"Symbol id {resolvedSymbolId} either doesn't exist or doesn't have a source file attached. Call get_object_outline to see the current ids for an object.");
        }
        return source;
    }

    [McpServerTool(Name = "list_procedure_calls", ReadOnly = true)]
    [Description("Returns the outgoing references (method calls, field accesses, type references) emitted from inside one procedure / trigger body. Use this to trace what a procedure does without reading the full source: each row carries the target object + member name + line so the agent can follow the call chain. Same disambiguation rules as get_procedure_source — pass symbolId from get_object_outline when the procedure name is shared (OnAction / OnValidate).")]
    public async Task<IReadOnlyList<ProcedureCall>> ListProcedureCallsAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Symbol id from get_object_outline. Preferred when set.")] long? symbolId = null,
        [Description("Name of the owning object. Required when symbolId is null.")] string? objectName = null,
        [Description("AL kind of the owning object. Required when symbolId is null.")] string? objectKind = null,
        [Description("Name of the procedure / trigger. Required when symbolId is null.")] string? procedureName = null,
        CancellationToken ct = default)
    {
        var resolvedSymbolId = symbolId ?? await ResolveProcedureSymbolIdAsync(releaseLabelOrId, objectName, objectKind, procedureName, ct);
        var calls = await _explorer.ListProcedureCallsAsync(resolvedSymbolId, MaxResults, ct);
        if (calls is null)
        {
            throw new McpException($"Symbol id {resolvedSymbolId} doesn't exist. Call get_object_outline to see the current ids for an object.");
        }
        return calls;
    }

    private const int MaxProcedureSourceLines = 200;

    /// <summary>
    /// Body-bearing symbol kinds — what counts as a "procedure" the
    /// forward-edge tools can read source for. Mirrors the kinds the
    /// procedure walker pushes a scope frame for.
    /// </summary>
    private static readonly HashSet<string> BodyBearingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "procedure", "local_procedure", "internal_procedure", "protected_procedure",
        "trigger", "event_publisher", "event_subscriber",
    };

    /// <summary>
    /// Resolves a procedure / trigger to its <c>oe_module_symbols.id</c>
    /// by (release, object, kind, procedure name). Throws
    /// <see cref="McpException"/> with copy steering the agent to the
    /// outline + symbolId form when the name is ambiguous on the
    /// object (page-action OnAction triggers, table-field OnValidate
    /// triggers, multiple overloads of the same procedure).
    /// </summary>
    private async Task<long> ResolveProcedureSymbolIdAsync(
        string releaseLabelOrId,
        string? objectName,
        string? objectKind,
        string? procedureName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectName)
            || string.IsNullOrWhiteSpace(objectKind)
            || string.IsNullOrWhiteSpace(procedureName))
        {
            throw new McpException("Must supply either symbolId or all of (objectName, objectKind, procedureName).");
        }

        var releaseId = await ResolveReleaseAsync(releaseLabelOrId, ct);
        var ownerKind = objectKind.Trim().ToLowerInvariant();
        var ownerName = objectName.Trim().ToLowerInvariant();
        var procName = procedureName.Trim();
        var procNameLower = procName.ToLowerInvariant();

        var candidates = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId
                        && s.Object.Kind == ownerKind
                        && s.Object.Name.ToLower() == ownerName
                        && s.Name.ToLower() == procNameLower)
            .Select(s => new { s.Id, s.Kind, s.LineNumber })
            .ToListAsync(ct);

        var bodied = candidates.Where(c => BodyBearingKinds.Contains(c.Kind)).ToList();
        if (bodied.Count == 0)
        {
            throw new McpException($"No procedure or trigger named '{procName}' on {ownerKind} '{objectName}' in release {releaseLabelOrId}. Call get_object_outline to see what this object exposes.");
        }
        if (bodied.Count > 1)
        {
            throw new McpException($"Multiple symbols named '{procName}' on {ownerKind} '{objectName}' (typical for page-action OnAction triggers and table-field OnValidate triggers). Call get_object_outline to see their line numbers and ids, then pass symbolId instead of procedureName.");
        }
        return bodied[0].Id;
    }

    private async Task<int> ResolveReleaseAsync(string releaseLabelOrId, CancellationToken ct)
    {
        if (int.TryParse(releaseLabelOrId, out var asId))
        {
            var exists = await _db.OeReleases.AsNoTracking().AnyAsync(r => r.Id == asId && r.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Release {asId} does not exist.");
            return asId;
        }
        var label = releaseLabelOrId.Trim();
        var row = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Label == label && r.DeletedAt == null)
            .Select(r => new { r.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Release '{releaseLabelOrId}' was not found. Call list_releases to see available labels.");
        }
        return row.Id;
    }
}
