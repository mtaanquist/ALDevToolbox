using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Tomlyn;

namespace ALDevToolbox.Services;

/// <summary>
/// First-run importer for templates, modules and the well-known dependency
/// catalogue. Reads the TOML files under <c>SEED_PATH</c> (or the discovered
/// equivalent) and populates the database in one transaction. The whole thing
/// is a no-op on subsequent boots — the seed is gated on the
/// <c>runtime_templates</c> table being empty so admin edits are never
/// clobbered by a redeploy.
/// </summary>
public class SeedService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<SeedService> _logger;

    public SeedService(
        AppDbContext db,
        IWebHostEnvironment env,
        OrganizationConfigService config,
        ILogger<SeedService> logger)
    {
        _db = db;
        _env = env;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Imports the seed files into <paramref name="organizationId"/> if (and
    /// only if) the organisation has no runtime template rows yet. Safe to
    /// call on every startup. Pass the organisation id to seed; pre-M13 this
    /// was a single global seed step, post-M13 every organisation can be
    /// seeded independently (the migration marks the Default org as already
    /// seeded).
    /// </summary>
    public async Task RunAsync(int organizationId, CancellationToken cancellationToken = default)
    {
        var alreadyHas = await _db.RuntimeTemplates
            .IgnoreQueryFilters()
            .AnyAsync(t => t.OrganizationId == organizationId, cancellationToken);
        if (alreadyHas)
        {
            _logger.LogInformation("Seed skipped — runtime_templates is non-empty for org {OrgId}.", organizationId);
            return;
        }

        var seedPath = ResolveSeedPath();
        if (seedPath is null || !Directory.Exists(seedPath))
        {
            _logger.LogWarning(
                "Seed path not found (looked at SEED_PATH and parents of the content root). " +
                "Org {OrgId} will start empty.", organizationId);
            return;
        }

        _logger.LogInformation("Seeding org {OrgId} from {SeedPath}.", organizationId, seedPath);

        var now = DateTime.UtcNow;
        var modulesByKey = await ImportModulesAsync(seedPath, organizationId, now, cancellationToken);
        var applicationVersionsByKey = await ImportApplicationVersionsAsync(seedPath, organizationId, now, cancellationToken);
        await ImportTemplatesAsync(seedPath, organizationId, now, modulesByKey, applicationVersionsByKey, cancellationToken);
        await ImportCatalogAsync(seedPath, organizationId, now, cancellationToken);

        var written = await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seed complete for org {OrgId}: {Rows} rows.",
            organizationId, written);

        // Populate the per-org configuration row introduced in Milestone P3.14.
        // Idempotent — does nothing for an org that already has a settings row,
        // so re-running seed against a partially-configured org is safe.
        await _config.PopulateDefaultsAsync(organizationId, cancellationToken);
    }

    /// <summary>
    /// Resolves the absolute seed path. Honours <c>SEED_PATH</c> if set; otherwise
    /// walks upward from the content root looking for a <c>Templates.seed/</c>
    /// directory. Returns <c>null</c> if neither route finds anything — the
    /// caller logs and continues with an empty database.
    /// </summary>
    private string? ResolveSeedPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SEED_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        var dir = new DirectoryInfo(_env.ContentRootPath);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Templates.seed");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // ----- templates -----

    private async Task ImportTemplatesAsync(
        string seedPath,
        int organizationId,
        DateTime now,
        IReadOnlyDictionary<string, Module> modulesByKey,
        IReadOnlyDictionary<string, ApplicationVersion> applicationVersionsByKey,
        CancellationToken ct)
    {
        foreach (var dir in Directory.EnumerateDirectories(seedPath, "runtime-*"))
        {
            var tomlPath = Path.Combine(dir, "template.toml");
            if (!File.Exists(tomlPath))
            {
                _logger.LogWarning("Skipping {Dir} — no template.toml.", dir);
                continue;
            }

            // Normalise runtime values so old seed files using `runtime = 15`
            // (bare integer) keep parsing into the new string-typed
            // TemplateMetaSeed.Runtime alongside the new `runtime = "15.2"`
            // form.
            var rawToml = await File.ReadAllTextAsync(tomlPath, ct);
            var seed = ParseToml<TemplateSeed>(TemplateTomlMapper.NormalizeRuntimeValue(rawToml), tomlPath);

            var template = new RuntimeTemplate
            {
                OrganizationId = organizationId,
                Key = seed.Template.Key,
                Runtime = seed.Template.Runtime,
                Name = seed.Template.Name,
                Description = seed.Template.Description,
                DefaultApplication = seed.Template.DefaultApplication,
                DefaultPlatform = seed.Template.DefaultPlatform,
                CoreIdRangeFrom = seed.Template.CoreIdRangeFrom,
                CoreIdRangeTo = seed.Template.CoreIdRangeTo,
                ModuleIdRangeStart = seed.Template.ModuleIdRangeStart,
                ModuleIdRangeSize = seed.Template.ModuleIdRangeSize,
                Defaults = MapDefaults(seed.Defaults),
                AppSourceCop = MapAppSourceCop(seed.AppSourceCop),
                Deprecated = false,
                CreatedAt = now,
                UpdatedAt = now,
                Folders = seed.Folders.Select((f, i) => new TemplateFolder
                {
                    OrganizationId = organizationId,
                    Ordering = i,
                    Path = f.Path,
                    Files = f.Files
                        .Select((file, fi) => new TemplateFile
                        {
                            OrganizationId = organizationId,
                            Ordering = fi,
                            Path = file.Path,
                            Content = file.Content,
                        })
                        .ToList(),
                }).ToList(),
                ModuleFolders = seed.ModuleFolders.Select((f, i) => new TemplateModuleFolder
                {
                    OrganizationId = organizationId,
                    Ordering = i,
                    Path = f.Path,
                    Files = f.Files
                        .Select((file, fi) => new TemplateModuleFile
                        {
                            OrganizationId = organizationId,
                            Ordering = fi,
                            Path = file.Path,
                            Content = file.Content,
                        })
                        .ToList(),
                }).ToList(),
            };

            // Pre-selected modules: resolve each key against the already-tracked
            // Module instances. Drop unknowns with a warning rather than failing
            // the entire seed run; admins can fix typos via the UI later.
            var ordering = 0;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in seed.Template.DefaultModules)
            {
                if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
                {
                    continue;
                }
                if (!modulesByKey.TryGetValue(key, out var module))
                {
                    _logger.LogWarning(
                        "Template '{TemplateKey}' references unknown default module '{ModuleKey}'; skipped.",
                        template.Key, key);
                    continue;
                }
                template.DefaultModules.Add(new RuntimeTemplateDefaultModule
                {
                    OrganizationId = organizationId,
                    Module = module,
                    Ordering = ordering++,
                });
            }

            // Optional default_application_version (Milestone P2.4). Same lenient
            // resolution rule as default_modules: an unknown key logs a warning
            // and the template falls back to its free-text default_application
            // / runtime values rather than failing the seed.
            var defaultAppVersionKey = seed.Template.DefaultApplicationVersion?.Trim();
            if (!string.IsNullOrEmpty(defaultAppVersionKey))
            {
                if (applicationVersionsByKey.TryGetValue(defaultAppVersionKey, out var version))
                {
                    template.DefaultApplicationVersion = version;
                }
                else
                {
                    _logger.LogWarning(
                        "Template '{TemplateKey}' references unknown default application version '{Key}'; using raw default_application/runtime values.",
                        template.Key, defaultAppVersionKey);
                }
            }

            _db.RuntimeTemplates.Add(template);
        }
    }

    private static TemplateDefaults MapDefaults(DefaultsSeed s) => new()
    {
        Publisher = s.Publisher,
        Target = s.Target,
        Url = s.Url,
        Logo = s.Logo,
        Features = s.Features,
        SupportedLocales = s.SupportedLocales,
        ResourceExposurePolicy = new ResourceExposurePolicy
        {
            AllowDebugging = s.ResourceExposurePolicy.AllowDebugging,
            AllowDownloadingSource = s.ResourceExposurePolicy.AllowDownloadingSource,
            IncludeSourceInSymbolFile = s.ResourceExposurePolicy.IncludeSourceInSymbolFile,
        },
    };

    private static AppSourceCopSettings MapAppSourceCop(AppSourceCopSeed s) => new()
    {
        MandatoryPrefix = s.MandatoryPrefix,
        SupportedCountries = s.SupportedCountries,
    };

    // ----- modules -----

    /// <summary>
    /// Imports the module seed files and returns a key-indexed view so the
    /// template importer can hydrate <c>default_modules</c> references against
    /// the same tracked <see cref="Module"/> instances.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Module>> ImportModulesAsync(
        string seedPath,
        int organizationId,
        DateTime now,
        CancellationToken ct)
    {
        var byKey = new Dictionary<string, Module>(StringComparer.Ordinal);
        var modulesDir = Path.Combine(seedPath, "modules");
        if (!Directory.Exists(modulesDir))
            return byKey;

        foreach (var path in Directory.EnumerateFiles(modulesDir, "*.toml"))
        {
            var file = ParseToml<ModuleSeedFile>(await File.ReadAllTextAsync(path, ct), path);
            var module = new Module
            {
                OrganizationId = organizationId,
                Key = file.Module.Key,
                Name = file.Module.Name,
                IdRangeSize = file.Module.IdRangeSize,
                Deprecated = false,
                CreatedAt = now,
                UpdatedAt = now,
                Dependencies = file.Module.Dependencies.Select((d, i) => new ModuleDependency
                {
                    OrganizationId = organizationId,
                    Ordering = i,
                    DepId = d.DepId,
                    DepName = d.DepName,
                    DepPublisher = d.DepPublisher,
                    DepVersion = d.DepVersion,
                }).ToList(),
            };
            _db.Modules.Add(module);
            byKey[module.Key] = module;
        }
        return byKey;
    }

    // ----- application versions -----

    /// <summary>
    /// Imports the application-version seed files (Milestone P2.4) and returns
    /// a key-indexed view so the template importer can resolve
    /// <c>default_application_version</c> references against the tracked
    /// instances. Files live one-per-key under
    /// <c>Templates.seed/application-versions/</c>; missing directory is fine
    /// and yields an empty catalogue.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ApplicationVersion>> ImportApplicationVersionsAsync(
        string seedPath,
        int organizationId,
        DateTime now,
        CancellationToken ct)
    {
        var byKey = new Dictionary<string, ApplicationVersion>(StringComparer.Ordinal);
        var dir = Path.Combine(seedPath, "application-versions");
        if (!Directory.Exists(dir))
            return byKey;

        var ordering = 0;
        // Sort by file path so seed runs are deterministic across filesystems
        // and the resulting Ordering matches the alphabetical seed file order
        // when no explicit `ordering` is set.
        foreach (var path in Directory.EnumerateFiles(dir, "*.toml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var file = ParseToml<ApplicationVersionSeedFile>(await File.ReadAllTextAsync(path, ct), path);
            if (string.IsNullOrWhiteSpace(file.Version.Key))
            {
                _logger.LogWarning("Application-version seed at {Path} has no key; skipped.", path);
                continue;
            }

            var entry = new ApplicationVersion
            {
                OrganizationId = organizationId,
                Key = file.Version.Key,
                Name = file.Version.Name,
                Application = file.Version.Application,
                Runtime = file.Version.Runtime,
                Ordering = file.Version.Ordering > 0 ? file.Version.Ordering : ordering,
                Deprecated = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.ApplicationVersions.Add(entry);
            byKey[entry.Key] = entry;
            ordering++;
        }
        return byKey;
    }

    // ----- catalogue -----

    private async Task ImportCatalogAsync(string seedPath, int organizationId, DateTime now, CancellationToken ct)
    {
        var catalogPath = Path.Combine(seedPath, "catalog", "well-known-deps.toml");
        if (!File.Exists(catalogPath))
            return;

        var file = ParseToml<CatalogSeedFile>(await File.ReadAllTextAsync(catalogPath, ct), catalogPath);
        for (var i = 0; i < file.Dependency.Count; i++)
        {
            var d = file.Dependency[i];
            _db.WellKnownDependencies.Add(new WellKnownDependency
            {
                OrganizationId = organizationId,
                DepId = d.DepId,
                DepName = d.DepName,
                DepPublisher = d.DepPublisher,
                DepVersionDefault = d.DepVersionDefault,
                Category = d.Category,
                Ordering = i,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    // ----- TOML helpers -----

    /// <summary>
    /// Cached options that map TOML <c>snake_case</c> keys onto C# PascalCase
    /// property names. Outliers in the seed files (camelCase keys mirroring AL
    /// JSON conventions) are handled per-property via
    /// <see cref="Tomlyn.Serialization.TomlPropertyNameAttribute"/>.
    /// </summary>
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Parses <paramref name="text"/> as TOML into a strongly-typed model. The
    /// file path is included in error messages so a malformed seed file points
    /// at itself rather than a generic "TOML parse error".
    /// </summary>
    private static T ParseToml<T>(string text, string path) where T : class, new()
    {
        try
        {
            return TomlSerializer.Deserialize<T>(text, TomlOptions) ?? new T();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {path}: {ex.Message}", ex);
        }
    }
}
