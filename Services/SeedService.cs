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
    private readonly ILogger<SeedService> _logger;

    public SeedService(AppDbContext db, IWebHostEnvironment env, ILogger<SeedService> logger)
    {
        _db = db;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Imports the seed files into the database if (and only if) no runtime
    /// template rows exist. Safe to call on every startup.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (await _db.RuntimeTemplates.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Seed skipped — runtime_templates is non-empty.");
            return;
        }

        var seedPath = ResolveSeedPath();
        if (seedPath is null || !Directory.Exists(seedPath))
        {
            _logger.LogWarning(
                "Seed path not found (looked at SEED_PATH and parents of the content root). " +
                "Database will start empty.");
            return;
        }

        _logger.LogInformation("Seeding database from {SeedPath}.", seedPath);

        var now = DateTime.UtcNow;
        // Modules are imported before templates so default-module references
        // on a template can resolve to already-tracked Module instances by
        // key. EF's change tracker fixes the foreign keys up at SaveChanges
        // time, so we never have to know the assigned module ids here.
        var modulesByKey = await ImportModulesAsync(seedPath, now, cancellationToken);
        await ImportTemplatesAsync(seedPath, now, modulesByKey, cancellationToken);
        await ImportCatalogAsync(seedPath, now, cancellationToken);

        var written = await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seed complete: {Templates} templates, {Modules} modules, {Catalog} catalogue entries ({Rows} rows).",
            await _db.RuntimeTemplates.CountAsync(cancellationToken),
            await _db.Modules.CountAsync(cancellationToken),
            await _db.WellKnownDependencies.CountAsync(cancellationToken),
            written);
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
        DateTime now,
        IReadOnlyDictionary<string, Module> modulesByKey,
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
                    Ordering = i,
                    Path = f.Path,
                    Files = f.Files
                        .Select((file, fi) => new TemplateFile
                        {
                            Ordering = fi,
                            Path = file.Path,
                            Content = file.Content,
                        })
                        .ToList(),
                }).ToList(),
                ModuleFolders = seed.ModuleFolders.Select((f, i) => new TemplateModuleFolder
                {
                    Ordering = i,
                    Path = f.Path,
                    Files = f.Files
                        .Select((file, fi) => new TemplateModuleFile
                        {
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
                    Module = module,
                    Ordering = ordering++,
                });
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
                Key = file.Module.Key,
                Name = file.Module.Name,
                IdRangeSize = file.Module.IdRangeSize,
                Deprecated = false,
                CreatedAt = now,
                UpdatedAt = now,
                Dependencies = file.Module.Dependencies.Select((d, i) => new ModuleDependency
                {
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

    // ----- catalogue -----

    private async Task ImportCatalogAsync(string seedPath, DateTime now, CancellationToken ct)
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
