namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Which sign-in methods an organisation's members may use. Stored per org on
/// <see cref="Entities.OrganizationSettings.LocalLoginPolicy"/>. The column
/// lands ahead of its enforcement (see the Entra tracking issue #552): the
/// login page, <c>AuthService</c>, magic-link and password-reset flows start
/// consulting it when Microsoft sign-in ships. SiteAdmins are always exempt
/// from <see cref="EntraOnly"/> as break-glass access.
/// </summary>
public enum LocalLoginPolicy
{
    /// <summary>Password, magic link, passkeys and Microsoft sign-in are all allowed.</summary>
    AllowAll,

    /// <summary>Only Microsoft (Entra ID) sign-in is allowed for this org's members.</summary>
    EntraOnly,
}
