using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Tomlyn;
using Tomlyn.Serialization;

namespace ALDevToolbox.Services;

/// <summary>
/// Serialises a generated project's form-post shape to <c>workspace.aldt.toml</c>
/// and parses such a file back into a <see cref="ProjectPlan"/> or
/// <see cref="StandaloneExtensionPlan"/>. Lets users save a workspace's settings
/// alongside the generated ZIP and re-import them later — see Milestone P2.3 in
/// <c>.design/milestones.md</c>.
/// </summary>
/// <remarks>
/// The DB remains the source of truth: parsing validates the referenced template
/// and module keys against live rows, refusing imports that point at deleted or
/// missing entities. Publisher is derived from the picked template's defaults
/// rather than persisted in the config file, so a reused workspace config always
/// reflects the current template setup.
/// </remarks>
public class WorkspaceConfigService
{
    /// <summary>Schema version baked into emitted files. Bump when the on-disk shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The kind value emitted for a workspace config.</summary>
    public const string WorkspaceKind = "workspace";

    /// <summary>The kind value emitted for a standalone-extension config.</summary>
    public const string ExtensionKind = "extension";

    /// <summary>Filename written into the generated ZIP and accepted by the import action.</summary>
    public const string FileName = "workspace.aldt.toml";

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private readonly AppDbContext _db;

    public WorkspaceConfigService(AppDbContext db)
    {
        _db = db;
    }

    // ===== Build (no DB access) =====

    /// <summary>
    /// Serialises a workspace plan to its <c>workspace.aldt.toml</c> form. The
    /// shape is the same one <see cref="ParseAsync"/> reads back; round-trips
    /// produce an equivalent plan modulo per-generation GUIDs (those are
    /// regenerated on every run by design).
    /// </summary>
    public string BuildWorkspace(ProjectPlan plan)
    {
        var doc = new WorkspaceDoc
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = WorkspaceKind,
            Workspace = new WorkspaceSection
            {
                Template = plan.TemplateKey,
                Name = plan.WorkspaceName,
                Brief = plan.Brief,
                Description = plan.Description,
                ApplicationVersion = plan.ApplicationVersion,
                RuntimeVersion = plan.RuntimeVersion,
                CoreIdRangeFrom = plan.CoreIdRangeFrom,
                CoreIdRangeTo = plan.CoreIdRangeTo,
                IncludeExamples = plan.IncludeExamples,
                IncludeForNav = plan.IncludeForNav,
                Modules = plan.SelectedModuleKeys.ToList(),
            },
        };
        return PrependHeader(TomlSerializer.Serialize(doc, TomlOptions));
    }

    /// <summary>
    /// Serialises a standalone-extension plan to its <c>workspace.aldt.toml</c>
    /// form. Mirrors <see cref="BuildWorkspace"/> but uses the extension shape.
    /// </summary>
    public string BuildExtension(StandaloneExtensionPlan plan)
    {
        var doc = new ExtensionDoc
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = ExtensionKind,
            Extension = new ExtensionSection
            {
                Template = plan.TemplateKey,
                Name = plan.ExtensionName,
                Brief = plan.Brief,
                Description = plan.Description,
                Publisher = plan.Publisher,
                ApplicationVersion = plan.ApplicationVersion,
                RuntimeVersion = plan.RuntimeVersion,
                IdRangeFrom = plan.IdRangeFrom,
                IdRangeTo = plan.IdRangeTo,
                IncludeExamples = plan.IncludeExamples,
                Dependencies = plan.Dependencies
                    .Select(d => new DependencyConfig
                    {
                        Id = d.DepId,
                        Name = d.DepName,
                        Publisher = d.DepPublisher,
                        Version = d.DepVersion,
                    })
                    .ToList(),
            },
        };
        return PrependHeader(TomlSerializer.Serialize(doc, TomlOptions));
    }

    private static string PrependHeader(string body) =>
        new StringBuilder()
            .AppendLine("# AL Dev Toolbox project config.")
            .AppendLine("# Re-import this file from /projects/new or /projects/extension to recreate the")
            .AppendLine("# project with the same settings, or scaffold a sibling extension against the")
            .AppendLine("# same workspace shape.")
            .Append(body)
            .ToString();

    // ===== Parse (validates against the DB) =====

    /// <summary>
    /// Parses the supplied TOML and validates it against the live database.
    /// Returns whichever shape the file declared via its <c>kind</c> key. Throws
    /// <see cref="PlanValidationException"/> when the file is malformed, points
    /// at a missing/deleted template, or references unknown module keys; the
    /// errors dictionary is keyed for inline display by the caller.
    /// </summary>
    public async Task<WorkspaceConfigImport> ParseAsync(string toml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toml))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = "The file is empty.",
            });
        }

        UnionDoc doc;
        try
        {
            doc = TomlSerializer.Deserialize<UnionDoc>(toml, TomlOptions) ?? new UnionDoc();
        }
        catch (Tomlyn.TomlException tex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = $"Failed to parse TOML: {tex.Message}",
            });
        }
        catch (Exception ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["File"] = $"Failed to parse TOML: {ex.Message}",
            });
        }

        if (doc.SchemaVersion != CurrentSchemaVersion)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["SchemaVersion"] = $"Unsupported schema_version '{doc.SchemaVersion}'. Expected {CurrentSchemaVersion}.",
            });
        }

        return doc.Kind switch
        {
            WorkspaceKind => new WorkspaceConfigImport(WorkspaceKind, await BuildWorkspaceImportAsync(doc, ct), null),
            ExtensionKind => new WorkspaceConfigImport(ExtensionKind, null, await BuildExtensionImportAsync(doc, ct)),
            _ => throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Kind"] = $"Unknown kind '{doc.Kind}'. Expected '{WorkspaceKind}' or '{ExtensionKind}'.",
            }),
        };
    }

    private async Task<ProjectPlan> BuildWorkspaceImportAsync(UnionDoc doc, CancellationToken ct)
    {
        var section = doc.Workspace ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Workspace"] = "Missing [workspace] section.",
        });

        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(section.Template)) errors["Template"] = "Required.";

        // Validate template exists and is active.
        if (!string.IsNullOrWhiteSpace(section.Template))
        {
            var templateExists = await _db.RuntimeTemplates
                .AsNoTracking()
                .AnyAsync(t => t.DeletedAt == null && t.Key == section.Template, ct);
            if (!templateExists)
            {
                errors["Template"] = $"Template '{section.Template}' was not found or has been deleted.";
            }
        }

        // Validate every referenced module key against the live, non-deleted set.
        var unknownModules = new List<string>();
        if (section.Modules.Count > 0)
        {
            var liveKeys = await _db.Modules
                .AsNoTracking()
                .Where(m => m.DeletedAt == null && section.Modules.Contains(m.Key))
                .Select(m => m.Key)
                .ToListAsync(ct);
            var liveSet = liveKeys.ToHashSet(StringComparer.Ordinal);
            unknownModules = section.Modules.Where(k => !liveSet.Contains(k)).ToList();
            if (unknownModules.Count > 0)
            {
                errors["Modules"] = $"Unknown or deleted module key(s): {string.Join(", ", unknownModules)}.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        return new ProjectPlan(
            TemplateKey: section.Template,
            WorkspaceName: section.Name,
            Brief: section.Brief,
            Description: section.Description,
            ApplicationVersion: section.ApplicationVersion,
            RuntimeVersion: section.RuntimeVersion,
            CoreIdRangeFrom: section.CoreIdRangeFrom,
            CoreIdRangeTo: section.CoreIdRangeTo,
            IncludeExamples: section.IncludeExamples,
            IncludeForNav: section.IncludeForNav,
            SelectedModuleKeys: section.Modules.ToList());
    }

    private async Task<StandaloneExtensionPlan> BuildExtensionImportAsync(UnionDoc doc, CancellationToken ct)
    {
        var section = doc.Extension ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Extension"] = "Missing [extension] section.",
        });

        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(section.Template)) errors["Template"] = "Required.";

        if (!string.IsNullOrWhiteSpace(section.Template))
        {
            var templateExists = await _db.RuntimeTemplates
                .AsNoTracking()
                .AnyAsync(t => t.DeletedAt == null && t.Key == section.Template, ct);
            if (!templateExists)
            {
                errors["Template"] = $"Template '{section.Template}' was not found or has been deleted.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        var deps = section.Dependencies
            .Select(d => new DependencyEntry(
                DepId: d.Id,
                DepName: d.Name,
                DepPublisher: d.Publisher,
                DepVersion: d.Version))
            .ToList();

        return new StandaloneExtensionPlan(
            TemplateKey: section.Template,
            ExtensionName: section.Name,
            Brief: section.Brief,
            Description: section.Description,
            ApplicationVersion: section.ApplicationVersion,
            RuntimeVersion: section.RuntimeVersion,
            IdRangeFrom: section.IdRangeFrom,
            IdRangeTo: section.IdRangeTo,
            IncludeExamples: section.IncludeExamples,
            Publisher: section.Publisher,
            Dependencies: deps);
    }

    // ===== TOML POCOs =====

    /// <summary>Emitted shape for kind = "workspace".</summary>
    private class WorkspaceDoc
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Kind { get; set; } = WorkspaceKind;
        public WorkspaceSection Workspace { get; set; } = new();
    }

    /// <summary>Emitted shape for kind = "extension".</summary>
    private class ExtensionDoc
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Kind { get; set; } = ExtensionKind;
        public ExtensionSection Extension { get; set; } = new();
    }

    /// <summary>
    /// Permissive shape used when reading: both sub-tables are nullable so the
    /// deserializer doesn't need a kind-aware preflight pass. The kind field
    /// dispatches to the correct branch.
    /// </summary>
    private class UnionDoc
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = string.Empty;
        public WorkspaceSection? Workspace { get; set; }
        public ExtensionSection? Extension { get; set; }
    }

    private class WorkspaceSection
    {
        public string Template { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Brief { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public int CoreIdRangeFrom { get; set; }
        public int CoreIdRangeTo { get; set; }
        public bool IncludeExamples { get; set; } = true;
        public bool IncludeForNav { get; set; }
        public List<string> Modules { get; set; } = new();
    }

    private class ExtensionSection
    {
        public string Template { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Brief { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public int IdRangeFrom { get; set; }
        public int IdRangeTo { get; set; }
        public bool IncludeExamples { get; set; } = true;
        public List<DependencyConfig> Dependencies { get; set; } = new();
    }

    private class DependencyConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}

/// <summary>
/// Result of <see cref="WorkspaceConfigService.ParseAsync"/>. Exactly one of
/// <see cref="Workspace"/> / <see cref="Extension"/> is non-null, matching the
/// declared <see cref="Kind"/>.
/// </summary>
public record WorkspaceConfigImport(
    string Kind,
    ProjectPlan? Workspace,
    StandaloneExtensionPlan? Extension);
