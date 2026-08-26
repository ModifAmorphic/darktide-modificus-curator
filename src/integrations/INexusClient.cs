namespace Modificus.Curator.Integrations;

/// <summary>
/// The Nexus Mods API client. Surface: auth validation (one method per auth
/// mode) + the v2 GraphQL update check, v1 REST download links, mod-page
/// metadata, and mod files. Auth is applied per-request by the configured
/// <see cref="INexusAuthMessageFactory"/>, and the parsed rate-limit headers are
/// carried on every response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth is per-request and explicit.</b> The auth factory selection reads
/// <c>NexusConfig.AuthMethod</c> live: <see cref="NexusAuthMethod.OAuth"/> uses
/// the OAuth bearer factory (with 401-reactive refresh);
/// <see cref="NexusAuthMethod.ApiKey"/> uses the apikey factory (static, no
/// refresh); <see cref="NexusAuthMethod.None"/> throws
/// <see cref="NexusNotAuthenticatedException"/>. There is <b>no fallback</b>:
/// the configured method is the single source of truth for which auth is
/// active.</para>
/// <para>
/// <b>v1 REST + v2 GraphQL.</b> The REST endpoints are the stable production
/// paths; the v3 openapi surfaces the mod endpoints as Experimental and is not
/// used.</para>
/// <para>
/// <b>Rate limits.</b> Every response carries the parsed <c>x-rl-*</c> headers
/// in its <see cref="Response{T}.RateLimits"/>.</para>
/// </remarks>
public interface INexusClient
{
    /// <summary>
    /// Validates the configured API key + returns the user's identity. Hits
    /// <c>GET /v1/users/validate.json</c>. Throws <see cref="NexusApiException"/>
    /// on a non-2xx; throws <see cref="NexusNotAuthenticatedException"/> when
    /// auth is <c>None</c> or not <c>ApiKey</c>.
    /// </summary>
    Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default);

    /// <summary>
    /// Premium-user download links for the given file. Hits
    /// <c>GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json</c>
    /// (premium-only endpoint; the response carries the CDN URLs).
    /// </summary>
    Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Free-user download links for the given file, keyed by the per-file token
    /// from the <c>nxm://</c> URL. Hits the same endpoint as the premium overload
    /// + <c>?key={nxmKey}&amp;expires={epoch}</c>. The response carries the CDN
    /// URLs.
    /// </summary>
    Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        string nxmKey,
        long expiresEpoch,
        CancellationToken ct = default);

    /// <summary>
    /// The mod-page metadata. Hits <c>GET /v1/games/{domain}/mods/{modId}.json</c>.
    /// Carries the canonical name + version.
    /// </summary>
    Task<Response<ModInfo>> GetModInfoAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default);

    /// <summary>
    /// The files attached to a mod. Hits
    /// <c>GET /v1/games/{domain}/mods/{modId}/files.json</c> and unwraps the
    /// <c>{"files":[...]}</c> envelope to the array.
    /// </summary>
    Task<Response<ModFile[]>> ListModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default);

    /// <summary>
    /// Queries the v2 GraphQL <c>modsByUid</c> endpoint for the update status of
    /// multiple mods in a single API call. Computes UIDs from the game id + mod
    /// id (<c>uid = game_id * 2^32 + mod_id</c>). Returns
    /// <see cref="ModUpdateStatus"/> for each mod, carrying the server-computed
    /// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> field (true if the mod
    /// has been updated since the user last downloaded it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batch query covers all requested mods in one call regardless of when
    /// they were last updated, and the server computes the update signal
    /// directly.</para>
    /// <para>
    /// Auth + app-identification headers are the same as v1 (applied per-request
    /// by the configured <see cref="INexusAuthMessageFactory"/>). Rate-limit
    /// headers are parsed onto the returned <see cref="Response{T}"/> the same
    /// way. Throws <see cref="NexusRateLimitException"/> on HTTP 429 / exhausted
    /// rate-limit headers; <see cref="NexusApiException"/> on other failures
    /// (including GraphQL-level errors in a 200 OK body).</para>
    /// </remarks>
    /// <param name="gameId">The Nexus game id (Darktide is 4943).</param>
    /// <param name="modIds">The Nexus mod ids to check.</param>
    Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(
        int gameId,
        IReadOnlyList<int> modIds,
        CancellationToken ct = default);

    /// <summary>
    /// Searches the game's mods by name through the Nexus v2 GraphQL
    /// <c>mods</c> query, run ANONYMOUSLY (no auth header; the endpoint sits
    /// behind Cloudflare, not the API key budget). The query runs twice and
    /// the results are unioned by mod id preserving search order (plain-name
    /// hits first): once with a surrounding-wildcard <c>name</c> filter and
    /// once with a bare <c>nameStemmed</c> filter (the stemmed index matches
    /// space-separated stemmed words, so a wildcard would break it).
    /// </summary>
    /// <remarks>
    /// <para>Anonymous responses carry no <c>x-rl-*</c> rate-limit headers;
    /// they are parsed onto the returned <see cref="Response{T}"/> anyway if
    /// ever present. No auth gate: the call works signed out, so it neither
    /// throws <see cref="NexusNotAuthenticatedException"/> nor consumes a
    /// refresh.</para>
    /// <para>Throws <see cref="NexusApiException"/> on a non-2xx or a
    /// GraphQL-level error in a 200 OK body (both query legs must succeed;
    /// the caller's failure posture is no-retry). Callers must stay serial +
    /// human-paced: the endpoint is Cloudflare-protected and bursts look like
    /// bot traffic.</para>
    /// </remarks>
    /// <param name="gameDomain">The game domain (resolved to the game id for
    /// the <c>gameId</c> filter; only the Darktide domain resolves).</param>
    /// <param name="terms">The already-normalized search terms (lowercase,
    /// word-separated; the caller owns the folder-name conversion).</param>
    /// <param name="count">The per-leg result cap.</param>
    Task<Response<NexusSearchResult[]>> SearchModsAsync(
        string gameDomain,
        string terms,
        int count,
        CancellationToken ct = default);
}
