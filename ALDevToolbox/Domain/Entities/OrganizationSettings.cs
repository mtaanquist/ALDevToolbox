using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Per-organisation defaults used to pre-fill the New Workspace and New
/// Extension forms (Milestone P3.14). Exactly one row per organisation;
/// validation matches the rules in <see cref="Services.GenerationService"/>.
/// </summary>
public class OrganizationSettings
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Default <c>app.json</c> publisher for new templates and extensions.</summary>
    public string DefaultPublisher { get; set; } = string.Empty;

    /// <summary>Default <c>app.json</c> <c>url</c> for every generated extension.</summary>
    public string? DefaultUrl { get; set; }

    /// <summary>Default <c>app.json</c> <c>logo</c> path for every generated extension.</summary>
    public string? DefaultLogo { get; set; }

    /// <summary>
    /// Org-wide list of country codes that get spliced into every generated
    /// <c>AppSourceCop.json</c>'s <c>supportedCountries</c> array. Org-wide
    /// because the supported markets are an organisation policy, not a
    /// per-template choice.
    /// </summary>
    public List<string> DefaultSupportedCountries { get; set; } = new();

    /// <summary>Lower bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeFrom { get; set; }

    /// <summary>Upper bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeTo { get; set; }

    /// <summary>One-line default brief copied into the form's <c>Brief</c> field.</summary>
    public string DefaultBrief { get; set; } = string.Empty;

    /// <summary>Longer default description copied into the form's <c>Description</c> field.</summary>
    public string DefaultCoreDescription { get; set; } = string.Empty;

    /// <summary>
    /// Admin-editable JSON template for the workspace's
    /// <c>{{short_name}}.code-workspace</c> file. The generator runs mustache
    /// substitution over it, then overlays a computed <c>folders</c> array
    /// before writing the file — so the admin owns <c>settings</c> and any
    /// other top-level keys, and the generator owns the folder list.
    /// </summary>
    public string CodeWorkspaceJson { get; set; } = OrganizationDefaults.CodeWorkspaceJson;

    /// <summary>
    /// Admin-authored Markdown shown to MCP agents by the
    /// <c>get_cookbook_guidance</c> tool before they call
    /// <c>suggest_recipe</c>. Empty by default; the guidance tool always
    /// returns built-in copy describing what each <c>RecipeType</c> means
    /// so an empty org-level guidance still steers the agent.
    /// </summary>
    public string CookbookGuidance { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/>, every active member of this organisation
    /// must have at least one strong-auth method enrolled (TOTP, email-MFA,
    /// or a passkey). Users without one land on <c>/account?required=1</c>
    /// on their next request and can't reach anything else until they
    /// enrol. The toggle itself refuses to flip on if the saving admin
    /// doesn't yet satisfy the requirement — a small foot-gun guard so an
    /// admin can't lock themselves out by accident.
    /// </summary>
    public bool RequireStrongAuth { get; set; }

    /// <summary>
    /// When <see langword="true"/>, a visitor who verifies an email whose
    /// domain this organisation has claimed (see
    /// <see cref="OrganizationEmailDomain"/>) joins as an Active
    /// <see cref="UserRole.User"/> immediately — no admin approval. When
    /// <see langword="false"/> (the default) such a signup lands as Pending and
    /// waits for an admin to approve it via <c>/admin/administration/users</c>,
    /// the historical existing-org behaviour. Only consulted by the verified,
    /// email-first signup flow; it has no effect on the SMTP-off fallback.
    /// </summary>
    public bool AutoJoinVerifiedDomainUsers { get; set; }

    /// <summary>
    /// Which third-party machine-translation backend this org uses (discriminator
    /// for <c>MachineTranslationProviderFactory</c>). Defaults to <c>"deepl"</c>,
    /// the only backend today.
    /// </summary>
    public string MachineTranslationProvider { get; set; } = "deepl";

    /// <summary>
    /// The org's machine-translation API key, encrypted with the Data Protection
    /// key ring (purpose
    /// <see cref="Services.OrganizationConfigService.MachineTranslationApiKeyProtectionPurpose"/>).
    /// Null when unset. Losing <c>app-keys</c> requires re-entering it. The audit
    /// interceptor redacts this column so ciphertext never lands in history.
    /// </summary>
    public string? MachineTranslationApiKeyEncrypted { get; set; }

    /// <summary>
    /// When the Translator calls the provider. <see cref="MtTrigger.Off"/> (the
    /// default) disables the feature entirely — it doubles as the master switch,
    /// so there is no separate enabled flag.
    /// </summary>
    public MtTrigger MachineTranslationTrigger { get; set; } = MtTrigger.Off;

    /// <summary>
    /// When <see langword="true"/>, the <c>ReleaseAutoImportScheduler</c> imports
    /// the newest Microsoft <em>OnPrem</em> Business Central release for this org
    /// once a day (skipping versions already in the catalogue). Doubles as the
    /// feature's master switch — there is no separate enabled flag. Only OnPrem
    /// artifacts ship the loose <c>.app</c> files the Object Explorer walks, so
    /// the artifact type isn't configurable. See <c>.design/object-explorer.md</c>.
    /// </summary>
    public bool AutoImportReleasesEnabled { get; set; }

    /// <summary>
    /// BC localisation/country code the auto-import fetches, e.g. <c>dk</c> or
    /// <c>w1</c>. Required when <see cref="AutoImportReleasesEnabled"/>; uppercased
    /// into the generated release label "Business Central {Major}.{Minor} ({CC})".
    /// </summary>
    public string? AutoImportCountry { get; set; }

    public DateTime UpdatedAt { get; set; }
}
