using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Extracts the object declaration (type, id, name) and the optional namespace
/// from an AL source file. Used by Object Explorer's import pipeline to
/// populate the per-file metadata columns.
///
/// Since AL added namespaces, the declaration is no longer guaranteed to be
/// the first non-comment line: <c>namespace X.Y.Z;</c> and one-or-more
/// <c>using X.Y;</c> lines (plus attributes like <c>[Obsolete]</c>) can sit
/// above it. The parser strips comments, captures the namespace, then scans
/// for the first line that matches a declaration shape
/// <c>&lt;type&gt; [id] &lt;name&gt;</c>.
/// </summary>
public static class AlDeclarationParser
{
    /// <summary>The lower-cased AL declaration keywords this parser recognises.</summary>
    public static readonly IReadOnlyList<string> KnownObjectTypes = new[]
    {
        "codeunit",
        "table",
        "page",
        "report",
        "query",
        "xmlport",
        "controladdin",
        "enum",
        "enumextension",
        "interface",
        "permissionset",
        "permissionsetextension",
        "profile",
        "pageextension",
        "pagecustomization",
        "tableextension",
        "reportextension",
        "dotnet",
    };

    private static readonly Regex DeclarationRegex = new(
        @"^\s*(?<type>codeunit|tableextension|pageextension|reportextension|enumextension|permissionsetextension|pagecustomization|table|page|report|query|xmlport|controladdin|enum|interface|permissionset|profile|dotnet)\b" +
        @"(?:\s+(?<id>\d+))?" +
        @"\s+(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+(?<ns>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parses the declaration from AL source. Returns <c>null</c> when no
    /// recognised declaration is found (the import pipeline records the file
    /// but flags it in the import summary).
    /// </summary>
    public static AlDeclaration? Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        // Trim UTF-8 BOM if it survived the stream reader.
        if (source.Length > 0 && source[0] == '﻿') source = source.Substring(1);

        // Drop block comments outright; line comments are dropped per-line below.
        var stripped = BlockCommentRegex.Replace(source, " ");

        string? ns = null;
        AlDeclaration? declaration = null;

        foreach (var rawLine in stripped.Split('\n'))
        {
            var line = rawLine;
            // Strip line comment if present (but keep the part before //).
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0) line = line.Substring(0, commentIdx);

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // Skip using statements, attributes, pragmas — they may legally
            // sit between the namespace line and the object declaration.
            if (trimmed.StartsWith("using ", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("[")) continue;
            if (trimmed.StartsWith("#")) continue;

            if (ns is null)
            {
                var nsMatch = NamespaceRegex.Match(line);
                if (nsMatch.Success)
                {
                    ns = nsMatch.Groups["ns"].Value;
                    continue;
                }
            }

            var match = DeclarationRegex.Match(line);
            if (match.Success)
            {
                var type = match.Groups["type"].Value.ToLowerInvariant();
                int? id = null;
                if (match.Groups["id"].Success && int.TryParse(match.Groups["id"].Value, out var parsedId))
                {
                    id = parsedId;
                }

                var rawName = match.Groups["name"].Value;
                var name = rawName.StartsWith("\"") && rawName.EndsWith("\"") && rawName.Length >= 2
                    ? rawName.Substring(1, rawName.Length - 2)
                    : rawName;

                declaration = new AlDeclaration(type, id, name, ns);
                break;
            }
        }

        return declaration;
    }
}

/// <summary>Parsed declaration metadata extracted from an AL file.</summary>
public sealed record AlDeclaration(string Type, int? Id, string Name, string? Namespace);
