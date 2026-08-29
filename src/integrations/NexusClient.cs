using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The default <see cref="INexusClient"/>. A thin wrapper over the Nexus v1 REST
/// API via <see cref="HttpClient"/>. Auth + app-identification headers are
/// applied per-request by the configured <see cref="INexusAuthMessageFactory"/>;
/// the rate-limit headers on every response are parsed into
/// <see cref="NexusRateLimits"/> + carried on the returned
/// <see cref="Response{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>HttpClient</c> is supplied by <c>IHttpClientFactory</c> (typed-client
/// pattern); the API base URL is the typed client's <c>BaseAddress</c>.</para>
/// <para>
/// <b>Auth.</b> Per-request auth is owned by the auth factory (selected live by
/// <see cref="NexusConfig.AuthMethod"/>); this client does not know which auth
/// method is in use.</para>
/// <para>
/// <b>401 handling.</b> On a 401, this client asks the auth factory to refresh
/// (OAuth) or give up (API key, None). On a successful refresh, the request is
/// retried once with the new credentials. The retry is bounded to one: a second
/// 401 surfaces as <see cref="NexusApiException"/> (avoids an infinite loop on a
/// persistently-invalid token).</para>
/// <para>
/// Registered as a transient (the <c>AddHttpClient&lt;T,TImpl&gt;</c> default);
/// holds no per-call state.</para>
/// </remarks>
internal sealed class NexusClient : INexusClient
{
    private readonly HttpClient _http;
    private readonly INexusAuthMessageFactory _auth;
    private readonly ILogger<NexusClient> _logger;

    public NexusClient(
        HttpClient http,
        INexusAuthMessageFactory auth,
        ILogger<NexusClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
    {
        var (response, _) = await SendAsync<ValidateInfo>(
            HttpMethod.Get,
            RelativeUri("v1/users/validate.json"),
            ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri(
            $"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json");
        var (response, _) = await SendArrayAsync<DownloadLink>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        string nxmKey,
        long expiresEpoch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(nxmKey);
        var uri = RelativeUri(
            $"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json"
            + $"?key={Uri.EscapeDataString(nxmKey)}&expires={expiresEpoch}");
        var (response, _) = await SendArrayAsync<DownloadLink>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<ModInfo>> GetModInfoAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri($"v1/games/{gameDomain}/mods/{modId}.json");
        var (response, _) = await SendAsync<ModInfo>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<ModFile[]>> ListModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri($"v1/games/{gameDomain}/mods/{modId}/files.json");

        // files.json wraps its array in {"files":[...]}; unwrap before returning.
        var (wrapped, _) = await SendAsync<ModFilesResponse>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        var files = wrapped.Data.Files ?? Array.Empty<ModFile>();
        return new Response<ModFile[]>(files, wrapped.RateLimits);
    }

    /// <inheritdoc />
    public async Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(
        int gameId,
        IReadOnlyList<int> modIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        // Compute UIDs: uid = game_id * 2^32 + mod_id. The GraphQL variable
        // type is [ID!]! and the ID scalar is serialized as a string, so the
        // UIDs are stringified for the variables object.
        var uids = modIds
            .Select(id => ((long)gameId * 4294967296L + id).ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var body = new GraphQlRequest
        {
            Query = ModsByUidQuery,
            Variables = new GraphQlVariables { Uids = uids },
        };
        var json = JsonSerializer.Serialize(body);

        var uri = RelativeUri("v2/graphql");

        // A content factory (not a single HttpContent instance) so the 401-retry
        // path in SendRawAsync can create a fresh, unreadable body for the retry
        // (HttpContent is single-use for sending).
        Func<HttpContent> contentFactory = () =>
        {
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        };

        using var response = await SendRawAsync(
            HttpMethod.Post, uri, ct, isRetry: false, contentFactory).ConfigureAwait(false);
        var payload = await ReadAsync<GraphQlResponse<ModsByUidData>>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);

        // A 200 OK can still carry GraphQL-level errors in the body. Surface
        // them as a NexusApiException so the caller's best-effort catch handles
        // them uniformly (the update check logs + returns an empty result).
        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message));
            _logger.LogError("Nexus GraphQL response carried errors: {Message}", message);
            throw new NexusApiException((int)response.StatusCode, message);
        }

        var nodes = payload.Data?.ModsByUid?.Nodes ?? Array.Empty<ModUpdateStatus>();
        return new Response<ModUpdateStatus[]>(nodes, limits);
    }

    /// <summary>
    /// The <c>modsByUid</c> GraphQL query. Requests the update status fields for
    /// a batch of mods by UID. Whitespace is insignificant in GraphQL, so the
    /// query is kept on one line.
    /// </summary>
    private const string ModsByUidQuery =
        "query($uids: [ID!]!) { modsByUid(uids: $uids) { nodes { uid name version updatedAt viewerUpdateAvailable viewerDownloaded } totalCount } }";

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>Anonymous routing:</b> this is the one client call that must
    /// NOT carry credentials. <see cref="SendRawAsync"/> hardwires the auth
    /// factory (its gate + headers + 401-refresh), so the search sends
    /// directly through the underlying <see cref="HttpClient"/> with only the
    /// app-identification headers applied (the same header set the factories
    /// apply minus the credential): a fresh request, no auth gate, no retry.
    /// Anonymous is not a degraded mode here; it is how the endpoint was
    /// verified to work.</para>
    /// <para><b>Query shape:</b> the Nexus website's own search shape: one
    /// <c>mods</c> query, the phrase as the <c>name</c> WILDCARD value with
    /// no literal asterisks, filtered by <c>gameDomainName</c>, best-match-first
    /// (<c>relevance DESC</c>; a newest-first cap can omit the exact title
    /// from a small page entirely), blocked content excluded. Nexus's
    /// wildcard index owns the matching semantics.</para>
    /// </remarks>
    public async Task<Response<NexusSearchResult[]>> SearchModsAsync(
        string gameDomain,
        string terms,
        int count,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(terms);
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        // Only the Darktide domain resolves (the app is Darktide-only; an
        // explicit check keeps the failure loud instead of silently searching
        // the wrong game). The canonical constant is embedded after the
        // case-insensitive validation, so the filter never carries arbitrary
        // caller casing (the captured website request's exact value).
        if (!string.Equals(gameDomain, NexusGameIdentity.DarktideDomain, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown game domain '{gameDomain}'; only Darktide is searchable.", nameof(gameDomain));
        }

        var domain = EscapeGraphQlString(NexusGameIdentity.DarktideDomain);
        var phrase = EscapeGraphQlString(terms);
        var capped = count.ToString(CultureInfo.InvariantCulture);
        var query =
            "query { mods(filter: { gameDomainName: [{op: EQUALS, value: \"" + domain + "\"}], " +
            "name: [{op: WILDCARD, value: \"" + phrase + "\"}] }, " +
            "viewUserBlockedContent: false, " +
            "sort: { relevance: { direction: DESC } }, count: " + capped +
            ") { nodes { modId name uid } } }";
        var (nodes, limits) = await SendAnonymousGraphQlSearchAsync(query, ct).ConfigureAwait(false);

        return new Response<NexusSearchResult[]>(
            nodes.Select(n => new NexusSearchResult(n.ModId, n.Name, n.Uid)).ToArray(),
            limits);
    }

    /// <summary>
    /// The one anonymous GraphQL search request: builds the request with the
    /// app-identification headers only (no auth factory involvement; the
    /// search endpoint is anonymous), sends it, surfaces GraphQL-level errors
    /// in a 200 OK body as <see cref="NexusApiException"/> (the
    /// <see cref="CheckUpdatesGraphQlAsync"/> precedent), and returns the
    /// nodes + whatever rate-limit headers were present.
    /// </summary>
    private async Task<(ModsSearchNode[] Nodes, NexusRateLimits Limits)> SendAnonymousGraphQlSearchAsync(
        string query,
        CancellationToken ct)
    {
        var uri = RelativeUri("v2/graphql");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        ApiKeyMessageFactory.ApplyAppHeaders(request);
        var content = new StringContent(
            JsonSerializer.Serialize(new GraphQlRequest { Query = query }), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var payload = await ReadAsync<GraphQlResponse<ModsSearchData>>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);

        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message));
            _logger.LogError("Nexus GraphQL search response carried errors: {Message}", message);
            throw new NexusApiException((int)response.StatusCode, message);
        }

        return (payload.Data?.Mods?.Nodes ?? Array.Empty<ModsSearchNode>(), limits);
    }

    /// <summary>
    /// Escapes a value for embedding in a GraphQL string literal: every
    /// single backslash is doubled first, then every double quote is escaped
    /// (a quote following a backslash therefore escapes both correctly).
    /// </summary>
    private static string EscapeGraphQlString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>Anonymous routing:</b> the search's exact posture (the
    /// endpoint is anonymous): the request is sent directly through the
    /// underlying <see cref="HttpClient"/> with only the
    /// app-identification headers, no auth factory involvement, no retry.</para>
    /// <para><b>Missing identity:</b> <c>modsByUid</c> simply omits a UID that
    /// resolves to nothing (a removed or nonexistent mod), so an empty node
    /// list is the documented not-found answer, not an error.</para>
    /// </remarks>
    public async Task<Response<NexusSearchResult?>> GetModByIdAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        if (modId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modId), "The mod id must be positive.");
        }

        if (!string.Equals(gameDomain, NexusGameIdentity.DarktideDomain, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown game domain '{gameDomain}'; only Darktide is lookup-able.", nameof(gameDomain));
        }

        var uid = ((long)NexusGameIdentity.DarktideGameId * 4294967296L + modId)
            .ToString(CultureInfo.InvariantCulture);
        var query =
            "query($uids: [ID!]!) { modsByUid(uids: $uids) { nodes { uid name } } }";
        var body = JsonSerializer.Serialize(new GraphQlRequest
        {
            Query = query,
            Variables = new GraphQlVariables { Uids = new[] { uid } },
        });

        var uri = RelativeUri("v2/graphql");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        ApiKeyMessageFactory.ApplyAppHeaders(request);
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var payload = await ReadAsync<GraphQlResponse<ModsByUidData>>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);

        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message));
            _logger.LogError("Nexus GraphQL lookup response carried errors: {Message}", message);
            throw new NexusApiException((int)response.StatusCode, message);
        }

        // An empty node list is the not-found answer (a UID that resolves to
        // nothing is omitted, not errored); the found node's canonical name is
        // the identity the caller needed beyond the id it already held.
        var node = payload.Data?.ModsByUid?.Nodes?.FirstOrDefault();
        var result = node is null
            ? null
            : new NexusSearchResult(modId, node.Name, node.Uid.ToString(CultureInfo.InvariantCulture));
        return new Response<NexusSearchResult?>(result, limits);
    }

    // ---- core send (with 401-reactive refresh + retry once) ----------------

    /// <summary>
    /// Sends a request that deserializes to a single object. Applies auth via
    /// the factory; on 401 asks the factory to refresh + retries once. Parses
    /// the rate-limit headers onto the returned <see cref="Response{T}"/>.
    /// </summary>
    private async Task<(Response<T> Response, bool WasRetry)> SendAsync<T>(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry = false)
    {
        using var response = await SendRawAsync(method, uri, ct, isRetry).ConfigureAwait(false);
        var payload = await ReadAsync<T>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);
        return (new Response<T>(payload, limits), isRetry);
    }

    /// <summary>
    /// Sends a request that deserializes to a top-level JSON array (the shape of
    /// <c>download_link.json</c>). Mirrors
    /// <see cref="SendAsync{T}"/> for auth + retry.
    /// </summary>
    private async Task<(Response<T[]> Response, bool WasRetry)> SendArrayAsync<T>(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry = false)
    {
        using var response = await SendRawAsync(method, uri, ct, isRetry).ConfigureAwait(false);
        var payload = await ReadArrayAsync<T>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);
        return (new Response<T[]>(payload, limits), isRetry);
    }

    /// <summary>
    /// Sends the request via the underlying <c>HttpClient</c>. On 401, asks the
    /// auth factory to refresh; on success, retries once with a fresh request
    /// (built by the factory with the now-current credentials). Disposes the
    /// 401 response + the original request before retrying.
    /// </summary>
    /// <remarks>
    /// <b>Auth gate.</b> If <see cref="INexusAuthMessageFactory.IsAuthenticatedAsync"/>
    /// returns <c>false</c> before the first send, this throws
    /// <see cref="NexusNotAuthenticatedException"/> (callers gate on it; this is
    /// the defensive backstop). The retry path skips the gate (the refresh just
    /// produced fresh credentials; if the factory reports not-authenticated
    /// again, the 401 will resurface).
    /// <para>
    /// <b>Request body.</b> <paramref name="contentFactory"/> is a factory (not
    /// a single <see cref="HttpContent"/>) because <see cref="HttpContent"/> is
    /// single-use for sending: the 401-retry path needs a fresh body. Null (the
    /// default) sends a bodyless request (GET / DELETE / etc.).</para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry,
        Func<HttpContent>? contentFactory = null)
    {
        if (!isRetry && !await _auth.IsAuthenticatedAsync(ct).ConfigureAwait(false))
        {
            throw new NexusNotAuthenticatedException();
        }

        // Build + send the request. The factory owns auth + app headers.
        var request = await _auth.CreateAsync(method, uri, ct).ConfigureAwait(false);
        if (contentFactory is not null)
        {
            request.Content = contentFactory();
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
        request.Dispose();

        // Retry once on 401 if the factory reports a successful refresh.
        if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
        {
            response.Dispose();

            if (await _auth.OnUnauthorizedAsync(ct).ConfigureAwait(false))
            {
                // Refresh succeeded: recurse once with isRetry=true. A second 401
                // surfaces as NexusApiException (the recursive call no longer
                // hits this branch). The content factory is forwarded so the
                // retry gets a fresh request body.
                return await SendRawAsync(method, uri, ct, isRetry: true, contentFactory)
                    .ConfigureAwait(false);
            }

            // Refresh not possible / failed. The original 401 propagates as a
            // NexusApiException.
            throw new NexusApiException(
                (int)HttpStatusCode.Unauthorized,
                "Nexus auth rejected (HTTP 401). Re-login required.");
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Throws <see cref="NexusRateLimitException"/> / <see cref="NexusApiException"/>
    /// for a failed response. Returns silently on success. Detection:
    /// HTTP 429, or HTTP 403 with one of the <c>x-rl-*-remaining</c> headers
    /// reporting zero, is the rate-limit signal.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var limits = NexusRateLimitsParser.Parse(response);
        if (IsRateLimited(response, limits))
        {
            _logger.LogWarning(
                "Nexus API rate limit exhausted (status {Status}; daily remaining {Daily}; hourly remaining {Hourly}).",
                (int)response.StatusCode,
                limits.DailyRemaining,
                limits.HourlyRemaining);
            throw new NexusRateLimitException((int)response.StatusCode, limits);
        }

        var message = await ReadErrorMessageAsync(response, ct).ConfigureAwait(false);
        _logger.LogError("Nexus API request failed: status {Status}, message {Message}.",
            (int)response.StatusCode, message);
        throw new NexusApiException((int)response.StatusCode, message);
    }

    /// <summary>
    /// The rate-limit signal: HTTP 429 always; HTTP 403 only when the limit
    /// headers are present (<c>x-rl-*-limit &gt; 0</c>) AND at least one
    /// remaining counter is zero. A 403 with no rate-limit headers, or with a
    /// non-zero remaining, is a permissions error, not rate-limiting.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response, NexusRateLimits limits)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // 403: rate-limit only when limit headers are present and a remaining
        // counter is exhausted (two-condition rule).
        var hasLimitHeaders = limits.DailyLimit > 0 || limits.HourlyLimit > 0;
        if (!hasLimitHeaders)
        {
            return false;
        }

        return limits.DailyRemaining <= 0 || limits.HourlyRemaining <= 0;
    }

    private void LogRateLimits(Uri uri, NexusRateLimits limits)
    {
        // Match NMA's pattern: log the remaining counters on every successful
        // call so the operator can watch the rate window drain.
        if (limits.DailyLimit > 0 || limits.HourlyLimit > 0)
        {
            _logger.LogInformation(
                "Nexus API call to {Uri} ok; remaining: daily={Daily}, hourly={Hourly}.",
                uri,
                limits.DailyRemaining,
                limits.HourlyRemaining);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new NexusApiException(
                (int)response.StatusCode,
                $"Nexus API returned an empty {typeof(T).Name} response.");
    }

    private static async Task<T[]> ReadArrayAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var dto = await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        return dto?.ToArray() ?? Array.Empty<T>();
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Nexus errors are JSON with a "message" field. Fall back to the reason
        // phrase for non-JSON bodies so the exception always carries something.
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? FallbackReason(response);
            }
        }
        catch
        {
            // Non-JSON or unreadable body.
        }

        return FallbackReason(response);
    }

    private static string FallbackReason(HttpResponseMessage response) =>
        response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";

    // ---- URI helpers -------------------------------------------------------

    /// <summary>
    /// Builds a relative URI against the typed client's <c>BaseAddress</c> (the
    /// API base URL, normalized in <see cref="ServiceCollectionExtensions.AddIntegrations"/>
    /// to end with a trailing slash so relative URIs resolve predictably).
    /// </summary>
    private static Uri RelativeUri(string relative) => new(relative, UriKind.Relative);
}

/// <summary>
/// Parses the Nexus rate-limit headers (<c>x-rl-*</c>) from an HTTP response
/// into a <see cref="NexusRateLimits"/>. Missing or unparseable headers yield
/// <c>0</c> / <c>null</c> for that field (never throws). Header names + parsing
/// mirror NMA's <c>ResponseMetadata.FromHttpHeaders</c>.
/// </summary>
internal static class NexusRateLimitsParser
{
    /// <summary>The all-zero / no-header reading (the fields' absent
    /// values).</summary>
    public static NexusRateLimits Empty { get; } = new(0, 0, null, 0, 0, null);

    public static NexusRateLimits Parse(HttpResponseMessage response)
    {
        ParseInt(response, "x-rl-daily-limit", out var dailyLimit);
        ParseInt(response, "x-rl-daily-remaining", out var dailyRemaining);
        ParseDate(response, "x-rl-daily-reset", out var dailyReset);
        ParseInt(response, "x-rl-hourly-limit", out var hourlyLimit);
        ParseInt(response, "x-rl-hourly-remaining", out var hourlyRemaining);
        ParseDate(response, "x-rl-hourly-reset", out var hourlyReset);

        return new NexusRateLimits(
            dailyLimit,
            dailyRemaining,
            dailyReset,
            hourlyLimit,
            hourlyRemaining,
            hourlyReset);
    }

    private static void ParseInt(HttpResponseMessage response, string header, out int value)
    {
        value = 0;
        if (response.Headers.TryGetValues(header, out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var parsed))
                {
                    value = parsed;
                    return;
                }
            }
        }
    }

    private static void ParseDate(HttpResponseMessage response, string header, out DateTimeOffset? value)
    {
        value = null;
        if (response.Headers.TryGetValues(header, out var values))
        {
            foreach (var v in values)
            {
                if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    value = parsed;
                    return;
                }
            }
        }
    }
}
