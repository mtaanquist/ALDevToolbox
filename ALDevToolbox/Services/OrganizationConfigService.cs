using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tomlyn;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for the per-organisation configuration
/// introduced in Milestone P3.14: settings (publisher / id range / brief /
/// description defaults), the logo, and the always-included file list.
///
/// Generation reads from this service rather than embedded resources or
/// hardcoded values so two organisations can have completely different
/// pre-fills, logos and sidecar files. A small in-memory cache keyed by
/// <c>organization_id</c> keeps the hot path off the database; cache entries
/// are invalidated on save. The cache is an <see cref="IMemoryCache"/>
/// resolved through DI (Singleton in production, per-fixture in tests) so
/// parallel xUnit fixtures hitting their own databases can't race on a
/// shared cache slot (issue #45).
/// </summary>
public class OrganizationConfigService
{
    /// <summary>Maximum logo size accepted from the upload form.</summary>
    public const int MaxLogoBytes = 256 * 1024;

    /// <summary>The two MIME types the upload form accepts.</summary>
    public static readonly IReadOnlySet<string> AllowedLogoContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/svg+xml", "image/png" };

    private static readonly Regex PathRegex = new(@"^[A-Za-z0-9._\-]+(?:/[A-Za-z0-9._\-]+)*$", RegexOptions.Compiled);
    private static readonly Regex DomainRegex = new(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$", RegexOptions.Compiled);
    private static readonly Regex SvgScriptTagRegex = new(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SvgEventAttrRegex = new(@"\s+on[a-zA-Z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ILogger<OrganizationConfigService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IMcpAvailability _mcpAvailability;

    public OrganizationConfigService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        ILogger<OrganizationConfigService> logger,
        IMemoryCache cache,
        IMcpAvailability mcpAvailability)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _logger = logger;
        _cache = cache;
        _mcpAvailability = mcpAvailability;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    private static string CacheKey(int organizationId) => $"org-config:{organizationId}";
    private static string NameCacheKey(int organizationId) => $"org-name:{organizationId}";

    /// <summary>
    /// Returns the current display name of <paramref name="organizationId"/>.
    /// Cached per-org so the layout can render the name in the top bar without
    /// a DB hit on every navigation. The cache is invalidated by
    /// <see cref="RenameOrganizationAsync"/> so rename takes effect immediately
    /// for every active circuit — not only after the renaming admin re-logs in.
    /// Bypasses query filters: layout calls hit this with the claim-derived
    /// org id and don't run under tenant scope.
    /// </summary>
    public async Task<string?> GetOrganizationNameAsync(int organizationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(NameCacheKey(organizationId), out string? cached)) return cached;
        var name = await _db.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct);
        _cache.Set(NameCacheKey(organizationId), name);
        return name;
    }

    /// <summary>
    /// Returns the cached or freshly-loaded <see cref="OrganizationConfig"/>
    /// for the acting user's organisation. Generation, the form pre-fill, and
    /// the admin page all read through this entry point so a save in one
    /// invalidates every reader.
    /// </summary>
    public Task<OrganizationConfig> GetCurrentAsync(CancellationToken ct = default)
        => GetForAsync(RequireOrganizationId(), ct);

    /// <summary>
    /// Loads the configuration for an arbitrary organisation. Used by seed and
    /// import flows that need to populate a specific org regardless of the
    /// current sign-in. Reads bypass query filters because seed-time and
    /// bootstrap callers may not have an organisation in scope yet.
    /// </summary>
    public async Task<OrganizationConfig> GetForAsync(int organizationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(organizationId), out OrganizationConfig? cached) && cached is not null)
            return cached;

        var settings = await _db.OrganizationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);

        var logo = await _db.OrganizationAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Kind == OrganizationAssetKind.Logo, ct);

        var files = await _db.OrganizationFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.OrganizationId == organizationId)
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);

        // The transient fallback initialises CodeWorkspaceJson from the
        // property initialiser (OrganizationDefaults.CodeWorkspaceJson) so a
        // fresh org's Workspace settings page renders the seeded JSON.
        var config = new OrganizationConfig(
            Settings: settings ?? new OrganizationSettings { OrganizationId = organizationId },
            Logo: logo,
            Files: files);

        _cache.Set(CacheKey(organizationId), config);
        return config;
    }

    /// <summary>Drops the cached configuration for one organisation. Called after every write.</summary>
    public void InvalidateCache(int organizationId) => _cache.Remove(CacheKey(organizationId));

    /// <summary>
    /// Persists the publisher / id-range / brief / description defaults block.
    /// Validation matches <see cref="GenerationService"/>'s rules: the id range
    /// must be a positive ascending pair and the publisher must be non-empty.
    /// </summary>
    public async Task SaveSettingsAsync(OrganizationSettingsInput input, CancellationToken ct = default)
    {
        Validate(input);
        var orgId = RequireOrganizationId();

        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }

        row.DefaultPublisher = input.DefaultPublisher.Trim();
        row.DefaultIdRangeFrom = input.DefaultIdRangeFrom;
        row.DefaultIdRangeTo = input.DefaultIdRangeTo;
        row.DefaultBrief = input.DefaultBrief?.Trim() ?? string.Empty;
        row.DefaultCoreDescription = input.DefaultCoreDescription?.Trim() ?? string.Empty;
        row.DefaultUrl = string.IsNullOrWhiteSpace(input.DefaultUrl) ? null : input.DefaultUrl.Trim();
        row.DefaultLogo = string.IsNullOrWhiteSpace(input.DefaultLogo) ? null : input.DefaultLogo.Trim();
        // DefaultSupportedCountries is no longer surfaced in the admin form
        // (AppSourceCop.json moved into Always-included files or per-template
        // overrides). The entity column stays so older rows keep their values.
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated organisation settings for org {OrgId} (publisher={Publisher}, range={From}-{To}).",
            orgId, row.DefaultPublisher, row.DefaultIdRangeFrom, row.DefaultIdRangeTo);
    }

    /// <summary>
    /// Persists the admin-edited <c>.code-workspace</c> JSON template. The
    /// generator overlays the computed <c>folders</c> array onto whatever is
    /// stored here, so the admin owns <c>settings</c> and any other top-level
    /// keys but never has to manage <c>folders</c>. Validation refuses empty
    /// input and anything that doesn't parse as a JSON object.
    /// </summary>
    public async Task SaveCodeWorkspaceJsonAsync(string codeWorkspaceJson, CancellationToken ct = default)
    {
        ValidateCodeWorkspaceJson(codeWorkspaceJson);
        var orgId = RequireOrganizationId();

        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        row.CodeWorkspaceJson = codeWorkspaceJson;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated organisation code-workspace JSON for org {OrgId} ({Bytes} bytes).",
            orgId, codeWorkspaceJson.Length);
    }

    /// <summary>
    /// Renames the current organisation. The slug is intentionally not
    /// editable — it's baked into the <c>org_id</c>/<c>org_name</c> claim set
    /// at sign-in and into any saved URLs. Cached <c>org_name</c> claims on
    /// open sessions stay stale until the next sign-in (same posture as
    /// display-name and role changes).
    /// </summary>
    public async Task RenameOrganizationAsync(string newName, CancellationToken ct = default)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Name"] = "Organisation name must be 2–80 characters.",
            });
        }

        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (string.Equals(org.Name, trimmed, StringComparison.Ordinal)) return;
        org.Name = trimmed;
        await _db.SaveChangesAsync(ct);
        _cache.Remove(NameCacheKey(orgId));
        _logger.LogInformation("Renamed org {OrgId} to {Name}.", orgId, trimmed);
    }

    /// <summary>Lists the email domains claimed by the current organisation.</summary>
    public Task<List<OrganizationEmailDomain>> ListEmailDomainsAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        return _db.OrganizationEmailDomains
            .AsNoTracking()
            .Where(d => d.OrganizationId == orgId)
            .OrderBy(d => d.Domain)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Claims a new email domain for the current organisation. Domains are
    /// globally unique so a successful add blocks every other org from
    /// claiming the same domain — a friendly error surfaces if it's already
    /// taken (whether by this org or another).
    /// </summary>
    public async Task AddEmailDomainAsync(string domain, CancellationToken ct = default)
    {
        var normalised = NormaliseDomain(domain);
        if (!DomainRegex.IsMatch(normalised) || normalised.Length > 253)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = "Enter a valid domain like 'acme.com'.",
            });
        }

        var orgId = RequireOrganizationId();
        var existing = await _db.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Domain == normalised, ct);
        if (existing is not null)
        {
            var msg = existing.OrganizationId == orgId
                ? "That domain is already on the list."
                : "That domain is claimed by another organisation.";
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = msg,
            });
        }

        _db.OrganizationEmailDomains.Add(new OrganizationEmailDomain
        {
            OrganizationId = orgId,
            Domain = normalised,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} claimed email domain {Domain}.", orgId, normalised);
    }

    /// <summary>Removes one of the current organisation's email-domain claims.</summary>
    public async Task RemoveEmailDomainAsync(int domainId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        // Query filter scopes to the current org, so cross-org IDs return null.
        var row = await _db.OrganizationEmailDomains.FirstOrDefaultAsync(d => d.Id == domainId, ct);
        if (row is null) return;
        _db.OrganizationEmailDomains.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} released email domain {Domain}.", orgId, row.Domain);
    }

    /// <summary>
    /// Toggles the per-org MCP opt-out. Refuses when the site-wide
    /// <see cref="IMcpAvailability"/> is off — the UI also disables the
    /// checkbox in that case but the service enforces it independently so a
    /// forged POST can't flip a hidden setting.
    /// </summary>
    public async Task SetMcpEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!_mcpAvailability.IsEnabled)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["McpEnabled"] = "MCP is disabled site-wide. Ask a site admin to enable it first.",
            });
        }
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (org.McpEnabled == enabled) return;
        org.McpEnabled = enabled;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} set MCP enabled = {Enabled}.", orgId, enabled);
    }

    private static string NormaliseDomain(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed.StartsWith('@')) trimmed = trimmed[1..];
        return trimmed;
    }

    /// <summary>
    /// Extracts the domain part of an email and looks up the claiming
    /// organisation, if any. Used by signup to route users to a known org
    /// without requiring them to type a slug. Bypasses query filters because
    /// signup runs pre-login (no org in scope) and the routing must see
    /// claims across every org.
    /// </summary>
    public async Task<Organization?> ResolveOrganizationByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        if (domain.Length == 0) return null;

        var claim = await _db.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => d.Organization)
            .FirstOrDefaultAsync(ct);
        return claim;
    }

    /// <summary>
    /// Replaces the logo for the current organisation with the supplied bytes.
    /// SVG uploads are sanitised (script tags and on* attributes stripped) so
    /// the rendered logo can't smuggle JavaScript into a generated workspace
    /// or the admin preview.
    /// </summary>
    public async Task UploadLogoAsync(string contentType, byte[] content, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedLogoContentTypes.Contains(contentType))
        {
            errors[nameof(contentType)] = "Logo must be an SVG or a PNG.";
        }
        if (content is null || content.Length == 0)
        {
            errors[nameof(content)] = "Pick a logo file to upload.";
        }
        else if (content.Length > MaxLogoBytes)
        {
            errors[nameof(content)] = $"Logo must be {MaxLogoBytes / 1024} KB or smaller.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        await _quotaGuard.EnsureCanWriteAsync(ct);

        var bytes = SanitiseLogo(contentType!, content!);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var row = await _db.OrganizationAssets
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.Kind == OrganizationAssetKind.Logo, ct);
        if (row is null)
        {
            row = new OrganizationAsset
            {
                OrganizationId = orgId,
                Kind = OrganizationAssetKind.Logo,
            };
            _db.OrganizationAssets.Add(row);
        }
        row.ContentType = contentType!;
        row.Content = bytes;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Uploaded logo for org {OrgId}: {Bytes} bytes ({ContentType}).",
            orgId, bytes.Length, contentType);
    }

    /// <summary>
    /// Removes the per-org logo. With the on-disk seed retired, there is no
    /// "default" logo to revert to; <see cref="GenerationService"/> falls back
    /// to its built-in placeholder when no row is present.
    /// </summary>
    public async Task RevertLogoAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationAssets
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.Kind == OrganizationAssetKind.Logo, ct);
        if (row is not null) _db.OrganizationAssets.Remove(row);
        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation("Removed logo for org {OrgId}.", orgId);
    }

    /// <summary>
    /// Replaces the always-included file list. Existing rows are matched by
    /// primary key (preserved through <see cref="OrganizationFileInput.Id"/>);
    /// rows missing from the input are deleted. Same reconciliation pattern as
    /// <see cref="CatalogService"/>.
    /// </summary>
    public async Task SaveFilesAsync(IReadOnlyList<OrganizationFileInput> inputs, CancellationToken ct = default)
    {
        ValidateFiles(inputs);
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();

        var existing = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var inputIds = inputs.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();

        var now = DateTime.UtcNow;
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var path = input.Path.Trim();
            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                row.Path = path;
                row.Content = input.Content ?? string.Empty;
                row.MustacheEnabled = input.MustacheEnabled;
                row.Scope = input.Scope;
                row.Ordering = i;
                row.UpdatedAt = now;
            }
            else
            {
                _db.OrganizationFiles.Add(new OrganizationFile
                {
                    OrganizationId = orgId,
                    Path = path,
                    Content = input.Content ?? string.Empty,
                    Scope = input.Scope,
                    MustacheEnabled = input.MustacheEnabled,
                    Ordering = i,
                    UpdatedAt = now,
                });
            }
        }

        foreach (var row in existing)
        {
            if (!inputIds.Contains(row.Id)) _db.OrganizationFiles.Remove(row);
        }

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Saved {Count} always-included file(s) for org {OrgId}.", inputs.Count, orgId);
    }

    /// <summary>
    /// Wipe-and-replace import for the per-org configuration block written by
    /// <see cref="ExportService"/>. Replaces the org's settings row, all
    /// always-included files, and the logo with whatever the TOML carries —
    /// callers must confirm the overwrite at the UI layer (same modal pattern
    /// as a destructive delete). Validation reuses the same rules as the
    /// per-section saves above.
    /// </summary>
    public async Task ImportFromTomlAsync(string toml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toml))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Toml"] = "Paste the contents of organization-config.toml.",
            });
        }

        await _quotaGuard.EnsureCanWriteAsync(ct);

        OrganizationConfigSeedFile seed;
        try
        {
            seed = TomlSerializer.Deserialize<OrganizationConfigSeedFile>(toml, TomlImportOptions)
                ?? throw new InvalidDataException("Empty TOML.");
        }
        catch (Exception ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Toml"] = $"Failed to parse TOML: {ex.Message}",
            });
        }

        var settingsInput = new OrganizationSettingsInput(
            DefaultPublisher: seed.Settings.DefaultPublisher,
            DefaultIdRangeFrom: seed.Settings.DefaultIdRangeFrom,
            DefaultIdRangeTo: seed.Settings.DefaultIdRangeTo,
            DefaultBrief: seed.Settings.DefaultBrief,
            DefaultCoreDescription: seed.Settings.DefaultCoreDescription);
        Validate(settingsInput);

        // Pre-Issue-#61 exports omit the field — fall back to the in-app default
        // so old archives still import. New imports go through the same
        // server-side JSON-object validation the Workspace settings form uses.
        var codeWorkspaceJson = string.IsNullOrWhiteSpace(seed.Settings.CodeWorkspaceJson)
            ? OrganizationDefaults.CodeWorkspaceJson
            : seed.Settings.CodeWorkspaceJson;
        ValidateCodeWorkspaceJson(codeWorkspaceJson);

        var fileInputs = seed.File
            .Select(f => new OrganizationFileInput(
                Id: null,
                Path: f.Path,
                Content: f.Content,
                MustacheEnabled: f.MustacheEnabled,
                Scope: Enum.TryParse<ALDevToolbox.Domain.ValueObjects.OrganizationFileScope>(f.Scope, out var s)
                    ? s
                    : ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot))
            .ToList();
        ValidateFiles(fileInputs);

        byte[]? logoBytes = null;
        string? logoContentType = null;
        if (seed.Logo is not null)
        {
            if (!AllowedLogoContentTypes.Contains(seed.Logo.ContentType))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentType"] = "Logo must be an SVG or a PNG.",
                });
            }
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(seed.Logo.ContentBase64);
            }
            catch (FormatException)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentBase64"] = "Logo bytes are not valid base64.",
                });
            }
            if (decoded.Length > MaxLogoBytes)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentBase64"] = $"Logo must be {MaxLogoBytes / 1024} KB or smaller.",
                });
            }
            logoBytes = SanitiseLogo(seed.Logo.ContentType, decoded);
            logoContentType = seed.Logo.ContentType;
        }

        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        // Settings: upsert the single row.
        var settings = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(settings);
        }
        settings.DefaultPublisher = settingsInput.DefaultPublisher.Trim();
        settings.DefaultIdRangeFrom = settingsInput.DefaultIdRangeFrom;
        settings.DefaultIdRangeTo = settingsInput.DefaultIdRangeTo;
        settings.DefaultBrief = settingsInput.DefaultBrief?.Trim() ?? string.Empty;
        settings.DefaultCoreDescription = settingsInput.DefaultCoreDescription?.Trim() ?? string.Empty;
        settings.DefaultUrl = string.IsNullOrWhiteSpace(settingsInput.DefaultUrl) ? null : settingsInput.DefaultUrl.Trim();
        settings.DefaultLogo = string.IsNullOrWhiteSpace(settingsInput.DefaultLogo) ? null : settingsInput.DefaultLogo.Trim();
        // DefaultSupportedCountries: column preserved, no longer written from
        // either the admin form or the TOML import — see SaveSettingsAsync.
        settings.CodeWorkspaceJson = codeWorkspaceJson;
        settings.UpdatedAt = now;

        // Files: drop everything and re-insert. Wipe-and-replace import means
        // callers have already confirmed the destructive operation.
        var existingFiles = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        _db.OrganizationFiles.RemoveRange(existingFiles);
        for (var i = 0; i < fileInputs.Count; i++)
        {
            var input = fileInputs[i];
            _db.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = orgId,
                Path = input.Path.Trim(),
                Content = input.Content ?? string.Empty,
                MustacheEnabled = input.MustacheEnabled,
                Scope = input.Scope,
                Ordering = i,
                UpdatedAt = now,
            });
        }

        // Logo: upsert if the TOML carries one; otherwise leave the existing
        // row alone (an admin who imports a logo-less TOML probably didn't
        // intend to wipe their logo, only the settings/files).
        if (logoBytes is not null)
        {
            var logo = await _db.OrganizationAssets
                .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                                          && a.Kind == OrganizationAssetKind.Logo, ct);
            if (logo is null)
            {
                logo = new OrganizationAsset
                {
                    OrganizationId = orgId,
                    Kind = OrganizationAssetKind.Logo,
                };
                _db.OrganizationAssets.Add(logo);
            }
            logo.ContentType = logoContentType!;
            logo.Content = logoBytes;
            logo.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation(
            "Imported organisation configuration into org {OrgId}: {Files} file(s){LogoSuffix}.",
            orgId, fileInputs.Count, logoBytes is null ? string.Empty : " + logo");
    }

    /// <summary>TOML deserialiser options used by the per-org config import.</summary>
    private static readonly TomlSerializerOptions TomlImportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Sanitises an uploaded SVG by stripping <c>&lt;script&gt;</c> elements
    /// and <c>on*</c> event-handler attributes. PNGs pass through unchanged.
    /// Public so the sanitiser can be exercised directly from tests.
    /// </summary>
    public static byte[] SanitiseLogo(string contentType, byte[] content)
    {
        if (!string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }
        var text = Encoding.UTF8.GetString(content);
        text = SvgScriptTagRegex.Replace(text, string.Empty);
        text = SvgEventAttrRegex.Replace(text, string.Empty);
        return Encoding.UTF8.GetBytes(text);
    }

    private static void Validate(OrganizationSettingsInput input)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(input.DefaultPublisher))
            errors[nameof(input.DefaultPublisher)] = "Publisher is required.";
        if (input.DefaultIdRangeFrom <= 0)
            errors[nameof(input.DefaultIdRangeFrom)] = "Must be greater than zero.";
        if (input.DefaultIdRangeTo <= input.DefaultIdRangeFrom)
            errors[nameof(input.DefaultIdRangeTo)] = "Must be greater than 'from'.";
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// Server-side validation for the workspace-settings JSON: must be
    /// non-empty and parse to a JSON object (not array / string / number).
    /// Errors are keyed on <c>codeWorkspaceJson</c> so the Workspace settings
    /// form can render the message inline next to the editor.
    /// </summary>
    internal static void ValidateCodeWorkspaceJson(string? input)
    {
        const string field = "codeWorkspaceJson";
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = "JSON is required. The file is always written, so the template can't be blank.",
            });
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(input);
        }
        catch (JsonException ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = $"Could not parse as JSON: {ex.Message}",
            });
        }

        if (parsed is not JsonObject)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = "JSON root must be an object — a workspace file has top-level keys like 'settings'.",
            });
        }
    }

    private static void ValidateFiles(IReadOnlyList<OrganizationFileInput> inputs)
    {
        var errors = new Dictionary<string, string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.Count; i++)
        {
            var path = inputs[i].Path?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                errors[$"Files[{i}].Path"] = "Path is required.";
            }
            else if (!PathRegex.IsMatch(path) || path.Contains(".."))
            {
                errors[$"Files[{i}].Path"] = "Use a relative path with letters, digits, '_', '-', '.' and '/'. No '..' segments.";
            }
            else if (!seenPaths.Add(path))
            {
                errors[$"Files[{i}].Path"] = $"Duplicate path '{path}'.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }
}

/// <summary>
/// Snapshot returned by <see cref="OrganizationConfigService.GetCurrentAsync"/>.
/// The <see cref="Settings"/> row is always non-null so callers don't need a
/// secondary check; missing rows surface as a transient default that's still
/// safe to read.
/// </summary>
public record OrganizationConfig(
    OrganizationSettings Settings,
    OrganizationAsset? Logo,
    IReadOnlyList<OrganizationFile> Files);

/// <summary>Form-post shape for the Defaults section of <c>/admin/configuration</c>.</summary>
public record OrganizationSettingsInput(
    string DefaultPublisher,
    int DefaultIdRangeFrom,
    int DefaultIdRangeTo,
    string DefaultBrief,
    string DefaultCoreDescription,
    string? DefaultUrl = null,
    string? DefaultLogo = null);

/// <summary>One row submitted by the always-included files editor.</summary>
public record OrganizationFileInput(
    int? Id,
    string Path,
    string Content,
    bool MustacheEnabled,
    ALDevToolbox.Domain.ValueObjects.OrganizationFileScope Scope = ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot);
