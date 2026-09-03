using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Translation.Providers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Data Protection purpose string for the per-org machine-translation API key.</summary>
    public const string MachineTranslationApiKeyProtectionPurpose = "ALDevToolbox.OrganizationSettings.MachineTranslationApiKey";

    private static readonly Regex PathRegex = new(@"^[A-Za-z0-9._\-]+(?:/[A-Za-z0-9._\-]+)*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ILogger<OrganizationConfigService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _mtApiKeyProtector;

    public OrganizationConfigService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        ILogger<OrganizationConfigService> logger,
        IMemoryCache cache,
        IDataProtectionProvider protectionProvider,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _logger = logger;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _mtApiKeyProtector = protectionProvider.CreateProtector(MachineTranslationApiKeyProtectionPurpose);
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns the tracked <see cref="OrganizationSettings"/> row for
    /// <paramref name="orgId"/>, inserting a fresh one (added to the change
    /// tracker) when none exists yet. Runs under the normal tenant query filter
    /// — every caller already holds the acting org id from
    /// <see cref="RequireOrganizationId"/>, so there is no cross-org read here.
    /// </summary>
    private async Task<OrganizationSettings> GetOrCreateSettingsAsync(int orgId, CancellationToken ct)
    {
        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        return row;
    }

    /// <summary>
    /// Copies the publisher / id-range / brief / description / url / logo
    /// defaults from <paramref name="input"/> onto <paramref name="row"/> and
    /// stamps <see cref="OrganizationSettings.UpdatedAt"/>. Shared by the
    /// per-section save and the TOML import so the two write the settings block
    /// identically. <see cref="OrganizationSettings.CodeWorkspaceJson"/> is left
    /// to the caller — only the import overwrites it.
    /// </summary>
    private static void ApplySettingsFields(OrganizationSettings row, OrganizationSettingsInput input, DateTime now)
    {
        row.DefaultPublisher = input.DefaultPublisher.Trim();
        row.DefaultIdRangeFrom = input.DefaultIdRangeFrom;
        row.DefaultIdRangeTo = input.DefaultIdRangeTo;
        row.DefaultBrief = input.DefaultBrief?.Trim() ?? string.Empty;
        row.DefaultCoreDescription = input.DefaultCoreDescription?.Trim() ?? string.Empty;
        row.DefaultUrl = string.IsNullOrWhiteSpace(input.DefaultUrl) ? null : input.DefaultUrl.Trim();
        row.DefaultLogo = string.IsNullOrWhiteSpace(input.DefaultLogo) ? null : input.DefaultLogo.Trim();
        // DefaultSupportedCountries is no longer surfaced in the admin form or
        // the TOML import (AppSourceCop.json moved into Always-included files or
        // per-template overrides). The entity column stays so older rows keep
        // their values.
        row.UpdatedAt = now;
    }

    /// <summary>
    /// Builds a new <see cref="OrganizationFile"/> row from an input at the
    /// given <paramref name="ordering"/>. Shared by the reconciling save and the
    /// wipe-and-replace import so both insert files identically.
    /// </summary>
    private static OrganizationFile NewOrganizationFile(int orgId, OrganizationFileInput input, int ordering, DateTime now) =>
        new()
        {
            OrganizationId = orgId,
            Path = input.Path.Trim(),
            Content = input.Content ?? string.Empty,
            MustacheEnabled = input.MustacheEnabled,
            Scope = input.Scope,
            Ordering = ordering,
            UpdatedAt = now,
        };

    private static string CacheKey(int organizationId) => $"org-config:{organizationId}";
    private static string NameCacheKey(int organizationId) => $"org-name:{organizationId}";

    /// <summary>
    /// Returns the current display name of <paramref name="organizationId"/>.
    /// Cached per-org so the layout can render the name in the top bar without
    /// a DB hit on every navigation. The entry is refreshed in place by
    /// <see cref="RenameOrganizationAsync"/> so rename takes effect immediately
    /// for every active circuit — not only after the renaming admin re-logs in.
    /// Bypasses query filters: layout calls hit this with the claim-derived
    /// org id and don't run under tenant scope.
    /// </summary>
    public async Task<string?> GetOrganizationNameAsync(int organizationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(NameCacheKey(organizationId), out string? cached)) return cached;

        // Deliberately NOT _db. This is the one query in this service that runs
        // from MainLayout, which renders above every page -- and Blazor does not
        // wait for a layout's OnInitializedAsync before starting its children's.
        // Sharing the request's scoped AppDbContext therefore means the layout
        // and the page can have two commands in flight on one context, which EF
        // refuses ("A second operation was started on this context instance").
        //
        // It only ever bit after a rename, because every other render served
        // this from cache and issued no command at all -- see issue #551, where
        // warming the cache elsewhere first made the crash disappear 3/3 while
        // going straight to the page crashed 3/3.
        //
        // A child scope gives this one read its own context. No new
        // IgnoreQueryFilters() call site: the existing one moves with the query,
        // and this read is by explicit org id rather than under tenant scope.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = await db.Organizations
            // Fence category 4 (explicitly scoped org-id lookup): pinned to o.Id == organizationId.
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
        // Tenant-isolation guard: the reads below bypass the EF query filter, so
        // a caller running inside an authenticated request must only ever ask for
        // its own org's config. Pre-auth / seed / bootstrap callers (no org in
        // scope) may target any org. Blocks a latent IDOR should a future caller
        // pass a user-influenced org id. See #489.
        if (_orgContext.CurrentOrganizationId is int scoped && scoped != organizationId)
        {
            throw new InvalidOperationException(
                $"Refusing cross-organisation config read: request scoped to org {scoped} asked for org {organizationId}.");
        }

        if (_cache.TryGetValue(CacheKey(organizationId), out OrganizationConfig? cached) && cached is not null)
            return cached;

        var settings = await _db.OrganizationSettings
            // Fence category 4 (explicitly scoped org-id lookup): the three reads below are all
            // pinned to OrganizationId == organizationId, and the guard above refuses a request
            // scoped to a different org.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);

        var logo = await _db.OrganizationAssets
            // Same category 4 read, pinned to a.OrganizationId == organizationId.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Kind == OrganizationAssetKind.Logo, ct);

        var files = await _db.OrganizationFiles
            // Same category 4 read, pinned to f.OrganizationId == organizationId.
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

        var row = await GetOrCreateSettingsAsync(orgId, ct);

        ApplySettingsFields(row, input, DateTime.UtcNow);

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

        var row = await GetOrCreateSettingsAsync(orgId, ct);
        var now = DateTime.UtcNow;
        row.CodeWorkspaceJson = codeWorkspaceJson;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated organisation code-workspace JSON for org {OrgId} ({Bytes} bytes).",
            orgId, codeWorkspaceJson.Length);
    }

    /// <summary>Cap on the Cookbook authoring guidance markdown body.</summary>
    public const int MaxCookbookGuidanceLength = 10_000;

    /// <summary>
    /// Persists the Cookbook authoring guidance returned by the
    /// <c>get_cookbook_guidance</c> MCP tool. Markdown; empty allowed
    /// (the tool still returns the built-in type descriptions).
    /// </summary>
    public async Task SaveCookbookGuidanceAsync(string? guidance, CancellationToken ct = default)
    {
        var body = guidance?.Trim() ?? string.Empty;
        if (body.Length > MaxCookbookGuidanceLength)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["CookbookGuidance"] = $"Guidance must be {MaxCookbookGuidanceLength} characters or fewer.",
            });
        }

        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.CookbookGuidance = body;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation(
            "Saved cookbook authoring guidance for org {OrgId} ({Bytes} chars).",
            orgId, body.Length);
    }

    /// <summary>
    /// Persists the per-org automatic-release-import settings (enable toggle +
    /// countries). At least one country is required to enable it; the value may
    /// be a comma-separated list (e.g. <c>w1,dk,nl</c>) — codes are trimmed,
    /// lower-cased, and de-duplicated, and the canonical joined form is stored.
    /// Throws <see cref="PlanValidationException"/> (field key
    /// <c>AutoImportCountry</c>) so the form renders the error inline.
    /// </summary>
    public async Task SaveAutoImportAsync(bool enabled, string? country, CancellationToken ct = default)
    {
        var codes = ParseAutoImportCountries(country);
        if (enabled && codes.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AutoImportCountry"] = "Pick at least one country code (e.g. 'dk' or 'w1,dk,nl') to enable automatic import.",
            });
        }
        if (codes.Any(c => !AutoImportCountryRegex.IsMatch(c)))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AutoImportCountry"] = "Use BC country codes like 'dk' or 'w1', separated by commas.",
            });
        }

        var normalized = codes.Count == 0 ? null : string.Join(",", codes);
        var orgId = RequireOrganizationId();
        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.AutoImportReleasesEnabled = enabled;
        row.AutoImportCountry = normalized;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation(
            "Updated release auto-import settings for org {OrgId} (enabled={Enabled}, countries={Countries}).",
            orgId, enabled, normalized ?? "(none)");
    }

    /// <summary>
    /// BC OnPrem artifact country codes are two-letter markets plus the
    /// <c>w1</c>/<c>w2</c>-style worldwide bases — two lower-case alphanumerics
    /// covers the whole CDN index.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AutoImportCountryRegex =
        new("^[a-z0-9]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Splits a stored or submitted auto-import country value into its canonical
    /// code list: comma-separated, trimmed, lower-cased, de-duplicated in first-seen
    /// order. Shared by the writer above and <c>ReleaseAutoImportScheduler</c> so
    /// the two never disagree on the format.
    /// </summary>
    public static List<string> ParseAutoImportCountries(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .Distinct()
            .ToList();

    // ── Machine translation (per-tenant DeepL / future providers) ───────────

    /// <summary>
    /// Audit-friendly view of the org's machine-translation settings for the
    /// admin form. Carries <see cref="MtSettingsView.HasApiKey"/> rather than the
    /// key itself — plaintext only ever materialises inside
    /// <see cref="ResolveMachineTranslationAsync"/>.
    /// </summary>
    public async Task<MtSettingsView> GetMachineTranslationViewAsync(CancellationToken ct = default)
    {
        var config = await GetCurrentAsync(ct);
        var s = config.Settings;
        return new MtSettingsView(
            Provider: NormaliseMtProvider(s.MachineTranslationProvider),
            HasApiKey: !string.IsNullOrEmpty(s.MachineTranslationApiKeyEncrypted),
            Trigger: s.MachineTranslationTrigger);
    }

    /// <summary>
    /// Persists the machine-translation settings. The API key is encrypted via the
    /// Data Protection ring; an empty key leaves the stored value untouched, and
    /// <see cref="MtSettingsInput.ClearApiKey"/> wipes it. A key is required to use
    /// any mode other than <see cref="MtTrigger.Off"/>. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors for the form.
    /// </summary>
    public async Task SaveMachineTranslationAsync(MtSettingsInput input, CancellationToken ct = default)
    {
        var provider = NormaliseMtProvider(input.Provider);
        var orgId = RequireOrganizationId();

        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);

        var hasStoredKey = !string.IsNullOrEmpty(row?.MachineTranslationApiKeyEncrypted);
        var willHaveKey = !input.ClearApiKey && (!string.IsNullOrWhiteSpace(input.ApiKey) || hasStoredKey);
        if (input.Trigger != MtTrigger.Off && !willHaveKey)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["MachineTranslationApiKey"] = "An API key is required to enable machine translation.",
            });
        }

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }

        row.MachineTranslationProvider = provider;
        row.MachineTranslationTrigger = input.Trigger;
        if (input.ClearApiKey)
        {
            row.MachineTranslationApiKeyEncrypted = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.ApiKey))
        {
            row.MachineTranslationApiKeyEncrypted = _mtApiKeyProtector.Protect(input.ApiKey.Trim());
        }
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated machine-translation settings for org {OrgId} (provider={Provider}, trigger={Trigger}, hasKey={HasKey}).",
            orgId, provider, input.Trigger, !string.IsNullOrEmpty(row.MachineTranslationApiKeyEncrypted));
    }

    /// <summary>
    /// Decrypts the stored API key and returns the resolved machine-translation
    /// settings, or <see langword="null"/> when the feature is off
    /// (<see cref="MtTrigger.Off"/>), no key is stored, or the key can't be
    /// decrypted (a lost key ring). Reads the cached <see cref="OrganizationConfig"/>,
    /// so it doesn't add a DB round-trip on the hot path.
    /// </summary>
    public async Task<ResolvedMtSettings?> ResolveMachineTranslationAsync(CancellationToken ct = default)
    {
        var config = await GetCurrentAsync(ct);
        var s = config.Settings;
        if (s.MachineTranslationTrigger == MtTrigger.Off) return null;
        if (string.IsNullOrEmpty(s.MachineTranslationApiKeyEncrypted)) return null;

        string apiKey;
        try
        {
            apiKey = _mtApiKeyProtector.Unprotect(s.MachineTranslationApiKeyEncrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt machine-translation API key; feature disabled until re-entered.");
            return null;
        }

        return new ResolvedMtSettings(NormaliseMtProvider(s.MachineTranslationProvider), apiKey, s.MachineTranslationTrigger);
    }

    /// <summary>Coerces a provider discriminator to a known value, defaulting to DeepL.</summary>
    private static string NormaliseMtProvider(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? MachineTranslationProviderFactory.DeepLProviderKey
            : value.Trim().ToLowerInvariant();

    /// <summary>
    /// The set of providers an org with no explicit configuration allows. An
    /// unconfigured org permits both, so the add-repo picker and the per-user token
    /// page work before an admin narrows the list.
    /// </summary>
    private static readonly IReadOnlyList<RepositoryProvider> AllProviders =
        new[] { RepositoryProvider.GitHub, RepositoryProvider.AzureDevOps };

    /// <summary>
    /// The Git hosting providers this org allows project repositories on. An empty
    /// stored list (a never-configured org) resolves to <see cref="AllProviders"/>
    /// so the tool isn't broken before the setting is touched. Reads the cached
    /// <see cref="OrganizationConfig"/>, so it adds no DB round-trip.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryProvider>> GetAllowedProvidersAsync(CancellationToken ct = default)
    {
        var config = await GetCurrentAsync(ct);
        return ParseAllowedProviders(config.Settings.AllowedRepositoryProviders);
    }

    /// <summary>True when <paramref name="provider"/> is permitted for the current org.</summary>
    public async Task<bool> IsProviderAllowedAsync(RepositoryProvider provider, CancellationToken ct = default)
        => (await GetAllowedProvidersAsync(ct)).Contains(provider);

    /// <summary>
    /// Persists which providers the org allows. At least one is required — an empty
    /// selection throws <see cref="PlanValidationException"/> (field key
    /// <c>AllowedRepositoryProviders</c>) so the form renders the error inline.
    /// Members store their own per-provider tokens under their account.
    /// </summary>
    public async Task SaveAllowedProvidersAsync(IReadOnlyList<RepositoryProvider> providers, CancellationToken ct = default)
    {
        if (providers is null || providers.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AllowedRepositoryProviders"] = "Pick at least one source-control provider.",
            });
        }

        var orgId = RequireOrganizationId();
        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.AllowedRepositoryProviders = providers.Distinct().Select(p => p.ToDiscriminator()).ToList();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated allowed repository providers for org {OrgId} ({Providers}).",
            orgId, string.Join(", ", row.AllowedRepositoryProviders));
    }

    /// <summary>Maps the stored discriminators back to providers; empty means all allowed.</summary>
    private static IReadOnlyList<RepositoryProvider> ParseAllowedProviders(List<string> stored)
    {
        if (stored is null || stored.Count == 0) return AllProviders;
        var parsed = stored
            .Select(RepositoryProviders.FromDiscriminator)
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .Distinct()
            .ToList();
        return parsed.Count == 0 ? AllProviders : parsed;
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
                ["Name"] = "Organisation name must be 2-80 characters.",
            });
        }

        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (string.Equals(org.Name, trimmed, StringComparison.Ordinal)) return;
        org.Name = trimmed;
        await _db.SaveChangesAsync(ct);
        // Set, not Remove. We know the new name, so there is no reason to make
        // the next render go and fetch it -- and that fetch was what raced the
        // page's own queries (#551). Removing the entry is what turned a rename
        // into a 500 on the very next page load.
        _cache.Set(NameCacheKey(orgId), trimmed);
        _logger.LogInformation("Renamed org {OrgId} to {Name}.", orgId, trimmed);
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
            // Fence category 1 (pre-auth routing): maps a sign-in email's domain to one org;
            // pinned to d.Domain == domain.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => d.Organization)
            .FirstOrDefaultAsync(ct);
        return claim;
    }

    /// <summary>
    /// Replaces the logo for the current organisation with the supplied bytes.
    /// SVG uploads are rebuilt from an allow-list of elements and attributes by
    /// <see cref="SanitiseLogo"/> so the rendered logo can't smuggle JavaScript
    /// into a generated workspace or the admin preview; an SVG that isn't
    /// well-formed is refused.
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
                _db.OrganizationFiles.Add(NewOrganizationFile(orgId, input, i, now));
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
        var settings = await GetOrCreateSettingsAsync(orgId, ct);
        ApplySettingsFields(settings, settingsInput, now);
        settings.CodeWorkspaceJson = codeWorkspaceJson;

        // Files: drop everything and re-insert. Wipe-and-replace import means
        // callers have already confirmed the destructive operation.
        var existingFiles = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        _db.OrganizationFiles.RemoveRange(existingFiles);
        for (var i = 0; i < fileInputs.Count; i++)
        {
            _db.OrganizationFiles.Add(NewOrganizationFile(orgId, fileInputs[i], i, now));
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
    /// Sanitises an uploaded SVG by re-emitting it from a parsed document with
    /// an <b>allow-list</b> of elements and attributes: anything not named in
    /// <see cref="SvgAllowedElements"/> is dropped together with its subtree,
    /// and any attribute not in <see cref="SvgAllowedAttributes"/> is dropped.
    /// That removes <c>script</c>, <c>foreignObject</c>, <c>use</c>,
    /// <c>animate</c>, <c>animateTransform</c>, <c>set</c>, <c>image</c>,
    /// every <c>on*</c> handler and every namespace we don't understand (HTML
    /// or XLink content smuggled into an SVG) without needing a pattern per
    /// vector. <c>href</c> / <c>xlink:href</c> survive only when the scheme is
    /// http, https, a <c>data:image/*</c> URI, or a same-document fragment.
    ///
    /// <para>The <c>style</c> element and the <c>style</c> attribute survive a
    /// conservative content filter (<see cref="IsSafeCss"/>) instead of being
    /// dropped, because drawing tools put a logo's fills and strokes there and
    /// removing them renders most real logos black. <c>viewBox</c>,
    /// <c>xmlns</c> and the presentation attributes are preserved. The result
    /// is serialised without an XML declaration.</para>
    ///
    /// <para>The document is parsed with DTDs prohibited and no external
    /// resolver, so entity-expansion and external-entity tricks are refused by
    /// the parser. Content that is not well-formed XML — including the
    /// unterminated <c>&lt;script</c> that defeated the old regex pair — is
    /// rejected with a <see cref="PlanValidationException"/> rather than
    /// passed through half-cleaned.</para>
    ///
    /// <para>PNGs pass through unchanged. Public so the sanitiser can be
    /// exercised directly from tests.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">
    /// The bytes are not a well-formed SVG document.
    /// </exception>
    public static byte[] SanitiseLogo(string contentType, byte[] content)
    {
        if (!string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });
        }

        if (doc.Root is null || doc.Root.Name != SvgNs + "svg")
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });
        }

        var clean = CleanSvgElement(doc.Root)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });

        return Encoding.UTF8.GetBytes(clean.ToString(SaveOptions.DisableFormatting));
    }

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>
    /// The SVG elements a logo is allowed to contain. Anything else — script,
    /// foreignObject, use, image, the animation elements — is dropped with its
    /// subtree, the one exception being <c>a</c>, which is unwrapped so a
    /// linked logo keeps its artwork (see <see cref="UnwrapSvgAnchor"/>).
    /// <c>style</c> is allowed but its body must pass
    /// <see cref="IsSafeCss"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> SvgAllowedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "defs", "linearGradient", "radialGradient", "stop",
        "clipPath", "mask", "pattern", "symbol", "title", "desc", "style",
    };

    /// <summary>
    /// Substrings that disqualify a <c>style</c> attribute or a
    /// <c>&lt;style&gt;</c> body. Everything CSS can use to fetch a resource,
    /// evaluate script, or break back out into markup is here; a declaration
    /// list of plain colours and lengths — what a real logo export contains —
    /// trips none of it. Matched against the value lower-cased with all
    /// whitespace removed, so <c>url ( javascript :</c>-style padding can't
    /// slip past.
    /// </summary>
    private static readonly string[] SvgUnsafeCssMarkers =
    [
        "url(", "expression(", "@import", "javascript:", "behavior:", "-moz-binding", "</", "<",
    ];

    /// <summary>
    /// True when a <c>style</c> attribute value or <c>&lt;style&gt;</c> body is
    /// safe to keep. Drawing tools (Illustrator, Inkscape, Figma) put a logo's
    /// fills and strokes in exactly these two places — dropping them outright
    /// renders most real logos black — so they get a conservative filter rather
    /// than a ban. Failing it costs only that attribute or element, not the
    /// whole file.
    /// </summary>
    private static bool IsSafeCss(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        return !SvgUnsafeCssMarkers.Any(marker => cleaned.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Geometry, presentation and layout attributes a logo needs. Deliberately
    /// excludes every <c>on*</c> handler; <c>href</c> and <c>style</c> are
    /// handled separately because their values need a content check.
    /// </summary>
    private static readonly IReadOnlySet<string> SvgAllowedAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        // structure / geometry
        "id", "class", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "d", "points", "dx", "dy", "transform",
        "preserveAspectRatio", "version", "gradientUnits", "gradientTransform",
        "patternUnits", "patternContentUnits", "patternTransform", "clipPathUnits",
        "maskUnits", "maskContentUnits", "spreadMethod", "offset",
        // presentation
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray",
        "stroke-dashoffset", "opacity", "color", "display", "visibility",
        "clip-path", "clip-rule", "mask", "shape-rendering", "vector-effect",
        "stop-color", "stop-opacity", "paint-order", "overflow",
        // text
        "font-family", "font-size", "font-weight", "font-style", "letter-spacing",
        "text-anchor", "dominant-baseline", "xml:space",
    };

    /// <summary>
    /// Recursively copies <paramref name="source"/> keeping only allow-listed
    /// elements and attributes. Returns <c>null</c> when the element itself is
    /// not allowed, so the caller drops it and its subtree.
    /// </summary>
    private static XElement? CleanSvgElement(XElement source)
    {
        if (source.Name.Namespace != SvgNs || !SvgAllowedElements.Contains(source.Name.LocalName))
        {
            return null;
        }

        if (source.Name.LocalName == "style")
        {
            // A style block carries CSS, not markup: keep its text (CDATA
            // included, re-emitted as escaped text) only when it passes the
            // filter, and drop the whole element otherwise.
            var css = string.Concat(source.Nodes().OfType<XText>().Select(t => t.Value));
            return IsSafeCss(css) ? new XElement(SvgNs + "style", new XText(css)) : null;
        }

        var clean = new XElement(SvgNs + source.Name.LocalName);

        foreach (var attr in source.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                // Keep only the SVG default namespace declaration; a prefix
                // binding for XLink or HTML has nothing left to point at.
                if (attr.Name.LocalName == "xmlns" && attr.Value == SvgNs.NamespaceName)
                {
                    clean.SetAttributeValue("xmlns", SvgNs.NamespaceName);
                }
                continue;
            }

            var isHref = attr.Name.LocalName == "href"
                && (attr.Name.Namespace == XNamespace.None || attr.Name.Namespace == XLinkNs);
            if (isHref)
            {
                if (IsSafeSvgHref(attr.Value))
                {
                    clean.SetAttributeValue("href", attr.Value);
                }
                continue;
            }

            if (attr.Name == "style")
            {
                if (IsSafeCss(attr.Value))
                {
                    clean.SetAttributeValue("style", attr.Value);
                }
                continue;
            }

            // Namespaced attributes (xlink:*, HTML, editor metadata) are out,
            // except xml:space which is plain markup.
            var name = attr.Name.Namespace == XNamespace.None
                ? attr.Name.LocalName
                : attr.Name.Namespace == XNamespace.Xml ? "xml:" + attr.Name.LocalName : null;
            if (name is null || !SvgAllowedAttributes.Contains(name))
            {
                continue;
            }
            if (name == "xml:space")
            {
                clean.SetAttributeValue(XNamespace.Xml + "space", attr.Value);
            }
            else
            {
                clean.SetAttributeValue(name, attr.Value);
            }
        }

        foreach (var node in source.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == SvgNs + "a":
                    // Exporters wrap a whole logo group in <a> when the designer
                    // added a link. Unwrap it — the artwork inside is promoted
                    // into this element and the link itself (with its href and
                    // any target/on* attributes) is discarded. This is the one
                    // unwrapped element: every other unknown name stays dropped
                    // with its subtree.
                    foreach (var unwrapped in UnwrapSvgAnchor(child))
                    {
                        clean.Add(unwrapped);
                    }
                    break;
                case XElement child:
                    var cleanChild = CleanSvgElement(child);
                    if (cleanChild is not null) clean.Add(cleanChild);
                    break;
                case XText text:
                    clean.Add(new XText(text.Value));
                    break;
                // Comments, processing instructions and CDATA are dropped.
            }
        }

        return clean;
    }

    /// <summary>
    /// Cleaned children of an <c>&lt;a&gt;</c>, with nested anchors unwrapped
    /// too. The anchor's own attributes never survive, so a
    /// <c>javascript:</c> href goes with it.
    /// </summary>
    private static IEnumerable<XElement> UnwrapSvgAnchor(XElement anchor)
    {
        foreach (var child in anchor.Elements())
        {
            if (child.Name == SvgNs + "a")
            {
                foreach (var nested in UnwrapSvgAnchor(child)) yield return nested;
                continue;
            }
            var clean = CleanSvgElement(child);
            if (clean is not null) yield return clean;
        }
    }

    /// <summary>
    /// True when an <c>href</c> on an allow-listed SVG element is safe to keep:
    /// a same-document fragment, an http(s) URL, or a <c>data:image/*</c> URI.
    /// Whitespace and control characters are stripped first because browsers
    /// ignore them when parsing the scheme — the same evasion
    /// <see cref="MarkdownRenderer.IsSafeUrl"/> defends against.
    /// </summary>
    private static bool IsSafeSvgHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cleaned = string.Concat(value.Where(c => c > ' '));
        if (cleaned.Length == 0) return false;
        if (cleaned[0] == '#') return true;

        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return cleaned.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
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
        // The logo path is written verbatim into the generated app.json and a ZIP
        // entry path. Validate it with the same single-relative-path / no-'..'
        // rule as org files so a traversal value can't escape the extraction root
        // on the end user's machine. See issue #369.
        if (!string.IsNullOrWhiteSpace(input.DefaultLogo))
        {
            var logo = input.DefaultLogo.Trim().Replace('\\', '/');
            if (!PathRegex.IsMatch(logo) || logo.Contains(".."))
            {
                errors[nameof(input.DefaultLogo)] =
                    "Use a relative path with letters, digits, '_', '-', '.' and '/'. No '..' segments.";
            }
        }
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

/// <summary>
/// Audit-friendly view of the per-org machine-translation settings. Carries
/// <see cref="HasApiKey"/> rather than the key itself.
/// </summary>
public record MtSettingsView(string Provider, bool HasApiKey, ALDevToolbox.Domain.ValueObjects.MtTrigger Trigger);

/// <summary>
/// Form-post shape for the machine-translation admin page. An empty
/// <see cref="ApiKey"/> leaves the stored key untouched; set
/// <see cref="ClearApiKey"/> to wipe it.
/// </summary>
public record MtSettingsInput(
    string? Provider,
    string? ApiKey,
    bool ClearApiKey,
    ALDevToolbox.Domain.ValueObjects.MtTrigger Trigger);

/// <summary>One row submitted by the always-included files editor.</summary>
public record OrganizationFileInput(
    int? Id,
    string Path,
    string Content,
    bool MustacheEnabled,
    ALDevToolbox.Domain.ValueObjects.OrganizationFileScope Scope = ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot);
