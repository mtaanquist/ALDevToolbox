using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Seed;
using Microsoft.EntityFrameworkCore;
using Tomlyn;

namespace ALDevToolbox.Services;

/// <summary>
/// Backup / snapshot service: walks the active rows in the database and emits a
/// ZIP whose layout matches <c>Templates.seed/</c>. The output is the inverse
/// direction of <see cref="SeedService"/> — feeding the unzipped archive into a
/// fresh empty database via the seed pipeline reproduces the same row contents
/// modulo timestamps. See <c>.design/templates-and-seeding.md</c>.
/// </summary>
public class ExportService
{
    /// <summary>
    /// Mirrors <see cref="SeedService"/> and <see cref="TemplateTomlMapper"/>: a
    /// global <c>snake_case</c> policy with per-property TOML name overrides on
    /// the camelCase outliers (defaults / appSourceCop) handled inside the seed
    /// POCOs themselves. Keeping the options identical across read and write
    /// paths is what makes round-tripping reliable.
    /// </summary>
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private readonly AppDbContext _db;
    private readonly ILogger<ExportService> _logger;

    public ExportService(AppDbContext db, ILogger<ExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Produces a ZIP containing the current state of the database serialised
    /// back into the same TOML structure as <c>Templates.seed/</c>. Soft-deleted
    /// rows are excluded so the export reflects the live, useful state. The
    /// returned stream is a rewound <see cref="MemoryStream"/> ready to be
    /// copied to the HTTP body.
    /// </summary>
    public async Task<GeneratedArchive> ExportAllAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var templates = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .OrderBy(t => t.Runtime)
            .ThenBy(t => t.Key)
            .ToListAsync(ct);

        var modules = await _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .OrderBy(m => m.Key)
            .ToListAsync(ct);

        var catalog = await _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var template in templates)
            {
                // Re-use the same mapper the admin TOML editor saves through, so
                // the round-trip is byte-identical to "edit in TOML mode, save".
                var entryPath = $"{template.Key}/template.toml";
                await WriteTextEntryAsync(archive, entryPath, TemplateTomlMapper.ToToml(template), ct);
            }

            foreach (var module in modules)
            {
                var seed = new ModuleSeedFile
                {
                    Module = new ModuleSeed
                    {
                        Key = module.Key,
                        Name = module.Name,
                        IdRangeSize = module.IdRangeSize,
                        Dependencies = module.Dependencies
                            .OrderBy(d => d.Ordering)
                            .Select(d => new ModuleDependencySeed
                            {
                                DepId = d.DepId,
                                DepName = d.DepName,
                                DepPublisher = d.DepPublisher,
                                DepVersion = d.DepVersion,
                            })
                            .ToList(),
                    },
                };
                await WriteTextEntryAsync(
                    archive,
                    $"modules/{module.Key}.toml",
                    TomlSerializer.Serialize(seed, TomlOptions),
                    ct);
            }

            var catalogSeed = new CatalogSeedFile
            {
                Dependency = catalog
                    .Select(c => new WellKnownDependencySeed
                    {
                        DepId = c.DepId,
                        DepName = c.DepName,
                        DepPublisher = c.DepPublisher,
                        DepVersionDefault = c.DepVersionDefault,
                        Category = c.Category,
                    })
                    .ToList(),
            };
            await WriteTextEntryAsync(
                archive,
                "catalog/well-known-deps.toml",
                TomlSerializer.Serialize(catalogSeed, TomlOptions),
                ct);
        }

        stream.Position = 0;
        var fileName = $"Templates.seed-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

        _logger.LogInformation(
            "Exported {Templates} template(s), {Modules} module(s), {Catalog} catalogue entry(ies) to {FileName} in {Elapsed}ms.",
            templates.Count, modules.Count, catalog.Count, fileName, stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, fileName);
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string path, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        // UTF-8 without BOM keeps the export diff-friendly with the seed files
        // that ship in the repo.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }
}
