using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Translation.Providers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Owns the per-organisation machine-translation settings block: the provider
/// discriminator, the trigger, and the bring-your-own API key encrypted with the
/// Data Protection key ring. Reads go through
/// <see cref="OrganizationConfigService"/> so they share its cache, and every
/// write invalidates it.
/// </summary>
public class MachineTranslationSettingsService
{
    /// <summary>Data Protection purpose string for the per-org machine-translation API key.</summary>
    public const string MachineTranslationApiKeyProtectionPurpose = "ALDevToolbox.OrganizationSettings.MachineTranslationApiKey";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<MachineTranslationSettingsService> _logger;
    private readonly IDataProtector _mtApiKeyProtector;

    public MachineTranslationSettingsService(
        AppDbContext db,
        IOrganizationContext orgContext,
        OrganizationConfigService config,
        ILogger<MachineTranslationSettingsService> logger,
        IDataProtectionProvider protectionProvider)
    {
        _db = db;
        _orgContext = orgContext;
        _config = config;
        _logger = logger;
        _mtApiKeyProtector = protectionProvider.CreateProtector(MachineTranslationApiKeyProtectionPurpose);
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Audit-friendly view of the org's machine-translation settings for the
    /// admin form. Carries <see cref="MtSettingsView.HasApiKey"/> rather than the
    /// key itself — plaintext only ever materialises inside
    /// <see cref="ResolveMachineTranslationAsync"/>.
    /// </summary>
    public async Task<MtSettingsView> GetMachineTranslationViewAsync(CancellationToken ct = default)
    {
        var config = await _config.GetCurrentAsync(ct);
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
        _config.InvalidateCache(orgId);

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
        var config = await _config.GetCurrentAsync(ct);
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
}

/// <summary>
/// Audit-friendly view of the per-org machine-translation settings. Carries
/// <see cref="HasApiKey"/> rather than the key itself.
/// </summary>
public record MtSettingsView(string Provider, bool HasApiKey, MtTrigger Trigger);

/// <summary>
/// Form-post shape for the machine-translation admin page. An empty
/// <see cref="ApiKey"/> leaves the stored key untouched; set
/// <see cref="ClearApiKey"/> to wipe it.
/// </summary>
public record MtSettingsInput(
    string? Provider,
    string? ApiKey,
    bool ClearApiKey,
    MtTrigger Trigger);
