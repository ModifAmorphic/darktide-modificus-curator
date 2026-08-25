# Integrations (`Modificus.Curator.Integrations`) -- reference

> The Nexus Mods v1 client + OAuth/API-key auth machinery, a download + extract
> + place mod acquisition service, and a Nexus-only update-check service that
> flags mods whose imported version predates a newer Nexus file release.

## Nexus client + auth

### `INexusClient`

The Nexus Mods API client. Auth is per-request via the auth message factory
selector (which reads `NexusConfig.AuthMethod` live); the parsed rate limits are
carried on every response. The v1 REST endpoints cover auth validation,
downloads, + mod-file listing; the v2 GraphQL endpoint covers the update check
(the `modsByUid` batch query).

```csharp
public interface INexusClient
{
    Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default);              // API key
    Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, CancellationToken ct = default);                                  // premium
    Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, string nxmKey, long expiresEpoch, CancellationToken ct = default); // free user
    Task<Response<ModInfo>> GetModInfoAsync(string gameDomain, int modId, CancellationToken ct = default);
    Task<Response<ModFile[]>> ListModFilesAsync(string gameDomain, int modId, CancellationToken ct = default);
    Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(int gameId, IReadOnlyList<int> modIds, CancellationToken ct = default);                          // v2 GraphQL
}
```

- `ValidateAsync` -- hits `GET /v1/users/validate.json` (API-key validate).
- `DownloadLinksAsync` (premium) -- hits `GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json`.
- `DownloadLinksAsync` (free user, with `nxmKey` + `expiresEpoch`) -- same
  endpoint with `?key={nxmKey}&expires={epoch}`.
- `GetModInfoAsync` -- hits `GET /v1/games/{domain}/mods/{modId}.json`.
- `ListModFilesAsync` -- hits `GET /v1/games/{domain}/mods/{modId}/files.json`
  and unwraps the `{"files":[...]}` envelope to the array.
- `CheckUpdatesGraphQlAsync` -- POSTs to `POST /v2/graphql` with the `modsByUid`
  batch query. Computes UIDs from `gameId` + `modIds`
  (`uid = game_id * 2^32 + mod_id`, stringified for the GraphQL `ID` scalar).
  Returns `ModUpdateStatus[]` with the server-computed `viewerUpdateAvailable`
  field per mod. Throws `NexusApiException` on GraphQL-level errors in a 200 OK
  body (in addition to the standard HTTP error handling).

Every method throws `NexusApiException` on a non-2xx; `NexusRateLimitException`
on a rate-limit signal (429, or 403 with `x-rl-*-remaining: 0`);
`NexusNotAuthenticatedException` when `AuthMethod == None` or the selected
method has no usable credentials.

**401-reactive refresh + retry-once.** On a 401, the client asks the auth
factory to refresh (OAuth) or give up (API key, None). On a successful refresh
the request is retried once with the new credentials. A second 401 surfaces as
`NexusApiException` (no infinite retry loop).

### Response wrapper + rate limits

```csharp
public sealed record Response<T>(T Data, NexusRateLimits RateLimits);

public sealed record NexusRateLimits(
    int DailyLimit, int DailyRemaining, DateTimeOffset? DailyReset,
    int HourlyLimit, int HourlyRemaining, DateTimeOffset? HourlyReset);
```

`NexusRateLimits` is parsed from the `x-rl-*` response headers (mirrors NMA's
`ResponseMetadata.FromHttpHeaders`). Missing/unparseable headers yield `0` /
`null` for that field (never throws). The update check inspects them after its
one call to flag the result rate-limited when a window is exhausted; the
metadata backfill inspects them per response + on a `NexusRateLimitException`;
every other call just carries and logs them. For the full rate-limiting strategy (what Curator
observes, how it reacts, what it does not do, and what consumes the budget), see
[Nexus API rate limiting](../architecture/nexus-rate-limiting.md).

The internal `NexusRateLimitReset.ComputeEarliest(NexusRateLimits?, DateTimeOffset)`
helper resolves the soonest server-reported reset of an actually-exhausted
window (remaining budget zero AND reset strictly in the future; earliest wins;
`null` when none applies, when limits are null, or when every exhausted reset is
absent/already-past). Shared by the update check + the metadata backfill so the
two cannot drift on what "the reset is" means. The all-zero
`NexusRateLimits.Unknown` (a 429 with no `x-rl-*` headers) yields `null` here.

### Key Nexus types

```csharp
public sealed class ValidateInfo          // API-key validate response
{
    public long UserId { get; set; }
    public string Key { get; set; }
    public string Name { get; set; }
    public bool IsPremium { get; set; }
    public bool IsSupporter { get; set; }
    public string Email { get; set; }
    public Uri? ProfileUrl { get; set; }
}

// NexusAccessTokenClaims: parsed from the OAuth access token's JWT payload
// (user.username + user.membership_roles). No signature verification; the
// claims are for UI display only. See NexusAccessTokenClaims.TryParse.

public enum NexusMembershipRole { Member, Supporter, Premium, LifetimePremium }

public sealed class DownloadLink           // CDN link from download_link.json
{
    public string Name { get; set; }
    public string ShortName { get; set; }
    public Uri Uri { get; set; }
}

public sealed class ModInfo                 // mod-page payload from mods/{id}.json
{
    public int ModId { get; set; }
    public int GameId { get; set; }
    public string DomainName { get; set; }
    public string Name { get; set; }        // the container display name (acquisition)
    public string Summary { get; set; }     // -> ModDisplayMetadata.Summary (trimmed by the mapper)
    public string Description { get; set; }
    public string Version { get; set; }     // page-header version (tier-2 update flag)
    public string? PictureUrl { get; set; } // nullable -> ModDisplayMetadata.ThumbnailUrl (HTTPS-enforced by the mapper)
    public int EndorsementCount { get; set; }
    public long CreatedTimestamp { get; set; }
    public long UpdatedTimestamp { get; set; }
    public string Author { get; set; }
    public string UploadedBy { get; set; }
    public bool ContainsAdultContent { get; set; }  // -> ModDisplayMetadata.IsAdultContent (copied verbatim)
    public bool Available { get; set; }
    public string Status { get; set; }
}

public sealed class ModFile { /* file_id, file_name, name, version, size, category_id, uploaded_timestamp, archived, ... */ }
```

`ModInfo.PictureUrl` is bound as a nullable string so an absent wire value
(distinct from an empty string) round-trips as `null` before the display-metadata
mapper runs. `ModInfo.ContainsAdultContent` is copied verbatim into
`ModDisplayMetadata.IsAdultContent`. Both feed the
[`ModDisplayMetadataMapper`](#metadata-backfill-service) shared by acquisition
and backfill.

### Typed Nexus exceptions

```csharp
public class NexusApiException : Exception            // unsealed
{
    public int StatusCode { get; }
    public NexusApiException(int statusCode, string message);
}

public sealed class NexusRateLimitException : NexusApiException
{
    public NexusRateLimits? Limits { get; }
}

public sealed class NexusNotAuthenticatedException : Exception;  // AuthMethod == None
```

### Auth message factories

The auth headers are applied per-request by a factory selected live by
`NexusConfig.AuthMethod`. The selection is explicit, **no fallback**: each
method's credentials are required, and the matching inner factory surfaces a
clear error when they are missing.

```csharp
public interface INexusAuthMessageFactory
{
    ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct);
    ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct);   // refresh (OAuth) or give up (ApiKey/None)
    ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct);
}

internal sealed class ApiKeyMessageFactory : INexusAuthMessageFactory;     // apikey: <key>
internal sealed class OAuth2MessageFactory : INexusAuthMessageFactory;     // Authorization: Bearer + 401-reactive refresh
internal sealed class NoneMessageFactory : INexusAuthMessageFactory;       // no auth, IsAuthenticated=false
internal sealed class NexusAuthMessageFactorySelector : INexusAuthMessageFactory;  // picks by AuthMethod
```

The OAuth factory depends on `INexusTokenStore` (the small read-only token view
+ refresh), which is implemented by `NexusOAuthTokenStore` (the OAuth session +
refresh orchestrator, separate from `NexusAuthService` to break the DI cycle).

### Nexus auth orchestrator

```csharp
public interface INexusAuthService
{
    event EventHandler? AuthStateChanged;

    Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default);
    Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default);
}

public sealed record NexusAuthResult       // Nexus auth action result
{
    public bool IsSuccess { get; init; }
    public string? Name { get; init; }
    public bool? IsPremium { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record NexusAuthState(NexusAuthMethod Method, string? Name, bool? IsPremium);

public sealed class NexusOAuthTokenStore : INexusTokenStore;   // OidcClient + token persistence + loopback login
```

- `AuthStateChanged` -- raised whenever an auth action changes the persisted
  `NexusAuthMethod` (OAuth login, API-key validate, or sign-out). Carries no
  payload; subscribers re-read what they need from the live config or
  `GetCurrentStateAsync`. The shell's Nexus flow refreshes the nxm
  handler status when leaving the destination; the DMF prompt is profile-creation-only
  and does not subscribe.
- `LoginWithOAuthAsync` -- runs the OAuth loopback flow (browser + token exchange
  + persist), flips `AuthMethod = OAuth` (clearing any API key), reads the
  display name + Premium state from the access token's JWT payload.
- `LoginWithApiKeyAsync` -- speculative-write + revert-on-failure; flips
  `AuthMethod = ApiKey` (clearing any OAuth tokens).
- `SignOutAsync` -- clears OAuth tokens + API key + resets to `None`.
- `GetCurrentStateAsync` -- returns the verified auth state (name + premium) for
  the Nexus destination's status line; null when `None`. For OAuth, reads the
  access token's JWT payload (no API call); for API key, hits the v1 validate
  endpoint. Returns an unverified state on a failure rather than throwing.

### OAuth loopback browser

`LoopbackBrowser` is the production `IBrowser` (from
`Duende.IdentityModel.OidcClient`). It pre-grabs an ephemeral loopback port
(exposed as `RedirectUri`), then on `InvokeAsync` binds an `HttpListener` on
that port, opens the user's default browser at OidcClient's authorize URL via
the shared `IExternalLauncher` (General library; a launch that could not start
maps to `BrowserResultType.UnknownError`), awaits the callback, and returns the
authorization response. Three-minute flow timeout; on expiry it surfaces
`BrowserResultType.Timeout`. Independent of the `nxm://` scheme handler
(loopback redirect, not `nxm://`).

## Mod acquisition service

The reusable download + extract + place orchestrator. Consumed by the UI-layer
download queue without retooling: the queue resolves or receives a concrete
file id, calls `AcquireFromNexusAsync`, and feeds the returned
`NexusAcquisitionResult` to the profile registration or the update completion.

```csharp
public sealed record NexusAcquisitionResult(
    Guid ContainerId,        // the repository container (existing reused when known)
    string VersionId,        // the imported version's opaque folder id (the PinnedPolicy key)
    string Version,          // the acquired file's release tag, for display
    bool IsHeadFile);        // the acquired file is the mod's newest non-archived MAIN file

public interface IModAcquisitionService
{
    Task<NexusAcquisitionResult> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<(long Received, long? Total)>? progress = null, CancellationToken ct = default);

    Task<NexusAcquisitionResult> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<(long Received, long? Total)>? progress = null, CancellationToken ct = default);

    Task<(int FileId, string Version)> ResolveLatestNexusAsync(   // no download
        string gameDomain, int modId, CancellationToken ct = default);
}
```

- `AcquireFromNexusAsync`: downloads a specific Nexus mod file, extracts it
  into the repository via `IModImportService.Import`, and returns the
  `NexusAcquisitionResult` (container + version ids, the release tag, and
  whether the file is the mod's current head release, computed from the files
  listing the acquisition already reads at zero extra API calls). The caller
  handles profile registration.
- `AcquireLatestNexusAsync`: resolves the mod's newest non-archived MAIN file
  (category_id 1) via `ResolveLatestNexusAsync`, then delegates to
  `AcquireFromNexusAsync` with the resolved `fileId` + null nxm key/expires
  (the premium / auth-only download path). Throws `InvalidOperationException`
  when no MAIN file is available. The head file is the resolved one by
  construction, so `IsHeadFile` is true.
- `ResolveLatestNexusAsync`: resolves the newest non-archived MAIN file WITHOUT
  downloading it (one `ListModFilesAsync` call, nothing else). For callers
  that know the mod id but need a concrete file id up front (the download
  queue's dedupe key, resolved by the enqueue fronts before any item exists)
  and acquire the file later through `AcquireFromNexusAsync`. The same
  `LatestMain` resolution `AcquireLatestNexusAsync` acquires, from one
  implementation, so call sites cannot disagree on "current release".

The `IProgress<(long Received, long? Total)>` parameter is the byte-progress
hook: cumulative bytes received plus the response `Content-Length` total when
the server sent one (null total = unknown; no separate HEAD call is made), so
a caller can render determinate progress without a second request. The
UI-layer download queue wires it to its per-row progress.

### Acquisition flow (`ModAcquisitionService`)

1. **Resolve download links** via `INexusClient.DownloadLinksAsync`. If both
   `nxmKey` and `nxmExpires` are present, the **free-user** overload is used
   (the per-file token from the `nxm://` URL); otherwise the **premium**
   (auth-only) overload. The auth header is applied by the client's auth
   factory. The **first** CDN link (`result.Data[0].Uri`) is used; Nexus
   returns them in priority order.
2. **Resolve metadata** for the Import: `GetModInfoAsync` for the mod name +
   the display metadata + `ListModFilesAsync` + find the file with matching
   `fileId` for the version string + file name + the file's `UploadedTimestamp`
   (Unix seconds). These are 2 API calls (3 total per acquisition, within rate
   limits). Display metadata (summary, thumbnail URL, adult flag) is normalized
   from the **same** `GetModInfoAsync` payload the name came from via the shared
   internal `ModDisplayMetadataMapper` (trim summary + picture URL; empty
   summary becomes `string.Empty`; empty/malformed/non-HTTPS picture URL
   becomes `null`; adult flag copied verbatim), so persisting it adds **no
   extra Nexus request**. **No degraded
   fallback:** if the metadata fetch fails, the acquisition fails with a clear
   error (a mod stored under its numeric id as a name is worse than a clean
   failure message). The matched file's `UploadedTimestamp` is converted to a
   `DateTimeOffset?` (null when the wire value is 0 / absent) and forwarded as
   the imported version's `RemoteUploadedAt`, the basis for the update-check
   publish-date comparison. A `0` is treated as "unknown" so the check falls
   back to `ImportedAt` rather than comparing against epoch.
3. **Download** from the CDN URI to a temp file
   (`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() +
   Path.GetExtension(fileName))`) using a plain `HttpClient` from
   `IHttpClientFactory` + the 81920-byte buffered copy, reporting the
   `IProgress<(long Received, long? Total)>` tuple (cumulative bytes + the
   response `Content-Length` total when present, null without one; no HEAD
   call). The real file extension (from
   the matched `ModFile.FileName`) is preserved on the temp file for log
   clarity; archive detection is content-based (magic bytes), so the extension
   is cosmetic. The temp file is deleted once Import returns (always, success or
   failure; no partial state).
4. **Import** via `IModImportService.Import(tempPath, modName, new NexusSource
   { ModId = modId }, version, remoteUploadedAt, remoteFileId, displayMetadata)`. The import service handles
   find-or-create-container (dedup by `NexusSource.ModId`) + add-version +
   the arrival-rule `IsLatest` re-evaluation + records
   `RemoteUploadedAt` and the Nexus `FileId` on the entry (new or reused), and
   the `displayMetadata` argument replaces the container's `DisplayMetadata` in the
   same manifest update (an acquisition that fetched newer text wins atomically
   with the version write). Both
   acquisition entry points (`AcquireFromNexusAsync` for concrete file ids +
   `AcquireLatestNexusAsync` for resolved head files) route through this
   call, so both record the publish date, the file id, and the display
   metadata. Premium,
   regular nxm, automatic update, per-row update, and DMF acquisitions all
   inherit the capture through the existing common path.
5. **Return** the `NexusAcquisitionResult` (container + version ids, the
   release tag, and the head flag).

The CDN download uses a plain `HttpClient` (not the typed `INexusClient`)
because the CDN URL is an absolute path with the per-file token in the query
string (free users) or just the session auth (premium); no base address or
Nexus-specific headers are needed.

### nxm download handler

The real `INxmModDownloadHandler` that replaces the no-op default. Lives
in the UI assembly (`Modificus.Curator.UI.Nxm`), not Integrations, because it
coordinates UI concerns: it reads the active profile from `IProfileSession`
(UI), shows gate-failure dialogs through `IDialogService` (UI), and marshals
those dialogs to the UI thread via `Dispatcher.UIThread` (Avalonia). Placing it
in Integrations would create a dependency cycle (Integrations cannot reference
the UI assembly, which is its consumer). The reusable acquisition service is
the backend seam in Integrations; the handler is the thin UI-coordinating
shell.

The handler is the **enqueue adapter** in front of the UI-layer download queue
(`IModDownloadQueue`; see the [ui reference](ui.md#the-download-queue)). It
performs no acquisition and no profile write; the passing path is an in-memory
peek plus one enqueue, so `HandleAsync` returns within milliseconds and the
IPC accept loop is freed immediately (enqueue order equals click order). Its
flow:

1. **Darktide-only gate**: the URL's game domain must match the Darktide
   domain (case-insensitive); anything else is refused before auth / profile /
   enqueue with a localized alert naming the link's game.
2. **Auth check** (live config read): `NexusConfig.AuthMethod != None`
   (required for every download; the `nxm://` key/expires is the per-file
   token for the free-user endpoint, NOT a substitute for auth). None ->
   `ShowAlertAsync("Nexus not configured", ...)`.
3. **Active-profile check**: `IProfileSession.ActiveProfileId != null`. null ->
   `ShowAlertAsync("No active profile", ...)`. The gate refusals stay modal
   alerts because at gate time there is no download row to host the failure on.
4. **Peek + enqueue**: a repository lookup by `NexusSource.ModId` supplies the
   container id + the row name (the localized "Nexus mod #<id>" fallback on a
   miss; no prefetch API call), the profile read supplies the target name
   captured at enqueue, and the request (domain, mod id, file id, purpose,
   nxm key/expires) is admitted onto the queue. The queue owns everything
   downstream: the dequeue-time auth recheck, the repository hit check, the
   acquisition with progress, the profile registration or update
   acknowledgement, and the reload; its failures render inline on the
   download row.

`ShowAlertAsync` marshals to the UI thread via an injectable `invokeOnUi` seam
(`Func<Func<Task>, Task>`); production wires `Dispatcher.UIThread.InvokeAsync`,
tests inject a pass-through. The handler is registered AFTER `AddNxm()` so DI
"last registration wins" supersedes the no-op default (see
[DI wiring](#di-registration)).

### OAuth constants (build-time)

`NexusOAuthConstants.ClientId` = `"modificus_curator"` (a build-time const, NOT
config and NOT an env var). No client secret is used: Nexus accepts this client
as a public client, so `NexusOAuthConstants` carries no `ClientSecret` (PKCE S256
protects the authorize leg). The `client_id` is posted in the token request body
(`TokenClientCredentialStyle = PostBody`), which is OidcClient's default with no
`ClientSecret` set. `Scope` = `"openid"` (the OIDC scope OidcClient needs for the
id_token; the display name + Premium state come from the access token's JWT
payload, so no additional scopes are requested). Application headers:
`Application-Name: Modificus-Curator`, `Application-Version: <asm>`,
`Protocol-Version: 1.0.0`, `User-Agent: Modificus-Curator/<ver>`.

## Update check service

A Nexus-only service that checks the active profile's Nexus mods for available
updates and produces a result the mod-list badges consume. Two check shapes
share the same `LastResult` / `CheckCompleted` surface: a periodic check (fired
on profile load + the periodic timer) and a thorough check (the manual "check
now" affordance). Both run the same v2 GraphQL `modsByUid` batch query (1 API
call for all Nexus mods) and flag a mod via three tiers: tier 1
`viewerUpdateAvailable` (the server's authoritative signal), tier 2 a mod-level
version compare, and tier 3 a latest-file-version confirmation that clears
tier-2 false positives (scoped to tier-2-only flags, best-effort, cached). They
differ only in the result's `Thorough` flag. The same batch query also carries
the current Nexus mod `name` for every id sent, so the check syncs each
container's display name to its current Nexus name at zero extra API cost (this
covers EVERY Nexus-sourced mod, Latest AND Pinned; the Nexus name wins, identity
`Id` is unchanged). `PinnedPolicy` mods are never flagged (only `LatestPolicy`
+ `NexusSource` are flagged), and `UntrackedSource` mods are not queried.

```csharp
public sealed record ModListCandidate(Guid ContainerId, ModVersionPolicy Policy);

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default);             // periodic (v2 GraphQL batch query)
    Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default);      // same query, Thorough = true
    UpdateCheckResult? LastResult { get; }
    event EventHandler<UpdateCheckResult?>? CheckCompleted;
}

public sealed record UpdateCheckResult(
    IReadOnlyList<ModUpdateInfo> Updates,
    DateTimeOffset CheckedAt,
    bool RateLimited,
    bool Thorough,
    bool NamesChanged = false,
    CheckOutcome Outcome = CheckOutcome.Failed,
    DateTimeOffset? RateLimitResetsAt = null);

public sealed record ModUpdateInfo(
    Guid ContainerId,
    int ModId,
    string ModName,
    string CurrentVersion,
    DateTimeOffset? LatestUpdateAt);

public enum CheckOutcome { Failed, Success, NoAuth, NoNexusMods, RateLimited }
```

- `CheckAsync(profileId, candidates)`: the periodic check (see flow below).
  The candidates are the profile's mod-list entries (container id + current
  policy), mapped at the call site by the UI layer (which references both
  libraries); Integrations holds no Profiles dependency. `profileId` is used
  only as the update-state key (the persisted known-update snapshot + the
  recorded result are per-profile). Queries the v2 GraphQL `modsByUid` batch
  endpoint (1 API call for all Nexus mods) and flags each Latest + Nexus
  candidate via three tiers (tier 1 `viewerUpdateAvailable`, tier 2 a mod-level
  version compare, tier 3 a latest-file-version confirmation that clears
  tier-2-only false positives). After the tier logic, syncs every Nexus mod's
  display name to its current Nexus name from the same batch response (Pinned
  mods included). An empty candidate list short-circuits to the `NoNexusMods`
  outcome (never a failure): local truth proves no applicable Nexus update can
  exist. Best-effort, never throws for non-cancellation failures: a transient
  API failure, missing auth, or exhausted rate limit all surface as an empty
  result. Cancellation (`OperationCanceledException`) propagates. The caller
  owns profile validity: an unreadable profile surfaces at the caller's
  candidate pull (the runner logs + skips), never here.
- `CheckThoroughAsync(profileId, candidates)`: runs the same v2 batch query as
  `CheckAsync`; the two differ only in the result's `Thorough` flag (`true`
  here). Kept for interface compatibility; both paths run the same query, so
  the flag no longer signals a coverage difference.
- `LastResult`: the last check result, or null before the first check. Holds
  the most recent result regardless of which method produced it. Kept for
  compatibility (the rate-limit notice reads it); the per-row update flags no
  longer read it. Written under a lock alongside the `CheckCompleted` invocation
  + the persisted-state recording.
- `CheckCompleted`: raised (on the completing thread) exactly once per
  `CheckAsync` / `CheckThoroughAsync` call (including the no-auth / rate-limited
  / failure short circuits) with the same result that was just set on
  `LastResult` + recorded through the update-state store.
- `Outcome`: the authoritative outcome of the check. `Success` is the only
  outcome that authoritatively replaces a profile's persisted known-update
  state (including clearing it when the API reports no updates); `NoNexusMods`
  clears it (local state proves no applicable Nexus update); `NoAuth`,
  `RateLimited`, and `Failed` preserve prior state. The UI-layer automatic-update
  service gates execution on `Outcome == Success` with updates.
- `NamesChanged`: `true` when the name-sync pass renamed at least one
  container. The mod-list UI refreshes the affected rows' displayed names in
  place when this is set. Only the normal completion path can set it `true`;
  the short-circuit paths (no auth, no Nexus mods, rate-limited, failure) leave
  it `false`.
- `RateLimitResetsAt`: when `RateLimited` is `true`, the soonest server-reported
  reset of an actually-exhausted window (UTC); `null` when the server reported a
  rate limit without a reset (e.g. an HTTP 429 with no `x-rl-*` headers, the
  all-zero `NexusRateLimits.Unknown` case), or whenever the check was not
  rate-limited. The UI keeps the refresh button disabled until this elapses,
  falling back to a short client-side cooldown when it is absent.

The latest-MAIN filter (`NexusModFiles.LatestMain`, category_id 1 +
non-archived, newest by `UploadedTimestamp`) is shared across two call sites:
`ModAcquisitionService.ResolveLatestNexusAsync` (one implementation behind
both `AcquireLatestNexusAsync` and the download queue's enqueue fronts) and
`UpdateCheckService`'s tier 3 (the latest-file-version confirmation of tier-2
flags), so the update check and the download path agree on what "latest
release" means. (`ModAcquisitionService.ResolveMetadataAsync` resolves a
caller-supplied file id by linear lookup, not via `LatestMain`.)

### Check flow (`UpdateCheckService`)

Both `CheckAsync` + `CheckThoroughAsync` run the same logic (they differ only in
the `Thorough` flag on the result).

1. **Auth gate.** Read `IConfigLoader.Load().Integrations.Nexus.AuthMethod`. If
   `None` -> return an empty result (no API call; the user hasn't configured
   Nexus).
2. **Enumerate the Nexus subset of the candidates.** For each candidate,
   resolve the container via `IModRepository.Get`. Keep every `NexusSource`
   candidate (Latest AND Pinned).
    Skip `UntrackedSource` and `LinkedSource` (linked mods have no Nexus
    identity and no versions, so they never enter the check). Derive a
    `checkable` subset filtered to
    `LatestPolicy` (the flag logic is scoped to it; Pinned mods are frozen
    version-wise and never flagged). If no Nexus candidates at all -> empty
    result (API not called; an empty candidate list lands here too). A profile
    with only Pinned Nexus mods still runs the batch (for
    the name sync).
4. **Query Nexus v2 GraphQL (1 call for ALL Nexus mods).**
   `INexusClient.CheckUpdatesGraphQlAsync(NexusGameIdentity.DarktideGameId, modIds, ct)`
   + `modIds` is EVERY Nexus mod's id (Latest +
   Pinned), so Pinned ids ride along for the name sync. The client computes UIDs
   (`uid = game_id * 2^32 + mod_id`), builds the `modsByUid` GraphQL query, and
   POSTs to `/v2/graphql`. A `NexusRateLimitException` is caught + surfaces as a
   rate-limited result (carrying the soonest server-reported reset of an
   exhausted window, picked from the exception's `NexusRateLimits`). A
   non-cancellation `Exception` is caught + surfaces as an empty result (the
   check is best-effort). Cancellation propagates.
 5. **Rate-limit gate (post-call).** From `response.RateLimits`: treat as
   rate-limited only when a limit was reported AND remaining is zero:
   `(DailyLimit > 0 && DailyRemaining <= 0) || (HourlyLimit > 0 && HourlyRemaining <= 0)`.
   The `> 0` guard avoids a false positive on `NexusRateLimits.Unknown` (the
   all-zero fallback when headers are absent, e.g. test stubs or non-rate-limited
   gateways). If rate-limited -> return an empty result with `RateLimited = true`
   and `RateLimitResetsAt` set to the soonest future reset of an exhausted
   window (the mod-list UI disables the refresh button until that instant
   elapses, falling back to a short client-side cooldown when the server gave
   none).
6. **Map results to flagged list (tiers 1 + 2).** Index the response nodes by
   UID (`Dictionary<long, ModUpdateStatus>`). For each CHECKABLE (Latest-only)
   mod, compute its UID + look up the node. Flag the mod if **either** signal
   triggers, and record which tier drove the flag: (a) `viewerUpdateAvailable == true`
   (tier 1, authoritative; server confirms an update since the user's last
   API-tracked download), or (b) the server's `version` string differs from the
   installed `VersionString` (tier 2, ordinal case-insensitive; catches cases
   `viewerUpdateAvailable` misses: user installed an older version, uses
   multiple PCs, or imported manually). If `viewerUpdateAvailable` is `false` or
   `null` AND versions match (or either version is empty), do not flag. If no
   node was returned for a UID (invalid id, removed mod), do not flag
   (conservative).
7. **Tier 3: latest-file-version confirmation.** For each mod flagged solely by
   tier 2 (not tier 1), resolve the newest non-archived MAIN file via
   `NexusModFiles.LatestMain` (the same filter the download path uses) + clear
   the flag when that file's version equals the installed version (the
   page-header `version` can lag the latest file). A different file version or
   an unresolved / failed resolution leaves the flag. The resolved version is
   cached per (mod id, page version, updated-at) with a 24h TTL, in memory and
   session-scoped, so a repeat check for an unchanged mod makes zero extra
   calls. Tier 3 only ever removes flags; it never adds. Tier-1 flags are
   untouched. See [the update-detection tiers](rate-limiting-strategy.md#update-detection-tiers).
8. **Name sync (free, piggybacks on the batch).** For EVERY Nexus mod (Latest +
   Pinned), look up its node; if the node's `name` is non-empty and differs from
   the container's stored `Name` (ordinal), rename the container via
   `IModRepository.RenameContainer` (identity `Id` unchanged; the Nexus name
   wins). An empty `name` or a missing node triggers no rename; one rename
   failure (it should not throw, but defensively) does not abort the rest. Sets
   `NamesChanged = true` on the result when at least one rename landed.
9. **Return + publish.** Set `LastResult`, raise `CheckCompleted` (under the
   lock), return the result.

### Update-state store

`IUpdateStateStore` owns the persistence rules for profile-scoped "known update
available" knowledge: when a fresh authoritative result replaces (or clears) a
profile's state, when prior state must be preserved, how to acknowledge a
successful local version change, and how to hydrate persisted state for the
current profile while filtering out stale entries.

```csharp
public interface IUpdateStateStore
{
    void RecordResult(Guid profileId, UpdateCheckResult result);
    void AcknowledgeInstall(Guid profileId, Guid containerId);
    IReadOnlyCollection<Guid> GetKnownUpdateContainerIds(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates);
}
```

- `RecordResult`: applies the replacement rules based on the result's
  `Outcome`. `Success` replaces that profile's snapshot with the result's
  flagged mods (clearing when the API reports no updates); `NoNexusMods` clears
  it; `NoAuth`, `RateLimited`, and `Failed` preserve prior state. Called by
  `UpdateCheckService` after every check completes (inside the publish lock).
- `AcknowledgeInstall`: removes a single profile/container entry immediately.
  Called by the download queue's completions (a ProfileAdd registration and a
  successful UpdateInstall) so a just-installed version clears its own
  flag without an extra API check.
- `GetKnownUpdateContainerIds`: reads the persisted snapshots for a profile,
  filters out stale ones (removed / pinned / source-changed / version-changed
  relative to the caller's candidates + the repository), writes the filtered set
  back (self-heal), and returns the container ids that remain flagged. The
  caller passes the profile entries it already holds (mapped to candidates the
  same way as the check); the store pulls no profile state itself. The UI calls
  this on reload + on profile switch + after an acknowledgement + after a check
  completes.

The persisted shape is `KnownUpdateSnapshot` (in General; a plain serializable
DTO) backed by `IKnownUpdateState.KnownUpdates` (a profile-keyed map in
`app-state.json`). Restored/persisted data is never re-published as a fresh
authoritative result; the UI reads it directly to render flags.

### Eligibility rules (`UpdateEligibility`)

The four known-update eligibility rules live in one pure static evaluator,
`UpdateEligibility.IsEligible(candidate, container, expectedModId,
expectedVersion, out reason)`, shared by every consumer that must decide
whether a recorded flag still applies: the state store's hydration self-heal
(`GetKnownUpdateContainerIds`) and the UI-layer download queue's dequeue-time
revalidation of a queued update install. A flag stays eligible only while:

1. the container is still a member of the caller's candidate list,
2. the entry is still on `LatestPolicy`,
3. the container still resolves to a `NexusSource` with the recorded mod id, and
4. the installed version (resolved via `ModContainer.ResolveVersion` with a
   `LatestPolicy`) still matches the recorded version case-insensitively.

A rejection carries a short machine-readable reason ("removed from profile",
"re-pinned", "container gone", "source changed", "version changed") for
logging. The evaluator is pure: no services, no I/O, no clock; everything
arrives as arguments.

## UI wiring (`UpdateCheckRunner`)

There is no mod-update installer in this library. Premium update installs run
through the UI-layer download queue (`IModDownloadQueue` /
`ModUpdateEnqueuer`, both in `ui/Session/`; see the
[ui reference](ui.md#the-download-queue)): the queue's single serial worker is
the one-download-at-a-time gate across manual updates, the automatic batch,
and nxm clicks, and its dequeue-time `UpdateEligibility` revalidation replaces
the former installer's in-gate check. This library contributes only the
primitives the queue composes: the acquisition service, the eligibility
evaluator, and the update-state store.

The triggers that fire the checks live in `UpdateCheckRunner` in `ui/Session/`,
NOT in the Integrations library (the service has no knowledge of profile
switches or of the profile store; it takes the candidate set + the state-key
profile id + checks). The runner is a UI-layer singleton that owns the
candidate pull: each fire reads the profile's mod list through
`IProfileService` inside its thread-pool task + maps the entries to
`ModListCandidate`s at the call site, so Integrations holds no Profiles
dependency. A pull failure (a deleted or unreadable profile) is logged + the
run skipped: no check call, no `LastResult` mutation. The runner subscribes to
`IProfileSession.PropertyChanged` filtered to `ActiveProfileId` only (it ignores
`IsRunning`, which the polling timer drives every few seconds) and fires
`CheckAsync` fire-and-forget via `Task.Run` on three triggers: startup (the
restored active id), an active-profile switch, and the periodic timer (every
`AutoUpdateCheckIntervalMinutes` when `AutoUpdateCheckEnabled` is on; the only
gated trigger). A fourth trigger, the manual "check now" affordance on the mod
list, fires `CheckThoroughAsync` via an awaitable `CheckNowAsync()` (the
mod-list VM awaits it to drive an `IsCheckingNow` spinner; the await also
covers the chained `IAutomaticUpdateService` enqueue batch, so the manual
spinner stays active through the head resolves + enqueues; the installs
themselves run on the download queue afterward). Registered + started from
`CuratorComposition` after the provider is built (best-effort: a wiring failure
is logged + swallowed, never blocks startup). The mod-list UI subscribes to
`CheckCompleted` and reads the profile-scoped `IUpdateStateStore` (not
`LastResult`) to render per-row update flags, passing the entries its last
reload loaded as the hydration candidates.

## Metadata backfill service

`INexusModMetadataService` backfills missing display metadata (summary,
thumbnail URL, adult-content flag) for repository containers that have none yet,
through the stable Nexus v1 `mods/{id}.json` endpoint. The acquisition path
already captures display metadata for every new Nexus import (see [Mod
acquisition service](#mod-acquisition-service)); this service closes the gap for
containers imported before that capture existed or was wired. It is invoked by
the UI-layer detailed-rows coordinator when Detailed mode encounters rows with
no metadata (see [ui: mod list density](ui.md#mod-list-density--detailed-rows)).
Its single operation accepts the active profile's container ids as priority
order and returns an immutable result; the caller fires-and-forgets.

```csharp
public interface INexusModMetadataService
{
    Task<NexusModMetadataResult> BackfillMissingAsync(
        IReadOnlyList<Guid> priorityContainerIds,     // active-profile ids, tried first
        CancellationToken ct = default);
}

public sealed class NexusModMetadataResult
{
    public IReadOnlyDictionary<Guid, ModDisplayMetadata> Updated { get; }   // TryInitializeDisplayMetadata == true
    public int AttemptedCount { get; }                                       // GetModInfoAsync calls made
    public bool RateLimited { get; }                                         // stopped on an exhausted window
    public DateTimeOffset? RateLimitResetsAt { get; }                        // soonest exhausted-window reset, when rate-limited

    public NexusModMetadataResult(
        IReadOnlyDictionary<Guid, ModDisplayMetadata> updated,
        int attemptedCount, bool rateLimited, DateTimeOffset? rateLimitResetsAt);

    public static NexusModMetadataResult Empty { get; }
}
```

### Backfill flow (`NexusModMetadataService`)

`BackfillMissingAsync` is serialized by a `SemaphoreSlim` and never throws for
non-cancellation failures (cancellation propagates). The singletons holds the
semaphore, so the service is registered as a singleton (see
[DI registration](#di-registration)).

1. **Auth gate.** Read `IConfigLoader.Load().Integrations.Nexus.AuthMethod`. If
   `None` -> return `Empty` (no API call). The user has not configured Nexus.
2. **Persisted 24-hour gate (rechecked after acquiring the lock).** Read
   `INexusMetadataBackfillState.LastNexusMetadataBackfillUtc`. When set and within 24 hours
   of now (strict less-than; a future stamp from clock skew gates), return
   `Empty`. Rechecking after the semaphore is acquired means a second
   overlapping call that arrived while the first was running returns empty
   rather than starting a second pass inside the window. See
   [rate-limiting strategy: metadata backfill gate](rate-limiting-strategy.md#metadata-backfill-gate).
3. **Candidate sequence.** Build a distinct list, capped at 25 attempted
   containers: priority ids first (de-duplicated, in caller order), then the
   remaining repository containers in deterministic `Guid` order. Each is
   re-resolved from the repository and included only when it is a
   `NexusSource` container whose `DisplayMetadata` is `null` (missing-only;
   untracked and linked containers are skipped, and a container that already
   carries metadata, even an empty object, is authoritative + skipped).
4. **Per-candidate loop (sequential).** For each candidate: a pre-request
   `Get` re-checks that the metadata is still `null` (an optimization; the
   correctness boundary is the atomic `TryInitializeDisplayMetadata` below).
   When the 25-attempt cap is reached, the pass stops. Otherwise it calls
   `INexusClient.GetModInfoAsync(NexusGameIdentity.DarktideDomain, modId, ct)`.
   - `NexusApiException` (a per-mod API failure, e.g. one removed mod) is
     logged and the pass **continues** with the next candidate.
   - `NexusRateLimitException` sets `RateLimited = true`, resolves the reset via
     `NexusRateLimitReset.ComputeEarliest`, and **stops** the pass (the metadata
     from the triggering response, when applicable, is persisted first).
   - `NexusNotAuthenticatedException` (auth revoked mid-pass) **stops** the pass.
   - Any other `Exception` (transport, repository, config, mapping) is caught by
     the outer boundary, logged, and absorbed into the partial state accumulated
     so far (the pass stops).
   - `OperationCanceledException` propagates (cancellation is not success).
5. **Map + persist.** A successful response's `ModInfo` is normalized through
   the shared internal `ModDisplayMetadataMapper` (the same normalization
   acquisition uses: trim summary + picture URL; empty summary -> `string.Empty`;
   empty/malformed/non-HTTPS picture URL -> `null`; adult flag copied verbatim)
   and persisted via `IModRepository.TryInitializeDisplayMetadata(containerId,
   metadata)`. Only an atomic null-to-non-null transition returns `true` and
   records the container in the result's `Updated` map; a container whose
   metadata was set by a concurrent writer between the pre-request `Get` and the
   atomic check-and-set returns `false` and is **not** overwritten. Existing
   metadata is never cleared or rewritten on any failure path.
6. **Exhausted-counter check (post-response).** From `response.RateLimits`:
   treat as rate-limited only when a limit was reported AND remaining is zero
   (`(DailyLimit > 0 && DailyRemaining <= 0) || (HourlyLimit > 0 &&
   HourlyRemaining <= 0)`, the same `> 0` guard the update check uses to avoid a
   false positive on `NexusRateLimits.Unknown`). When exhausted, set
   `RateLimited = true`, resolve the reset, and stop the pass.
7. **Stamp + return.** After the loop, when at least one API request was
   attempted, stamp `INexusMetadataBackfillState.LastNexusMetadataBackfillUtc = now` (so a
   real pass gates the next one for 24 hours). A no-auth, already-gated, or
   no-candidate no-op attempts zero requests and does **not** stamp. Return an
   immutable `NexusModMetadataResult` (the `Updated` map is defensively wrapped
   in a `ReadOnlyDictionary` over a copy of its pairs, so neither a later
   mutation of the input nor a downcast to a mutable dictionary can change the
   result).

### `ModDisplayMetadataMapper` (internal)

The single normalization from `ModInfo` to the source-agnostic
`ModDisplayMetadata` (see [mods: ModDisplayMetadata](mods.md#moddisplaymetadata-record)).
Shared by acquisition (`ModAcquisitionService.ResolveMetadataAsync`) and the
backfill so the rules cannot drift between the two:

- Trim `ModInfo.Summary` and `ModInfo.PictureUrl`. An empty summary becomes
  `string.Empty`.
- An empty, malformed, or non-HTTPS picture URL becomes `null`
  (`Uri.TryCreate` rejects malformed input without throwing; the scheme check
  keeps the UI thumbnail cache on HTTPS only).
- `ModInfo.ContainsAdultContent` is copied verbatim.

Never returns `null`: a fetched result with no display content is a non-null
object whose `Summary` is empty and whose `ThumbnailUrl` is `null`, so the
container can distinguish fetched-but-empty from not-fetched.

## DI registration

```csharp
public static IServiceCollection AddIntegrations(this IServiceCollection services);
```

Registers:

- `INexusClient` → `NexusClient` as a **typed HTTP client** via
  `AddHttpClient<INexusClient, NexusClient>`, configured from
  `CuratorConfig.Integrations.Nexus.BaseUrl`.
- The auth message factories (`ApiKeyMessageFactory`, `OAuth2MessageFactory`,
  `NoneMessageFactory`) + the `INexusAuthMessageFactory` selector
  (`NexusAuthMessageFactorySelector`).
- `IBrowser` → `LoopbackBrowser` (the production loopback impl).
- `NexusOAuthTokenStore` (singleton; the OAuth token + login orchestrator),
  exposed both directly and as `INexusTokenStore`.
- `NexusAuthService` (singleton; the Nexus auth orchestrator),
  exposed both directly and as `INexusAuthService`.
- `IModAcquisitionService` -> `ModAcquisitionService` (singleton; the download +
  extract + place orchestrator over `INexusClient` + `IModImportService` + a
  plain `HttpClient` from the factory for the CDN download).
- `IUpdateCheckService` -> `UpdateCheckService` (singleton; the Nexus-only
  update check. Depends on `INexusClient` + `IModRepository` + `IConfigLoader` +
  `IUpdateStateStore`; the check set arrives as caller-mapped
  `ModListCandidate`s, so Integrations holds no Profiles reference).
- `INexusModMetadataService` -> `NexusModMetadataService` (singleton; the
  missing-only display-metadata backfill over the stable v1 endpoint. Depends on
  `INexusClient` + `IModRepository` + `IConfigLoader` +
  `INexusMetadataBackfillState`;
  holds the semaphore that serializes overlapping passes).
- `IUpdateStateStore` -> `UpdateStateStore` (singleton; the profile-scoped
  known-update persistence rules over `IKnownUpdateState.KnownUpdates` + the
  caller's candidates + the repository for hydration self-heal).

The OAuth factory's token store + the service's token store are the SAME
`NexusOAuthTokenStore` instance (matches production wiring). The store depends
only on config + the browser; the service depends on the store + the v1 client;
the client depends on the auth factory selector; the selector depends on the
inner factories; the OAuth factory depends on the small `INexusTokenStore`
view. No construction-time cycle.

`AddIntegrations()` resolves `CuratorConfig` + `ILogger<>` from the container.

## Dependencies

- **Curator libraries:** `config` (`CuratorConfig.Integrations.Nexus`),
  `general` (`IConfigLoader`, `INexusMetadataBackfillState` for the metadata-backfill
  gate),
  `mods` (`IModImportService`, `NexusSource`, `IModRepository` /
  `ModContainer` / `ModVersion` / `ModVersionPolicy` for the acquisition +
  update-check services, `ModDisplayMetadata` for the
  acquisition capture + the metadata backfill). Integrations references no
  Profiles library: the update family takes the profile's mod-list entries as
  `ModListCandidate` call parameters, mapped by the UI layer (which references
  both libraries).
- **NuGet:** `Microsoft.Extensions.Http` (`AddHttpClient<TClient,TImpl>` +
  `IHttpClientFactory`), `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`, `Duende.IdentityModel.OidcClient`
  7.1.0 (Apache-2.0, the FOSS-licensed OIDC client; not the dual-licensed
  IdentityServer product).
- **BCL otherwise:** `System.Net.HttpListener` (the loopback listener),
  `System.Diagnostics.Process` (the browser launcher), `System.Text.Json`
  (response parsing) -- all in-box on net10.0.

## Testing

`Modificus.Curator.Integrations.Tests` covers:

- **`NexusClient`** against a fake `HttpMessageHandler` (`StubHttpMessageHandler`)
  -- v1 endpoint paths, response parsing, rate-limit header parsing, error
  mapping, the auth gate, and the 401-retry-after-refresh path.
- **Auth message factories** -- `ApiKeyMessageFactory` adds the `apikey` header;
  `OAuth2MessageFactory` adds `Authorization: Bearer` + refreshes on 401 (via a
  fake `INexusTokenStore`); the selector picks the right one based on the live
  `AuthMethod`; concurrent 401s coalesce into one refresh.
- **`NexusAuthService`** + **`NexusOAuthTokenStore`** -- API-key validate (success
  + revert-on-failure), OAuth login (via the backchannel seam against a stub
  discovery + token endpoint), token refresh (persist), sign-out, switching
  methods clears the other method's credentials.
- **`NexusConfig` JSON round-trip** -- defaults, OAuth tokens persist + reload.
- **`LoopbackBrowser`** + **`HttpListenerLoopbackListener`** -- a real listener
  binds an ephemeral loopback port; an `HttpClient` simulates the browser
  redirect; the listener returns the callback query string; the friendly HTML
  response is served.
- **`AddIntegrations`** DI wiring (the Nexus client + auth factory resolution +
  the acquisition service + the update-check service + the metadata-backfill
  service, all as singletons).
- **`ModAcquisitionService`** against a fake `INexusClient` + a fake
  `IModImportService` + a stub HTTP handler for the CDN download: premium vs
  free-user overload selection, first-CDN-link use, metadata resolution (name +
  version + the display-metadata capture from the shared mapper with no extra
  API call + its pass-through to `Import`, including the Nexus file id
  forwarded as `remoteFileId`), the no-degraded-fallback error policy
  (metadata failure + missing file throw, no partial import), download failure,
  import-failure temp cleanup, progress reporting (cumulative bytes + the
  Content-Length total, null total without one), cancellation, the
  latest-MAIN-file resolution + null-nxm-token forward + no-MAIN-file throw for
  `AcquireLatestNexusAsync`, the `IsHeadFile` computation (false for an older
  MAIN file, archived + optional files ignored for head-ness), and
  `ResolveLatestNexusAsync` (returns the head file id + tag without
  downloading; throws when no MAIN file exists or the domain is empty).
- **`UpdateCheckService`** against a fake `INexusClient` + caller-built
  `ModListCandidate` batches + a fake `IModRepository` + the `FakeConfigLoader`:
   correct flagging (`viewerUpdateAvailable == true` flags, `false` + `null` do not),
   `PinnedPolicy` (flag-wise) / `UntrackedSource` / `LinkedSource` skipping,
  no-auth short-circuit (no API call), no-Nexus-mods short-circuit,
  rate-limit guard (the `> 0` guard prevents a false positive on `NexusRateLimits.Unknown`,
  symmetric daily + hourly paths, + `NexusRateLimitException` surfacing),
  API-failure best-effort, the `LastResult` + `CheckCompleted` contract, the
  `Thorough` flag on both methods, the 1-API-call-per-check contract (the batch
  covers ALL Nexus mods, Latest + Pinned), +
  `CheckThoroughAsync` producing the same results as `CheckAsync`. Mods missing
  from the response (a UID did not resolve) are not flagged (conservative). Tier
  3 is covered: the false-positive clear, the real-update keep, the tier-1 skip,
  the cache hit / invalidation on page-version change, and the failure-leaves
  -flag path. Name sync is covered: a rename when the Nexus name differs (no
  extra API call), no rename when the name matches or is empty/missing, a Pinned
  mod synced but not flagged, the flag-uses-pre-sync-name ordering, and the
  per-mod defensive catch (one rename failure does not abort the pass). The
  internal `NexusClient.CheckUpdatesGraphQlAsync` is covered
  against canned HTTP responses: POST to `/v2/graphql`, UID computation from
  game id + mod ids, string + numeric UID deserialization, GraphQL-error
  surfacing (200 OK body
  with errors), + rate-limit exception.
- **`UpdateEligibility`** -- the four rules evaluated directly: the eligible
  baseline, each rejection reason (removed / re-pinned / container gone /
  source changed by id + by source type / version changed), and the
  case-insensitive version match. The update-install revalidation that
  consumes the evaluator runs in the UI-layer download queue (covered by
  `ModDownloadQueueTests` in `Modificus.Curator.UI.Tests`).
- **`NexusModMetadataService`** against a fake `INexusClient` + a fake
  `IModRepository` + the `FakeConfigLoader` + a fake/real backfill state:
  the auth gate (None returns empty, no API call), the persisted 24-hour gate
  (boundary, future stamp, clock skew, extreme values), serialized overlapping
  passes (the semaphore + the post-lock gate recheck), candidate selection
  (priority ordering, distinct ids, deterministic `Guid`-order remainder,
  Nexus-only + missing-only filtering), the 25-attempt cap, per-candidate
  concurrency (the `TryInitializeDisplayMetadata` atomicity means a concurrent
  acquisition cannot be clobbered by a stale fetch that raced the check-and-set),
  the stop/continue policy (`NexusApiException` continues; `NexusRateLimitException`,
  `NexusNotAuthenticatedException`, and exhausted counters stop; generic
  transport/repository/config failures are absorbed), the stamping rules (a real
  attempt stamps; a no-auth / already-gated / no-candidate no-op does not), the
  never-throws boundary, the immutable `NexusModMetadataResult` (defensive copy
  of `Updated`, not downcastable to a mutable dictionary, the `Empty` singleton
  fresh), and the shared `ModDisplayMetadataMapper` normalization (trim summary +
  picture URL, empty summary -> `string.Empty`, empty/malformed/non-HTTPS picture
  URL -> `null`, adult flag copied verbatim).

The internal `NexusClient`, `NexusAuthService`, `NexusOAuthTokenStore`,
`LoopbackBrowser`, `HttpListenerLoopbackListener`, `ModAcquisitionService`,
`NexusModMetadataService`, `ModDisplayMetadataMapper`, `UpdateCheckService`,
`NexusRateLimitReset`, and the auth factories are visible to tests via
`InternalsVisibleTo`. The `NxmModDownloadHandler` (UI) is tested in
`Modificus.Curator.UI.Tests` (visible via the UI project's `InternalsVisibleTo`),
alongside the `UpdateCheckRunner` (the UI-layer wiring that fires the check on
profile load).

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md) -- the
  [Mod sources / integrations](../architecture/MODIFICUS-CURATOR.md#mod-sources--integrations)
  section + the
  [Nexus authentication](../architecture/MODIFICUS-CURATOR.md#nexus-authentication)
  subsection.
- [config](config.md) -- the `NexusConfig` schema.
- [nxm](nxm.md) -- the `nxm://` scheme handler, including the no-op default
  handler seam that the real `NxmModDownloadHandler` supersedes via DI
  last-registration-wins.
