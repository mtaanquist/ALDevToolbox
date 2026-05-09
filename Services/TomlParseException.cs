namespace ALDevToolbox.Services;

/// <summary>
/// One parse-time complaint from <see cref="TemplateTomlMapper.FromToml"/>,
/// with a 1-based line/column so the admin TOML editor can light up the
/// offending row in the gutter.
/// </summary>
public sealed record TomlParseIssue(int Line, int Column, string Message);

/// <summary>
/// Raised by <see cref="TemplateTomlMapper.FromToml"/> when the input TOML
/// fails to parse. The admin editor reaches in for the structured
/// <see cref="Issues"/> list to render gutter markers; other callers can fall
/// back on <see cref="Exception.Message"/>.
/// </summary>
/// <remarks>
/// Doesn't derive from <see cref="InvalidDataException"/> because that type is
/// sealed in .NET 10. Callers that previously caught the wider type need a
/// dedicated <c>catch (TomlParseException)</c> branch.
/// </remarks>
public sealed class TomlParseException : Exception
{
    public IReadOnlyList<TomlParseIssue> Issues { get; }

    public TomlParseException(string message, IReadOnlyList<TomlParseIssue> issues, Exception? inner = null)
        : base(message, inner)
    {
        Issues = issues;
    }
}
