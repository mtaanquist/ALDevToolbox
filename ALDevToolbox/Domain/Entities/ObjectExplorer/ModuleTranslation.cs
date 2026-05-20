namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One <c>&lt;trans-unit&gt;</c> from an XLIFF translation file attached to a
/// <see cref="Module"/>. Rows arrive either from the .app archive's
/// <c>Translations/</c> folder during the initial Release import, or from an
/// admin's manual single-file / per-release ZIP upload against an already
/// ingested Release (see GitHub issue #151). Re-uploading the same
/// <c>(module, language)</c> pair clobbers the previous rows.
///
/// The XLIFF id (e.g. <c>Table 792343850 - Field 1010272445 - Property 1295455071</c>)
/// uses BC's hashed numeric ids and isn't directly resolvable to AL object
/// numbers. The human-readable lookup info comes from the "Xliff Generator"
/// note, which the parser splits into <see cref="ObjectKind"/> /
/// <see cref="ObjectName"/> / <see cref="SubKind"/> / <see cref="SubName"/> /
/// <see cref="PropertyName"/> so the MCP search tool can return "Table
/// AppSetup field Activate Assembly On Service caption" directly.
/// <see cref="SymbolId"/> is best-effort resolved to a row in
/// <see cref="ModuleSymbol"/> for kinds we already symbolise (fields,
/// labels, procedures); page-control and enum-value navigation is deferred
/// to issue #151's v2.
/// </summary>
public class ModuleTranslation
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    /// <summary>BCP-47 target locale taken from <c>&lt;file target-language="..."&gt;</c>, e.g. <c>da-DK</c>.</summary>
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>Raw <c>id</c> attribute of the trans-unit — kept verbatim for clobber de-duplication.</summary>
    public string TransUnitId { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;
    public string TargetText { get; set; } = string.Empty;

    /// <summary>The <c>state</c> attribute on <c>&lt;target&gt;</c> (e.g. <c>translated</c>, <c>needs-review-translation</c>).</summary>
    public string? TargetState { get; set; }

    /// <summary>
    /// Bucketed category derived from the parsed property name:
    /// <c>caption</c>, <c>tooltip</c>, <c>label</c>, <c>instructional</c>,
    /// <c>option</c>, or <c>other</c>. Drives the MCP tool's kind filter so
    /// agents can default to captions + labels (errors) and opt into
    /// tooltips when needed.
    /// </summary>
    public string Kind { get; set; } = "other";

    /// <summary>Lower-cased AL object kind from the developer note (<c>table</c>, <c>page</c>, <c>pageextension</c>, <c>codeunit</c>, <c>report</c>, …).</summary>
    public string? ObjectKind { get; set; }

    /// <summary>Unquoted object name from the developer note (e.g. <c>AppSetup</c>).</summary>
    public string? ObjectName { get; set; }

    /// <summary>Sub-element kind from the developer note: <c>field</c>, <c>control</c>, <c>action</c>, <c>namedtype</c>, <c>value</c>. Null for top-level property translations.</summary>
    public string? SubKind { get; set; }

    /// <summary>Sub-element name (e.g. <c>Activate Assembly On Service</c>). Null when <see cref="SubKind"/> is null.</summary>
    public string? SubName { get; set; }

    /// <summary>Property name (<c>Caption</c>, <c>ToolTip</c>, <c>InstructionalText</c>, …). Null for label / named-type translations.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Raw <c>&lt;note from="Xliff Generator"&gt;</c> body — debug aid + fallback when the structured fields can't be parsed.</summary>
    public string? DeveloperNote { get; set; }

    /// <summary>
    /// Best-effort link into <see cref="ModuleSymbol"/> when the lookup hint
    /// resolves to a kind we symbolise (fields, labels, procedures). Page
    /// controls, page actions and enum values are intentionally left null in
    /// v1 — see issue #151.
    /// </summary>
    public long? SymbolId { get; set; }
    public ModuleSymbol? Symbol { get; set; }

    public DateTime CreatedAt { get; set; }
}
