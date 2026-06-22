namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// When the Translator should call the per-tenant machine-translation provider.
/// Stored as an <c>int</c> on <c>organization_settings</c>; <see cref="Off"/> is
/// the default and doubles as the feature's master switch — there is no separate
/// "enabled" flag. See <c>.design/translator/</c> for the feature design.
/// </summary>
public enum MtTrigger
{
    /// <summary>Machine translation is disabled for the organisation (default). No external calls.</summary>
    Off = 0,

    /// <summary>Only translate when the user explicitly clicks the action in the editor (cheapest active mode).</summary>
    OnDemand = 1,

    /// <summary>Translate automatically for a unit only when the translation memory has no exact (≥0.99) match.</summary>
    AutoWhenNoExactMatch = 2,

    /// <summary>Translate automatically for every selected unit (results are cached per source to avoid re-billing).</summary>
    AlwaysAuto = 3,
}
