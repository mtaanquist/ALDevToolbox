using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Changes one value inside an <c>app.json</c> and leaves every other byte of
/// the file exactly as it was (issue #630).
///
/// <para><strong>Why not parse and re-serialise.</strong> A manifest is a file
/// people maintain: the order of its properties, its indentation, its trailing
/// comma and the comment somebody left above <c>idRanges</c> are all theirs. A
/// pull request that bumps <c>application</c> and reflows the other forty lines
/// is a pull request nobody can review, so the edit is done on the text: the
/// reader finds where the value sits, and only those bytes are replaced.</para>
///
/// <para><see cref="System.Text.Json.Utf8JsonReader.TokenStartIndex"/> is what
/// makes that possible - it says where in the input each token began, so the
/// value can be located precisely rather than matched with a regular
/// expression that would also hit the same version string somewhere else in
/// the file.</para>
///
/// <para>Every method returns <see langword="null"/> when it cannot make the
/// change safely; the caller falls back to a whole-document rewrite and says
/// so in its log. See <c>.design/github-integration-phase2.md</c>, issue #630.</para>
/// </summary>
internal static class AppJsonValueEditor
{
    /// <summary>The one byte-order mark a Windows editor leaves on an app.json, kept aside so the indices below line up.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>
    /// Replaces a top-level string property's value - <c>application</c> or
    /// <c>platform</c>. Null when the property is not there, is not a string,
    /// or the text cannot be read as JSON.
    /// </summary>
    public static string? ReplaceRootProperty(string json, string property, string newValue) =>
        Replace(json, newValue, (ref Utf8JsonReader reader) => FindRootProperty(ref reader, property));

    /// <summary>
    /// Replaces the <c>version</c> of the entry in <c>dependencies</c> whose id
    /// is <paramref name="dependencyId"/> (matched however the manifest spells
    /// it - <c>id</c> or the older <c>appId</c>, braces and case ignored). Null
    /// when there is no such entry or it states no version.
    /// </summary>
    public static string? ReplaceDependencyVersion(string json, string dependencyId, string newValue) =>
        Replace(json, newValue, (ref Utf8JsonReader reader) => FindDependencyVersion(ref reader, dependencyId));

    /// <summary>
    /// The last resort: parse the whole document and write it back indented.
    /// Formatting is lost, which is why the caller logs when it comes to this,
    /// but a manifest the reader could not walk still gets its bump.
    /// </summary>
    public static string? RewriteWholeDocument(string json, Action<JsonObject> edit)
    {
        var (mark, body) = Split(json);
        try
        {
            if (JsonNode.Parse(body, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) is not JsonObject root)
            {
                return null;
            }

            edit(root);
            return mark + root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a leading byte-order mark off the front so the reader sees JSON
    /// and the byte indices it reports line up with the body. The mark is put
    /// back on the way out, because removing it would be a change to the file
    /// the pull request never claimed to make.
    /// </summary>
    private static (string Mark, string Body) Split(string json) =>
        json.Length > 0 && json[0] == ByteOrderMark
            ? (ByteOrderMark.ToString(), json[1..])
            : (string.Empty, json);

    /// <summary>Where a value sits in the text: the byte range covering it, quotes included.</summary>
    private readonly record struct ValueSpan(int Start, int Length);

    private delegate ValueSpan? Locator(ref Utf8JsonReader reader);

    private static string? Replace(string json, string newValue, Locator locate)
    {
        var (mark, body) = Split(json);
        var bytes = Encoding.UTF8.GetBytes(body);

        ValueSpan? found;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            found = locate(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }

        if (found is not { } span) return null;
        if (span.Start < 0 || span.Start + span.Length > bytes.Length) return null;
        // The span has to be the quoted string it was read as; anything else
        // means the indices and the text have drifted apart and the edit would
        // corrupt the file.
        if (bytes[span.Start] != (byte)'"' || bytes[span.Start + span.Length - 1] != (byte)'"') return null;

        var replacement = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newValue));
        var result = new byte[bytes.Length - span.Length + replacement.Length];
        Array.Copy(bytes, 0, result, 0, span.Start);
        Array.Copy(replacement, 0, result, span.Start, replacement.Length);
        Array.Copy(bytes, span.Start + span.Length, result, span.Start + replacement.Length,
            bytes.Length - span.Start - span.Length);
        return mark + Encoding.UTF8.GetString(result);
    }

    private static ValueSpan? FindRootProperty(ref Utf8JsonReader reader, string property)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return null;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var isWanted = reader.CurrentDepth == 1 && reader.ValueTextEquals(property);
            if (!reader.Read()) return null;
            if (isWanted)
            {
                return reader.TokenType == JsonTokenType.String ? Span(ref reader) : null;
            }
            Skip(ref reader);
        }
        return null;
    }

    private static ValueSpan? FindDependencyVersion(ref Utf8JsonReader reader, string dependencyId)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return null;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var isDependencies = reader.CurrentDepth == 1 && reader.ValueTextEquals("dependencies");
            if (!reader.Read()) return null;
            if (!isDependencies)
            {
                Skip(ref reader);
                continue;
            }
            if (reader.TokenType != JsonTokenType.StartArray) return null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    Skip(ref reader);
                    continue;
                }
                if (ReadDependency(ref reader, dependencyId) is { } span) return span;
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// Walks one entry of the dependency array from its <c>{</c>, remembering
    /// where its <c>version</c> sits and what its id says - the two can come in
    /// either order, and the manifests in the wild use both.
    /// </summary>
    private static ValueSpan? ReadDependency(ref Utf8JsonReader reader, string dependencyId)
    {
        ValueSpan? version = null;
        var idMatches = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var isVersion = reader.ValueTextEquals("version");
            var isId = reader.ValueTextEquals("id") || reader.ValueTextEquals("appId");
            if (!reader.Read()) break;

            if (isVersion && reader.TokenType == JsonTokenType.String)
            {
                version = Span(ref reader);
            }
            else if (isId && reader.TokenType == JsonTokenType.String)
            {
                idMatches = NormaliseId(reader.GetString()) == NormaliseId(dependencyId);
            }
            else
            {
                Skip(ref reader);
            }
        }

        return idMatches ? version : null;
    }

    /// <summary>An app id compares the way AL means it: braces and case are decoration.</summary>
    internal static string NormaliseId(string? id) =>
        (id ?? string.Empty).Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>The byte range of the string token the reader is on, its quotes included.</summary>
    private static ValueSpan Span(ref Utf8JsonReader reader) =>
        new((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex));

    /// <summary>Steps over a value the walk does not care about, whatever shape it is.</summary>
    private static void Skip(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }
}
