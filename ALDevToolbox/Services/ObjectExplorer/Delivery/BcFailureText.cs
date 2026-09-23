using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// What a failed install's text says, taken apart once. Business Central reports a failed
/// app operation as an <c>errorMessage</c> that wraps a JSON fragment in a sentence of its
/// own ("A request to the Data Plane Admin Service failed. Http status code: BadRequest
/// Error: { "code": ..., "message": ... }"). The codes are the only part safe to branch on;
/// the message is in the <em>environment's</em> language and is shown as given, never
/// translated and never mined for details (<c>.design/saas-delivery.md</c>, "Failure
/// detail comes from the codes, never the message").
/// </summary>
/// <param name="Code">The top-level <c>code</c>, or empty.</param>
/// <param name="InnerCode"><c>innerError.code</c>, or empty.</param>
/// <param name="Message">Business Central's own message, verbatim; the whole text when there was no JSON to take it from.</param>
/// <param name="InnerMessage"><c>innerError.message</c> when it says something the outer message does not, else empty.</param>
/// <param name="FromBusinessCentral">True when the text carried Business Central's wrapper or error JSON, so <see cref="Message"/> is its words rather than ours.</param>
/// <param name="Truncated">True when the JSON was cut off before it closed (rows stored before #930 were clipped at 300 characters).</param>
public sealed record BcFailureDetail(
    string Code,
    string InnerCode,
    string Message,
    string InnerMessage,
    bool FromBusinessCentral,
    bool Truncated)
{
    public static readonly BcFailureDetail Empty = new("", "", "", "", false, false);
}

/// <summary>
/// Parses Business Central's failure text (<see cref="Parse"/>) and maps the error codes
/// we know to a sentence and a next step. Issue #930.
/// </summary>
public static class BcFailureText
{
    /// <summary>The one code with a sentence of its own so far: a schema change the sync mode would not make.</summary>
    public const string ExtensionChangeFailed = "ExtensionChangeFailed";

    // "A request to the Data Plane Admin Service failed. Http status code: BadRequest Error:"
    // tells a reader nothing the code doesn't, so it goes. Matched loosely: the status word
    // varies, and so may the spacing.
    private static readonly Regex Wrapper = new(
        @"A request to the Data Plane Admin Service failed\.\s*(Http status code:\s*\S+\s*)?(Error:\s*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The run's own opening, which it writes in front of Business Central's text on the
    // log line for a failed install (and, before #930, on the stored failure too):
    // "Business Central reported the install as failed (ExtensionChangeFailed / TenantSyncFailure)."
    // It is ours, so reading the codes back out of it is not reading Business Central's prose.
    private static readonly Regex RunPrefix = new(
        @"^\s*Business Central reported the install as failed(?:\s*\((?<codes>[^)]*)\))?\.\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // For JSON that was cut off before it closed: "key": "value" with escapes, the value
    // allowed to run to the end of the text.
    private static Regex Field(string name) => new(
        "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)(\"|\\\\?$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex CodeField = Field("code");
    private static readonly Regex MessageField = Field("message");

    /// <summary>
    /// Takes a failure text apart: the embedded JSON's <c>code</c>, <c>message</c> and
    /// <c>innerError</c>, with the Data Plane Admin Service wrapper dropped. Text with no
    /// JSON comes back whole as the message (the wrapper and the run's own prefix
    /// removed). Never throws.
    /// </summary>
    public static BcFailureDetail Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return BcFailureDetail.Empty;

        var prefix = RunPrefix.Match(text);
        var prefixCodes = prefix.Success && prefix.Groups["codes"].Success
            ? prefix.Groups["codes"].Value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
        var body = prefix.Success ? text[prefix.Length..] : text;
        var fromBc = prefix.Success || Wrapper.IsMatch(body);

        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        BcFailureDetail? detail = null;

        if (start >= 0 && end > start && TryParseJson(body[start..(end + 1)], out var parsed))
        {
            detail = parsed;
        }
        else if (start >= 0)
        {
            // JSON that starts but never closes: rows stored before #930 were clipped at
            // 300 characters, usually inside the message. Read what is there.
            var fragment = body[start..];
            var code = CodeField.Match(fragment);
            var message = MessageField.Match(fragment);
            if (code.Success || message.Success)
            {
                var closed = message.Success && message.Groups[2].Value == "\"";
                detail = new BcFailureDetail(
                    code.Success ? Unescape(code.Groups[1].Value) : "",
                    "",
                    message.Success ? Unescape(message.Groups[1].Value).Trim() : "",
                    "",
                    FromBusinessCentral: true,
                    Truncated: !closed);
            }
        }

        if (detail is null)
        {
            var rest = Wrapper.Replace(body, "").Trim();
            detail = new BcFailureDetail("", "", fromBc ? rest : text.Trim(), "", fromBc, false);
        }

        // The codes the run read from the operation itself, when the text carries none.
        if (detail.Code.Length == 0 && prefixCodes.Length > 0) detail = detail with { Code = prefixCodes[0] };
        if (detail.InnerCode.Length == 0 && prefixCodes.Length > 1) detail = detail with { InnerCode = prefixCodes[1] };
        return detail with { FromBusinessCentral = detail.FromBusinessCentral || fromBc };
    }

    /// <summary>The text with the run's own opening taken off, leaving what Business Central sent.</summary>
    public static string WithoutRunPrefix(string text)
    {
        var prefix = RunPrefix.Match(text);
        return prefix.Success ? text[prefix.Length..] : text;
    }

    /// <summary>
    /// The log line's text for an install Business Central reported as failed: the run's
    /// own opening with the codes, then Business Central's text exactly as it came, on one
    /// line. <see cref="Parse"/> reads it back whole, codes included, even when Business
    /// Central's text carried no JSON.
    /// </summary>
    public static string ForLog(BcFailureDetail detail, string? raw)
    {
        var codes = new[] { detail.Code, detail.InnerCode }.Where(c => c.Length > 0).ToList();
        var opening = codes.Count > 0
            ? $"Business Central reported the install as failed ({string.Join(" / ", codes)})."
            : "Business Central reported the install as failed.";
        var text = string.IsNullOrWhiteSpace(raw)
            ? ""
            : string.Join(" ", raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return text.Length == 0 ? opening : $"{opening} {text}";
    }

    private static bool TryParseJson(string json, out BcFailureDetail detail)
    {
        detail = BcFailureDetail.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // Some responses nest the whole thing one level down, as { "error": { ... } }.
            if (!root.TryGetProperty("code", out _)
                && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                root = error;
            }

            var code = Str(root, "code");
            var message = Str(root, "message");
            var innerCode = "";
            var innerMessage = "";
            if (root.TryGetProperty("innerError", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                innerCode = Str(inner, "code");
                innerMessage = Str(inner, "message");
            }
            if (string.Equals(innerMessage.Trim(), message.Trim(), StringComparison.Ordinal)) innerMessage = "";
            if (code.Length == 0 && message.Length == 0 && innerCode.Length == 0) return false;

            detail = new BcFailureDetail(code, innerCode, message.Trim(), innerMessage.Trim(), true, false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Unescape(string jsonString)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + jsonString + "\"") ?? jsonString;
        }
        catch (JsonException)
        {
            // Cut in the middle of an escape ("\u00"): undo the common ones by hand.
            return jsonString.Replace("\\\"", "\"").Replace("\\\\", "\\").TrimEnd('\\');
        }
    }

    // ── Codes to words ────────────────────────────────────────────────────────

    /// <summary>
    /// The short form for a collapsed row, without the app: "Business Central refused a
    /// schema change". The code is shown beside it as a tag, so it is not repeated here.
    /// </summary>
    public static string Summary(string? code) => code switch
    {
        ExtensionChangeFailed => "Business Central refused a schema change",
        { Length: > 0 } => "Business Central refused the install",
        _ => "Business Central reported the install as failed",
    };

    /// <summary>
    /// The one line for a failed deployment, naming the app: "Business Central refused a
    /// schema change while installing CRONUS Core 28.2.17.126." Stored as the delivery's
    /// failure message and opens "What happened" on the page.
    /// </summary>
    public static string WhatHappened(string? code, string appLabel) => code switch
    {
        ExtensionChangeFailed => $"Business Central refused a schema change while installing {appLabel}.",
        { Length: > 0 } => $"Business Central refused the install of {appLabel} ({code}).",
        _ => $"Business Central reported the install of {appLabel} as failed.",
    };

    /// <summary>
    /// What the code means and, for the codes we know, what to do about it. Stored with
    /// the failed app, ahead of Business Central's own message.
    /// </summary>
    public static string Sentence(string? code) => code switch
    {
        ExtensionChangeFailed => "Business Central refused a schema change (a renamed or removed table or field). "
            + "Deploy again with Force sync to push it through, or keep the old names.",
        { Length: > 0 } => $"Business Central refused the install ({code}).",
        _ => "Business Central reported the install as failed.",
    };

    /// <summary>
    /// The failed app's stored message: <see cref="Sentence"/>, the codes it does not
    /// already name, then Business Central's message as given - so a reader of the app
    /// row (or an agent reading the history) gets all three without the wrapper or the JSON.
    /// </summary>
    public static string AppMessage(BcFailureDetail detail)
    {
        var text = Sentence(detail.Code);
        var codes = new[] { detail.Code, detail.InnerCode }
            .Where(c => c.Length > 0 && !text.Contains($"({c})", StringComparison.Ordinal))
            .ToList();
        if (codes.Count > 0) text += $" Error code: {string.Join(" / ", codes)}.";
        var said = string.Join(" ", new[] { detail.Message, detail.InnerMessage }.Where(s => s.Length > 0));
        return said.Length == 0 ? text : $"{text} Business Central's message: {said}";
    }

    /// <summary>
    /// The suggested next step for a failure Business Central reported, in the words of
    /// the delivery row sheet. <paramref name="forceSync"/> says the deployment already used
    /// Force sync, so suggesting it again would be no help.
    /// </summary>
    public static string NextStep(string? code, string environmentName, bool forceSync) => code switch
    {
        ExtensionChangeFailed when forceSync =>
            "This deployment already used Force sync, and Business Central still refused the change. "
            + "Its message above says what it refused; change the app and build again.",
        ExtensionChangeFailed =>
            "If the new version removes tables or fields the customer no longer needs, deploy this build again with Force sync. "
            + $"That deletes those columns and their data in \"{environmentName}\". Otherwise keep them in the app, mark them obsolete, and build again.",
        _ => "Business Central's message above says what it refused. If it does not say what to change, copy the response below for support.",
    };
}
