namespace ALDevToolbox.Services;

/// <summary>
/// One parse-time complaint from <see cref="TemplateTomlMapper.FromToml"/>,
/// with a 1-based line/column so the admin TOML editor can light up the
/// offending row in the gutter.
/// </summary>
public sealed record TomlParseIssue(int Line, int Column, string Message);

/// <summary>
/// Raised by <see cref="TemplateTomlMapper.FromToml"/> when the input TOML
/// fails to parse. Inherits from <see cref="InvalidDataException"/> so existing
/// callers that already catch the wider type keep working — only the admin
/// editor reaches in for the structured <see cref="Issues"/> list.
/// </summary>
public sealed class TomlParseException : InvalidDataException
{
    public IReadOnlyList<TomlParseIssue> Issues { get; }

    public TomlParseException(string message, IReadOnlyList<TomlParseIssue> issues, Exception? inner = null)
        : base(message, inner)
    {
        Issues = issues;
    }
}
