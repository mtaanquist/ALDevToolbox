using System.Text.RegularExpressions;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Shared validation regexes used across services. Mirrors the HTML <c>pattern</c>
/// attributes on the admin forms — keep this file and the form attributes in sync.
/// </summary>
public static class ValidationPatterns
{
    /// <summary>
    /// Lowercase-alphanumeric-hyphen keys used as stable identifiers for
    /// templates, modules and application versions. Mirrors
    /// <c>pattern="[a-z0-9-]+"</c> on the admin forms.
    /// </summary>
    public static readonly Regex Key = new("^[a-z0-9-]+$", RegexOptions.Compiled);
}
