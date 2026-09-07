namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// How an organisation decides the prefix that <c>{{extension_prefix}}</c>
/// renders to - the word in front of every generated extension's name, as in
/// "JM Core". An organisation convention rather than a per-template one, which
/// is why it lives on <see cref="Entities.OrganizationSettings"/>.
/// Specified in <c>.design/customer-naming.md</c>.
/// </summary>
public enum ExtensionPrefixMode
{
    /// <summary>
    /// No prefix convention: the field is not on the New Workspace form and the
    /// customer's short name is used, so <c>"{{extension_prefix}} Core"</c>
    /// still renders as "JM Core" without anyone typing it twice.
    /// </summary>
    Hidden = 0,

    /// <summary>
    /// One prefix for every workspace, from
    /// <see cref="Entities.OrganizationSettings.ExtensionPrefix"/>. For
    /// organisations whose extension names carry the partner's mark rather than
    /// the customer's. The field is not on the form.
    /// </summary>
    Fixed = 1,

    /// <summary>
    /// The consultant chooses per workspace, pre-filled with the organisation's
    /// value when it has one and the customer's short name otherwise.
    /// </summary>
    PerWorkspace = 2,
}
