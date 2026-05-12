namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// How the affix declared by a template's <c>defaults</c> block is applied to
/// AL object names. The single placeholder <c>{{affix}}</c> is substituted with
/// the affix string for <see cref="Prefix"/> and <see cref="Suffix"/>, and with
/// the empty string for <see cref="None"/>. Position (before / after the
/// surrounding token) is implicit in how the template author wrote the literal
/// — the enum exists so the editor can flag the convention and so
/// <see cref="None"/> can collapse <c>{{affix}}</c> to empty cleanly.
/// </summary>
public enum AffixType
{
    None = 0,
    Prefix = 1,
    Suffix = 2,
}
