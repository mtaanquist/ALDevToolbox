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
