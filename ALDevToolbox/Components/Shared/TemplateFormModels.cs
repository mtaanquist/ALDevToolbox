using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Mutable mirror of <see cref="TemplateAuthoring"/> that the structured admin
/// editor binds against. Fields use <c>set;</c> accessors so Razor's
/// <c>@bind</c> can write each keystroke without rebuilding the whole record.
/// <c>ToAuthoring</c> packs the form back into the immutable payload the
/// service layer accepts.
/// </summary>
/// <remarks>
/// The types live in the shared namespace because
/// <see cref="ALDevToolbox.Components.Shared.RecursiveFolderEditor"/> binds
/// against <see cref="TemplateFolderForm"/> too.
/// </remarks>
public sealed class TemplateFormState
{
    public string Key { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Convenience input for <c>defaults.application</c>; spliced into <see cref="DefaultsJson"/> on save.</summary>
    public string DefaultApplication { get; set; } = string.Empty;

    /// <summary>Convenience input for <c>defaults.platform</c>; spliced into <see cref="DefaultsJson"/> on save.</summary>
    public string DefaultPlatform { get; set; } = "1.0.0.0";

    public string DefaultsJson { get; set; } = string.Empty;
    public string AppSourceCopJson { get; set; } = string.Empty;
    public int CoreIdRangeFrom { get; set; }
    public int CoreIdRangeTo { get; set; }
    public int ModuleIdRangeStart { get; set; }
    public int ModuleIdRangeSize { get; set; }
    public bool Deprecated { get; set; }
    public bool IsDefault { get; set; }
    public string DefaultApplicationVersionKey { get; set; } = string.Empty;
    public List<string> DefaultModuleKeys { get; } = new();

    /// <summary>Ordered <c>[[extensions]]</c> declarations under this template.</summary>
    public List<ExtensionForm> Extensions { get; } = new();

    /// <summary>
    /// Builds a fresh blank state with the same default ranges and defaults
    /// JSON the TOML <c>BlankToml</c> starter produces.
    /// </summary>
    public static TemplateFormState Blank() => new()
    {
        DefaultsJson = JsonSerializer.Serialize(new TemplateDefaults(), PrettyJson),
        AppSourceCopJson = JsonSerializer.Serialize(new AppSourceCopSettings(), PrettyJson),
        CoreIdRangeFrom = 90000,
        CoreIdRangeTo = 90999,
        ModuleIdRangeStart = 91000,
        ModuleIdRangeSize = 200,
    };

    /// <summary>Hydrates form state from an authoring payload. Used by both the load path and the TOML → Form switch.</summary>
    public static TemplateFormState From(TemplateAuthoring source)
    {
        var (app, platform) = SplitAppPlatform(source.DefaultsJson);

        var state = new TemplateFormState
        {
            Key = source.Key,
            Runtime = source.Runtime,
            Name = source.Name,
            Description = source.Description,
            DefaultApplication = app,
            DefaultPlatform = string.IsNullOrEmpty(platform) ? "1.0.0.0" : platform,
            DefaultsJson = ReformatJson(source.DefaultsJson, fallback: JsonSerializer.Serialize(new TemplateDefaults(), PrettyJson)),
            AppSourceCopJson = ReformatJson(source.AppSourceCopJson, fallback: JsonSerializer.Serialize(new AppSourceCopSettings(), PrettyJson)),
            CoreIdRangeFrom = source.CoreIdRangeFrom,
            CoreIdRangeTo = source.CoreIdRangeTo,
            ModuleIdRangeStart = source.ModuleIdRangeStart,
            ModuleIdRangeSize = source.ModuleIdRangeSize,
            Deprecated = source.Deprecated,
            IsDefault = source.IsDefault,
            DefaultApplicationVersionKey = source.DefaultApplicationVersionKey ?? string.Empty,
        };
        foreach (var key in source.DefaultModuleKeys) state.DefaultModuleKeys.Add(key);
        foreach (var ext in source.Extensions) state.Extensions.Add(ExtensionForm.From(ext));
        return state;
    }

    /// <summary>
    /// Packs the form back into <see cref="TemplateAuthoring"/>. The dedicated
    /// <see cref="DefaultApplication"/> / <see cref="DefaultPlatform"/> inputs
    /// are spliced into <see cref="DefaultsJson"/> so the service-side
    /// validator sees the user-edited values regardless of what was in the
    /// textarea.
    /// </summary>
    public TemplateAuthoring ToAuthoring() => new(
        Key: Key,
        Runtime: Runtime,
        Name: Name,
        Description: Description,
        DefaultsJson: SpliceAppPlatform(DefaultsJson, DefaultApplication, DefaultPlatform),
        AppSourceCopJson: AppSourceCopJson,
        CoreIdRangeFrom: CoreIdRangeFrom,
        CoreIdRangeTo: CoreIdRangeTo,
        ModuleIdRangeStart: ModuleIdRangeStart,
        ModuleIdRangeSize: ModuleIdRangeSize,
        Deprecated: Deprecated,
        IsDefault: IsDefault,
        DefaultApplicationVersionKey: string.IsNullOrWhiteSpace(DefaultApplicationVersionKey)
            ? null
            : DefaultApplicationVersionKey,
        DefaultModuleKeys: DefaultModuleKeys.ToList(),
        Extensions: Extensions.Select(e => e.ToAuthoring()).ToList());

    internal static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// Round-trips the JSON through the pretty-printer so the form textarea
    /// shows consistent indentation; falls back to <paramref name="fallback"/>
    /// when the input doesn't parse.
    /// </summary>
    private static string ReformatJson(string json, string fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static (string Application, string Platform) SplitAppPlatform(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (string.Empty, string.Empty);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var app = root.TryGetProperty("application", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            var plat = root.TryGetProperty("platform", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            return (app, plat);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Writes <paramref name="application"/> and <paramref name="platform"/>
    /// into the JSON document, overwriting any existing values. The dedicated
    /// inputs always win — if the textarea contains a stale value, it is
    /// silently replaced rather than triggering a validation surprise.
    /// </summary>
    private static string SpliceAppPlatform(string json, string application, string platform)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = new JsonObject();
        }
        if (node is not JsonObject obj)
        {
            obj = new JsonObject();
        }
        obj["application"] = application ?? string.Empty;
        obj["platform"] = platform ?? string.Empty;
        return obj.ToJsonString(PrettyJson);
    }
}

/// <summary>Mutable mirror of <see cref="ExtensionAuthoring"/>.</summary>
public sealed class ExtensionForm
{
    public string Path { get; set; } = string.Empty;
    public string NameTemplate { get; set; } = string.Empty;
    public bool Required { get; set; } = true;

    /// <summary>Optional per-extension override; empty string means "fall back to the template default".</summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>Optional per-extension override; empty string means "fall back to the template runtime".</summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>Optional explicit id-range start. <c>null</c> + <see cref="IdRangeTo"/> null = auto-allocate.</summary>
    public int? IdRangeFrom { get; set; }
    public int? IdRangeTo { get; set; }

    public List<TemplateFolderForm> Folders { get; } = new();
    public List<DependencyForm> Dependencies { get; } = new();

    public static ExtensionForm From(ExtensionAuthoring source)
    {
        var form = new ExtensionForm
        {
            Path = source.Path,
            NameTemplate = source.NameTemplate,
            Required = source.Required,
            Application = source.Application ?? string.Empty,
            Runtime = source.Runtime ?? string.Empty,
            IdRangeFrom = source.IdRangeFrom,
            IdRangeTo = source.IdRangeTo,
        };
        foreach (var f in source.Folders) form.Folders.Add(TemplateFolderForm.From(f));
        foreach (var d in source.Dependencies) form.Dependencies.Add(DependencyForm.From(d));
        return form;
    }

    public ExtensionAuthoring ToAuthoring() => new(
        Path: Path,
        NameTemplate: NameTemplate,
        Required: Required,
        Application: string.IsNullOrWhiteSpace(Application) ? null : Application,
        Runtime: string.IsNullOrWhiteSpace(Runtime) ? null : Runtime,
        IdRangeFrom: IdRangeFrom,
        IdRangeTo: IdRangeTo,
        Folders: Folders.Select(f => f.ToAuthoring()).ToList(),
        Dependencies: Dependencies.Select(d => d.ToAuthoring()).ToList());
}

/// <summary>Mutable mirror of <see cref="FolderAuthoring"/>. Recursive — <see cref="Folders"/> nests.</summary>
public sealed class TemplateFolderForm
{
    public string Path { get; set; } = string.Empty;
    public List<TemplateFolderForm> Folders { get; } = new();
    public List<TemplateFileForm> Files { get; } = new();

    public static TemplateFolderForm From(FolderAuthoring source)
    {
        var form = new TemplateFolderForm { Path = source.Path };
        foreach (var child in source.Folders) form.Folders.Add(From(child));
        foreach (var file in source.Files) form.Files.Add(TemplateFileForm.From(file));
        return form;
    }

    public FolderAuthoring ToAuthoring() => new(
        Path: Path,
        Folders: Folders.Select(f => f.ToAuthoring()).ToList(),
        Files: Files.Select(f => f.ToAuthoring()).ToList());
}

/// <summary>Mutable mirror of <see cref="FileAuthoring"/>.</summary>
public sealed class TemplateFileForm
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsExample { get; set; }

    public static TemplateFileForm From(FileAuthoring source) => new()
    {
        Path = source.Path,
        Content = source.Content,
        IsExample = source.IsExample,
    };

    public FileAuthoring ToAuthoring() => new(Path, Content, IsExample);
}

/// <summary>Which leg of the <see cref="DependencyAuthoring"/> discriminated union the row uses.</summary>
public enum DependencyFormKind { Extension, Module, Literal }

/// <summary>
/// Mutable mirror of <see cref="DependencyAuthoring"/>. The form carries the
/// chosen <see cref="Kind"/> as an explicit enum so the UI can switch input
/// sets without losing values typed into the inactive group while the user
/// experiments.
/// </summary>
public sealed class DependencyForm
{
    public DependencyFormKind Kind { get; set; } = DependencyFormKind.Extension;
    public string RefExtensionPath { get; set; } = string.Empty;
    public string RefModuleKey { get; set; } = string.Empty;
    public string LitId { get; set; } = string.Empty;
    public string LitName { get; set; } = string.Empty;
    public string LitPublisher { get; set; } = string.Empty;
    public string LitVersion { get; set; } = string.Empty;

    public static DependencyForm From(DependencyAuthoring source)
    {
        var form = new DependencyForm
        {
            RefExtensionPath = source.RefExtensionPath ?? string.Empty,
            RefModuleKey = source.RefModuleKey ?? string.Empty,
            LitId = source.LitId ?? string.Empty,
            LitName = source.LitName ?? string.Empty,
            LitPublisher = source.LitPublisher ?? string.Empty,
            LitVersion = source.LitVersion ?? string.Empty,
        };
        form.Kind = source.RefExtensionPath is not null
            ? DependencyFormKind.Extension
            : source.RefModuleKey is not null
                ? DependencyFormKind.Module
                : DependencyFormKind.Literal;
        return form;
    }

    public DependencyAuthoring ToAuthoring() => Kind switch
    {
        DependencyFormKind.Extension => new DependencyAuthoring(
            RefExtensionPath: string.IsNullOrWhiteSpace(RefExtensionPath) ? null : RefExtensionPath,
            RefModuleKey: null, LitId: null, LitName: null, LitPublisher: null, LitVersion: null),
        DependencyFormKind.Module => new DependencyAuthoring(
            RefExtensionPath: null,
            RefModuleKey: string.IsNullOrWhiteSpace(RefModuleKey) ? null : RefModuleKey,
            LitId: null, LitName: null, LitPublisher: null, LitVersion: null),
        _ => new DependencyAuthoring(
            RefExtensionPath: null, RefModuleKey: null,
            LitId: string.IsNullOrWhiteSpace(LitId) ? null : LitId,
            LitName: string.IsNullOrWhiteSpace(LitName) ? null : LitName,
            LitPublisher: string.IsNullOrWhiteSpace(LitPublisher) ? null : LitPublisher,
            LitVersion: string.IsNullOrWhiteSpace(LitVersion) ? null : LitVersion),
    };
}
