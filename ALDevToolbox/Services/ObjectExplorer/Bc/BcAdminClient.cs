using System.Net;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <inheritdoc cref="IBcAdminClient"/>
public sealed class BcAdminClient : IBcAdminClient
{
    /// <summary>Stands in for the environment name in logs for the tenant-wide calls.</summary>
    private const string TenantScope = "the tenant";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcAdminClient> _logger;

    public BcAdminClient(IHttpClientFactory httpFactory, ILogger<BcAdminClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminEnvironmentsUrl);
        request.UseBearer(accessToken);

        // A 404 on the tenant-wide list is not "this tenant has no environments" — that
        // answer comes back as an empty envelope. It means the route or the token's tenant
        // is wrong, and reporting it as an empty fleet would hide a broken connection.
        var body = await SendAsync(request, "reading the environments", TenantScope, NotFoundPolicy.Error, ct)
            .ConfigureAwait(false);
        return ParseEnvironments(body!);
    }

    public async Task<BcEnvironment?> GetEnvironmentAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.AdminEnvironmentUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // A hard-deleted environment is a 404, which is an answer, not a fault:
        // the caller turns "no longer there" into its own message.
        var body = await SendAsync(request, "reading the environment", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? null : ParseEnvironment(body);
    }

    public async Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentUpdatesUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // Same reading of a 404 as GetEnvironmentAsync: the environment is gone, so it has
        // no updates on offer. Both readers — the nightly mirror and the environment panel —
        // want that as "nothing scheduled" rather than as an error they can't act on.
        var body = await SendAsync(
                request, "reading the Business Central updates", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? Array.Empty<BcEnvironmentUpdate>() : ParseEnvironmentUpdates(body);
    }

    public async Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminTimezonesUrl);
        request.UseBearer(accessToken);
        var body = await SendSettingsAsync(request, "reading the time zones", TenantScope, ct).ConfigureAwait(false);
        return ParseTimezones(body);
    }

    public async Task SetAppUpdateCadenceAsync(
        string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cadence))
        {
            throw new ArgumentException("Choose how often Marketplace apps should update.", nameof(cadence));
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["value"] = cadence.Trim() });
        using var request = new HttpRequestMessage(
            HttpMethod.Put, BcConstants.EnvironmentAppCadenceUrl(applicationFamily, environmentName))
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "setting the app update cadence", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Set the Marketplace app update cadence on {Environment} to {Cadence}.", environmentName, cadence);
    }

    public async Task<bool?> GetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BcConstants.EnvironmentM365AccessUrl(applicationFamily, environmentName));
        request.UseBearer(accessToken);

        var body = await SendSettingsAsync(request, "reading Microsoft 365 licence access", environmentName, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Flag(doc.RootElement, "enabled") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
    {
        // The documented body sends the boolean as a string.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["enabled"] = enabled ? "true" : "false",
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BcConstants.EnvironmentM365AccessUrl(applicationFamily, environmentName))
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "changing Microsoft 365 licence access", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation("Set Microsoft 365 licence access on {Environment} to {Enabled}.", environmentName, enabled);
    }

    public async Task SelectTargetVersionAsync(
        string accessToken, string? applicationFamily, string environmentName,
        string targetVersion, string? targetVersionType,
        DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw new ArgumentException("Choose the version to update to.", nameof(targetVersion));
        }

        var payload = new Dictionary<string, object> { ["selected"] = true };
        if (!string.IsNullOrWhiteSpace(targetVersionType)) payload["targetVersionType"] = targetVersionType.Trim();
        // ISO-8601 in UTC, the form the documented body shows and the one the updates read
        // hands back. Only sent when the caller is actually moving the date: a PATCH that
        // omits it leaves the customer's existing slot alone.
        if (selectedDateTime is { } when)
        {
            payload["selectedDateTime"] = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        // Sent as a real JSON boolean, unlike the string "true"/"false" the Microsoft 365
        // licence endpoint documents. This body already carries `selected` as a boolean and
        // the same endpoint reads both flags back as booleans, so the two flags in one body
        // stay the same shape. If Business Central ever refuses it, the string form is the
        // first thing to try.
        if (ignoreUpdateWindow is { } ignore) payload["ignoreUpdateWindow"] = ignore;

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, BcConstants.EnvironmentUpdateUrl(applicationFamily, environmentName, targetVersion))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        await SendSettingsAsync(request, "choosing the next update", environmentName, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Selected Business Central {Version} as the next update for {Environment} (date {SelectedDateTime}, ignoreUpdateWindow {IgnoreUpdateWindow}).",
            targetVersion, environmentName, selectedDateTime, ignoreUpdateWindow);
    }

    /// <summary>
    /// Sends one environment-settings request and maps a refusal to a message keyed on the
    /// error <em>code</em>. Returns the body so a reader can parse it.
    /// </summary>
    private async Task<string> SendSettingsAsync(
        HttpRequestMessage request, string action, string environmentName, CancellationToken ct) =>
        // Never null: a 404 is a refusal here, so the send has already thrown.
        (await SendAsync(request, action, environmentName, NotFoundPolicy.Error, ct,
            (status, body) => DescribeSettingsFailure(status, body, action)).ConfigureAwait(false))!;

    /// <summary>What a <c>404</c> means for one Admin Center call.</summary>
    private enum NotFoundPolicy
    {
        /// <summary>A 404 is a fault like any other status and becomes a <see cref="BcApiException"/>.</summary>
        Error,

        /// <summary>
        /// A 404 means Business Central no longer has the thing being asked about, which is an
        /// answer rather than a fault: the send returns null and the caller turns that into its
        /// own empty result.
        /// </summary>
        Absent,
    }

    /// <summary>
    /// Sends one Admin Center request and maps transport faults and non-success statuses to
    /// <see cref="BcApiException"/> with a short, secret-free detail.
    /// <paramref name="action"/> is a gerund for the message ("reading the environments").
    /// <paramref name="environmentName"/> names the environment in the log, or
    /// <see cref="TenantScope"/> for a call that isn't about one environment.
    /// <paramref name="describeFailure"/> replaces the generic refusal wording for the calls
    /// whose error codes are worth translating into something a consultant can act on.
    /// </summary>
    /// <returns>
    /// The response body, or <c>null</c> for a 404 the caller asked to read as absent.
    /// </returns>
    private async Task<string?> SendAsync(
        HttpRequestMessage request, string action, string environmentName, NotFoundPolicy notFound,
        CancellationToken ct, Func<HttpStatusCode, string, string>? describeFailure = null)
    {
        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BcApiException(null, $"Couldn't reach the Business Central Admin Center API while {action}.", ex);
        }

        using (response)
        {
            if (notFound == NotFoundPolicy.Absent && response.StatusCode == HttpStatusCode.NotFound) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return body;

            // Read the error envelope, don't just log the status. Microsoft returns a
            // stable `code` and a diagnostic `message` here, and without them a 401 is
            // indistinguishable from a 403 in the logs — which is how "the app isn't on
            // the authorized-apps list" got misread as "GDAP is missing" for a customer
            // who had no GDAP relationship in the first place.
            var detail = ExtractError(body);
            _logger.LogWarning("BC admin call ({Action}) for {Environment} returned {Status}. {Detail}",
                action, environmentName, response.StatusCode, detail.Length > 0 ? detail : "(no error body)");
            throw new BcApiException(response.StatusCode, describeFailure is null
                ? $"The Admin Center API returned {(int)response.StatusCode}. {detail}".TrimEnd()
                : describeFailure(response.StatusCode, body));
        }
    }

    /// <summary>
    /// Turns a refused settings write into something a consultant can act on, keyed on the
    /// error code rather than Microsoft's prose.
    /// </summary>
    internal static string DescribeSettingsFailure(HttpStatusCode status, string body, string action)
    {
        var code = ErrorCode(body);
        var detail = ExtractError(body);
        return code switch
        {
            "environmentNotFound" =>
                "Business Central no longer has this environment. Refresh the environments and try again.",
            "applicationTypeDoesNotExist" =>
                "Business Central didn't recognise this environment's application family. Refresh the environments and try again.",
            "cannotSetAppInsightsKey" or "EnvironmentNotActive" =>
                "The environment has to be active before this can change. Wait for Business Central to finish what it's doing, then try again.",
            _ => $"Business Central refused the change while {action}. {detail}".TrimEnd(),
        };
    }

    /// <summary>Parses the tenant-wide time-zone list.</summary>
    internal static IReadOnlyList<BcTimeZone> ParseTimezones(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcTimeZone>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned a time-zone list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcTimeZone>();
            }

            var result = new List<BcTimeZone>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new BcTimeZone(id, Text(item, "displayName") ?? id, Text(item, "currentUtcOffset") ?? string.Empty));
            }
            return result;
        }
    }

    public async Task<BcUpdateSettings?> GetUpdateSettingsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
    {
        var url = BcConstants.EnvironmentUpdateSettingsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        // An environment that has since been removed is an answer, not a fault - the
        // same treatment GetEnvironmentAsync gives a 404.
        var body = await SendAsync(request, "reading the update window", environmentName, NotFoundPolicy.Absent, ct)
            .ConfigureAwait(false);
        return body is null ? null : ParseUpdateSettings(body);
    }

    public async Task SetUpdateSettingsAsync(
        string accessToken, string? applicationFamily, string environmentName,
        TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(windowsTimeZoneId))
        {
            throw new ArgumentException(
                "Business Central needs a Windows time-zone id for an update window.", nameof(windowsTimeZoneId));
        }

        // The wall-time + timezone parameter set, never the UTC one: the UTC set resets
        // the time zone the admin center displays to the country default.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["preferredStartTime"] = start.ToString("HH\\:mm"),
            ["preferredEndTime"] = end.ToString("HH\\:mm"),
            ["timeZoneId"] = windowsTimeZoneId.Trim(),
        });

        var url = BcConstants.EnvironmentUpdateSettingsUrl(applicationFamily, environmentName);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);

        // A 404 on a write is a refusal, not an absence: the environment has to exist for
        // the window to be worth setting, and DescribeUpdateSettingsFailure turns the
        // environmentNotFound code into "refresh the environments and try again".
        await SendAsync(request, "setting the update window", environmentName, NotFoundPolicy.Error, ct,
            DescribeUpdateSettingsFailure).ConfigureAwait(false);

        _logger.LogInformation(
            "Set the Business Central update window for {Environment} to {Start}-{End} ({TimeZone}).",
            environmentName, start, end, windowsTimeZoneId);
    }

    /// <summary>
    /// Turns a rejected update-window write into something a consultant can act on. Keyed
    /// on the error <em>code</em>, because the accompanying message is Microsoft's prose
    /// and not guaranteed stable.
    /// </summary>
    internal static string DescribeUpdateSettingsFailure(HttpStatusCode status, string body)
    {
        var code = ErrorCode(body);
        var detail = ExtractError(body);
        return code switch
        {
            "ScheduledUpgradeConstraintViolation" =>
                "Business Central refused this window because it clashes with the update already scheduled for this environment - "
                + "the update would fall outside the window, or it is due today and the window has passed. "
                + "Change the window, or move the update date in the admin center.",
            "invalidRange" =>
                "Business Central refused this window as too small. Give the update more room between the start and end times.",
            "environmentNotFound" =>
                "Business Central no longer has this environment. Refresh the environments and try again.",
            _ => $"The Admin Center API returned {(int)status}. {detail}".TrimEnd(),
        };
    }

    /// <summary>Reads the <c>code</c> from an Admin Center error envelope, flat or nested under <c>error</c>.</summary>
    private static string ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
            return root.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Parses the environment <c>updates</c> envelope. An unreleased version carries only
    /// <c>expectedAvailability</c>; a released one carries <c>scheduleDetails</c>. Both
    /// shapes appear in the same list, so every nested field is optional.
    /// </summary>
    internal static IReadOnlyList<BcEnvironmentUpdate> ParseEnvironmentUpdates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BcEnvironmentUpdate>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned an update list we couldn't read.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BcEnvironmentUpdate>();
            }

            var result = new List<BcEnvironmentUpdate>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var version = Text(item, "targetVersion");
                if (string.IsNullOrWhiteSpace(version)) continue;

                // Both blocks are optional and appear on different kinds of row, so
                // neither may be read unless it is actually an object: the shared Text
                // helper throws on an element that was never assigned.
                var hasSchedule = item.TryGetProperty("scheduleDetails", out var schedule)
                    && schedule.ValueKind == JsonValueKind.Object;
                var hasExpected = item.TryGetProperty("expectedAvailability", out var expected)
                    && expected.ValueKind == JsonValueKind.Object;

                result.Add(new BcEnvironmentUpdate(
                    TargetVersion: version,
                    Available: Flag(item, "available") ?? false,
                    Selected: Flag(item, "selected") ?? false,
                    UpdateStatus: Text(item, "updateStatus") ?? string.Empty,
                    TargetVersionType: Text(item, "targetVersionType") ?? string.Empty,
                    SelectedDateTime: hasSchedule ? Moment(schedule, "selectedDateTime") : null,
                    LatestSelectableDateTime: hasSchedule ? Moment(schedule, "latestSelectableDateTime") : null,
                    IgnoreUpdateWindow: hasSchedule && (Flag(schedule, "ignoreUpdateWindow") ?? false),
                    RolloutStatus: hasSchedule ? Text(schedule, "rolloutStatus") ?? string.Empty : string.Empty,
                    ExpectedMonth: hasExpected ? Number(expected, "month") : null,
                    ExpectedYear: hasExpected ? Number(expected, "year") : null));
            }
            return result;
        }
    }

    private static bool? Flag(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(v.GetString(), out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            }
            : null;

    private static DateTimeOffset? Moment(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    /// <summary>
    /// Parses <c>settings/upgrade</c>. The API answers a literal <c>null</c> body for an
    /// environment with no window, so that is a normal result rather than a parse failure.
    /// Only the wall-time trio is read: the UTC pair the response also carries names the
    /// next occurrence and moves, so it would go stale in the cache within a day.
    /// </summary>
    internal static BcUpdateSettings? ParseUpdateSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BcApiException(null, "Business Central returned an update window we couldn't read.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var start = ReadWallTime(root, "preferredStartTime");
            var end = ReadWallTime(root, "preferredEndTime");
            var tz = Text(root, "timeZoneId");
            if (start is null && end is null && string.IsNullOrWhiteSpace(tz)) return null;
            return new BcUpdateSettings(start, end, tz);
        }
    }

    /// <summary>Reads an <c>HH:mm</c> wall time; tolerates <c>HH:mm:ss</c> in case the API grows seconds.</summary>
    private static TimeOnly? ReadWallTime(JsonElement element, string property)
    {
        var raw = Text(element, property);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TimeOnly.TryParseExact(raw, "HH\\:mm", System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var exact)
            ? exact
            : TimeOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var loose) ? loose : null;
    }

    /// <summary>
    /// Pulls a short, secret-free summary out of an Admin Center error envelope
    /// (<c>{ "code": ..., "message": ... }</c>), falling back to the OData <c>error.message</c>
    /// shape the automation API uses. Empty when the body isn't JSON or carries neither.
    /// </summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            // The automation API nests the same two fields under "error".
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                root = nested;
            }

            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var detail = (code, message) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
                ({ Length: > 0 }, _) => code!,
                (_, { Length: > 0 }) => message!,
                _ => string.Empty,
            };
            return detail.Length > 300 ? detail[..300] : detail;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Parses the Admin Center <c>{ "value": [ ... ] }</c> environments envelope. Internal for the client test.</summary>
    internal static IReadOnlyList<BcEnvironment> ParseEnvironments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BcEnvironment>();
        }

        var result = new List<BcEnvironment>();
        foreach (var item in value.EnumerateArray())
        {
            if (ReadEnvironment(item) is { } env) result.Add(env);
        }
        return result;
    }

    /// <summary>
    /// Parses the by-name environment response. That endpoint returns the environment
    /// object directly, but tolerates the list envelope too (Microsoft has shipped both
    /// shapes on neighbouring routes). Internal for the client test.
    /// </summary>
    internal static BcEnvironment? ParseEnvironment(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ReadEnvironment(item) is { } fromList) return fromList;
            }
            return null;
        }
        return ReadEnvironment(root);
    }

    /// <summary>
    /// Reads one environment object. Every field beyond name/type is optional — a
    /// payload without <c>versionDetails</c>, or with it null, must not throw — and
    /// enum-ish strings are kept verbatim because Microsoft's casing varies per
    /// endpoint. Returns null for an entry with no usable name.
    /// </summary>
    private static BcEnvironment? ReadEnvironment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var name = Text(item, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new BcEnvironment(name, Text(item, "type") ?? string.Empty)
        {
            FriendlyName = Text(item, "friendlyName"),
            ApplicationFamily = Text(item, "applicationFamily"),
            Status = Text(item, "status"),
            CountryCode = Text(item, "countryCode"),
            AadTenantId = Guid.TryParse(Text(item, "aadTenantId"), out var tenant) ? tenant : null,
            WebClientLoginUrl = Text(item, "webClientLoginUrl"),
            LocationName = Text(item, "locationName"),
            GeoName = Text(item, "geoName"),
            RingName = Text(item, "ringName"),
            AppSourceAppsUpdateCadence = Text(item, "appSourceAppsUpdateCadence"),
            Version = ReadVersion(item),
            GracePeriodStartDate = Timestamp(item, "gracePeriodStartDate"),
            EnforcedUpdatePeriodStartDate = Timestamp(item, "enforcedUpdatePeriodStartDate"),
            SoftDeletedOn = Timestamp(item, "softDeletedOn"),
            HardDeletePendingOn = Timestamp(item, "hardDeletePendingOn"),
            DeleteReason = Text(item, "deleteReason"),
        };
    }

    /// <summary>The version lives under <c>versionDetails</c>, which can be absent or null.</summary>
    private static string? ReadVersion(JsonElement item)
    {
        if (!item.TryGetProperty("versionDetails", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return Text(details, "version") ?? Text(details, "applicationVersion");
    }

    private static string? Text(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTime? Timestamp(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.TryGetDateTime(out var when)
            ? DateTime.SpecifyKind(when.ToUniversalTime(), DateTimeKind.Utc)
            : null;
    }
}
