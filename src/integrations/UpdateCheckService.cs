using System.Collections.Concurrent;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IUpdateCheckService"/>. Queries the Nexus v2 GraphQL
/// <c>modsByUid</c> batch endpoint for the update status of the caller's
/// <see cref="LatestPolicy"/> + <see cref="NexusSource"/> candidates via
/// <see cref="INexusClient"/>. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>One batch query, three-tier detection.</b> The v2 <c>modsByUid</c> query
/// takes a batch of mod UIDs and returns the server-computed
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> field for each: true if
/// the mod has been updated since the viewer (current user) last downloaded it.
/// This eliminates the v1 approach's Month-endpoint intersect, cross-endpoint
/// timestamp tolerance, per-mod reconciliation, and reconciliation pinning. The
/// server tracks the user's downloads and computes the signal directly.</para>
/// <para>
/// A mod is flagged when any of three tiers fire. <b>Tier 1:</b>
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> is <c>true</c> (the
/// server's authoritative "updated since you downloaded" signal). <b>Tier 2:</b>
/// the installed file version differs from the mod-page header
/// <see cref="ModUpdateStatus.Version"/> (catches older-version-installed,
/// multi-PC, and manual-import cases the server's per-user tracking misses).
/// <b>Tier 3:</b> latest-file-version confirmation, scoped to tier-2-only flags
/// (tier 1 is authoritative and never second-guessed). It resolves the newest
/// non-archived MAIN file via <see cref="NexusModFiles.LatestMain"/> (the same
/// filter the download path uses) and clears the flag when that file's version
/// equals the installed version. The mod-page header version can lag the latest
/// file (the author bumps the file without updating the header), which is the
/// false positive tier 2 produces; tier 3 confirms against the actual file.
/// Best-effort and cached: a failure or an unresolved file leaves the flag, and
/// the resolved version is cached per (mod id, page version, updated-at) so a
/// repeat check for an unchanged mod makes zero extra calls.</para>
/// <para>
/// <b>Two check shapes, identical logic.</b> Both <see cref="CheckAsync"/>
/// (the periodic / profile-load path) and <see cref="CheckThoroughAsync"/> (the
/// manual "check now" path) run the same v2 batch query; they differ only in
/// the result's <see cref="UpdateCheckResult.Thorough"/> flag (kept for
/// interface compatibility).</para>
/// <para>
/// <b>Best-effort, never throws (except cancellation).</b> A transient API
/// failure, a missing auth config, or an exhausted rate limit all surface as an
/// empty result (with <see cref="UpdateCheckResult.RateLimited"/> set when
/// applicable). The caller fires-and-forgets the check; there is nothing for it
/// to catch. Cancellation (<see cref="OperationCanceledException"/>) propagates
/// so a cancelled check is not misreported as "no updates found".</para>
/// <para>
/// <b>Nexus-only.</b> Untracked mods are skipped (no remote to query). The
/// update-FLAG logic (tiers 1/2/3) is scoped to
/// <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods;
/// <see cref="PinnedPolicy"/> mods are frozen version-wise and are never
/// flagged. The name-sync pass covers EVERY <see cref="NexusSource"/> mod in
/// the profile (Latest AND Pinned): the batch query already returns the name
/// for free, so the sync piggybacks on it at zero extra API cost.</para>
/// </remarks>
internal sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly INexusClient _nexus;
    private readonly IModRepository _repository;
    private readonly IConfigLoader _configLoader;
    private readonly IUpdateStateStore _stateStore;
    private readonly ILogger<UpdateCheckService> _logger;

    /// <summary>
    /// The clock, for testable tier-3 cache TTL math. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>; tests inject a controllable clock.
    /// Only <see cref="ResolveLatestFileVersionAsync"/> reads this (the TTL age
    /// check + the cache entry's <c>CachedAt</c> stamp, which feeds back into
    /// the next age check). The result timestamps elsewhere are record-keeping
    /// and read <see cref="DateTimeOffset.UtcNow"/> directly, unaffected by this
    /// seam. Mirrors the <c>UpdateCheckRunner</c> clock seam.
    /// </summary>
    private readonly Func<DateTimeOffset> _getNow;

    /// <summary>
    /// Guards the <see cref="LastResult"/> write + the
    /// <see cref="CheckCompleted"/> invocation so an event subscriber observes
    /// the result that was just published. See
    /// <see cref="IUpdateCheckService.LastResult"/> for the full rationale.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckCompleted"/> is invoked inside this lock. Subscribers
    /// must not synchronously call back into the service in a way that re-enters
    /// a check (it would contest this lock); the badge refresh is expected to
    /// marshal to the UI thread, which avoids the re-entry. The lock body is
    /// one assignment + one invoke, so the hold time is minimal.
    /// </remarks>
    private readonly object _publishLock = new();

    /// <summary>
    /// In-memory cache for tier 3 (latest-file-version confirmation), keyed on the
    /// three batch-query signals that are free to observe: the mod id, the mod-page
    /// header <see cref="ModUpdateStatus.Version"/>, and
    /// <see cref="ModUpdateStatus.UpdatedAt"/>. Any observable mod-level change
    /// invalidates the entry (the key no longer matches), so tier 3 re-resolves
    /// automatically when the mod author bumps the page version or the updated-at
    /// timestamp. A <see cref="LatestFileCacheTtl"/> backstop re-resolves even on
    /// an unchanged key, for the rare case a new file is uploaded without ticking
    /// either field. Session-scoped: not persisted to app-state.json.
    /// </summary>
    /// <remarks>
    /// A <see cref="ConcurrentDictionary{TKey, TValue}"/> so overlapping checks
    /// (a profile switch landing on a periodic tick) cannot corrupt it. A redundant
    /// resolve under such an overlap is harmless: the cache simply takes the last
    /// writer, and the redundant call is bounded by the small tier-2-only subset.
    /// </remarks>
    private readonly ConcurrentDictionary<LatestFileCacheKey, (string? LatestFileVersion, DateTimeOffset CachedAt)> _latestFileCache = new();

    /// <summary>
    /// The tier-3 cache TTL backstop. Even on an unchanged key (mod id + page
    /// version + updated-at), an entry older than this is re-resolved so a new file
    /// uploaded without a page-version or updated-at bump is eventually picked up.
    /// 24 hours balances staleness against call savings.
    /// </summary>
    private static readonly TimeSpan LatestFileCacheTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// The tier-3 cache key: the mod id + the two mod-page signals the batch query
    /// already returns. Any change to either field invalidates the entry (the key
    /// no longer matches), so tier 3 re-resolves automatically when the mod author
    /// bumps the page version or the updated-at timestamp. The nullable
    /// <see cref="UpdatedAt"/> compares null-equals-null, so two checks that both
    /// observe a missing updated-at share an entry.
    /// </summary>
    private readonly record struct LatestFileCacheKey(
        int ModId,
        string PageVersion,
        DateTimeOffset? UpdatedAt);

    /// <param name="nexus">The Nexus v1/v2 client (the GraphQL batch query +
    /// the per-mod file listing for tier 3).</param>
    /// <param name="repository">The mod repository (to resolve installed
    /// versions + containers).</param>
    /// <param name="configLoader">Read live for the Nexus auth gate (AuthMethod.None
    /// short-circuits before the API is touched).</param>
    /// <param name="stateStore">Records each result's authoritative outcome so
    /// persisted known-update state survives a restart + self-heals against the
    /// live profile.</param>
    /// <param name="logger">Structured logger for best-effort failure paths.</param>
    /// <param name="getNow">The clock, for testable tier-3 cache TTL math.
    /// Defaults to <see cref="DateTimeOffset.UtcNow"/> when null (the production
    /// path). DI supplies this default for the unregistered optional parameter;
    /// tests inject a controllable clock to drive the TTL deterministically.</param>
    public UpdateCheckService(
        INexusClient nexus,
        IModRepository repository,
        IConfigLoader configLoader,
        IUpdateStateStore stateStore,
        ILogger<UpdateCheckService> logger,
        Func<DateTimeOffset>? getNow = null)
    {
        _nexus = nexus ?? throw new ArgumentNullException(nameof(nexus));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public UpdateCheckResult? LastResult { get; private set; }

    /// <inheritdoc />
    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckAsync(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
        => RunCheckAsync(profileId, candidates, thorough: false, ct);

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckThoroughAsync(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
        => RunCheckAsync(profileId, candidates, thorough: true, ct);

    /// <summary>
    /// The shared check logic for both <see cref="CheckAsync"/> +
    /// <see cref="CheckThoroughAsync"/>. Runs the auth gate, the Nexus-mods
    /// enumeration over the candidates (Latest + Pinned), the single v2 GraphQL
    /// batch query (one call for all Nexus mods), the rate-limit gate, the
    /// three-tier update mapping scoped to the Latest subset (tier 1
    /// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/>, tier 2 mod-level
    /// version compare, tier 3 latest-file-version confirm), and a name-sync
    /// pass over ALL Nexus mods that renames each container to match its current
    /// Nexus mod name (piggybacks on the batch response at zero extra API cost),
    /// then publishes the result with the given <paramref name="thorough"/> flag.
    /// Tier 3 only refines tier-2-only flags (Latest subset); name sync covers
    /// both Latest + Pinned. Both check shapes inherit both via this method.
    /// </summary>
    private async Task<UpdateCheckResult> RunCheckAsync(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates, bool thorough, CancellationToken ct)
    {
        // 1. Auth gate. No auth configured means the user has not signed in; the
        //    Nexus client would refuse the call, so short-circuit to an empty
        //    result without touching the API. Do not throw: the user simply
        //    hasn't configured Nexus yet.
        var authMethod = _configLoader.Load().Integrations.Nexus.AuthMethod;
        if (authMethod == NexusAuthMethod.None)
        {
            _logger.LogDebug("Update check skipped: Nexus auth not configured.");
            return Publish(profileId, EmptyResult(thorough, CheckOutcome.NoAuth));
        }

        // 2. The Nexus subset of the candidates. Enumerate EVERY Nexus-sourced
        //    candidate (Latest OR Pinned): the batch query is sent for this full
        //    set so both the update-flag logic AND the name-sync pass read their
        //    data from one call. Pinned mods ride along for name sync only (the
        //    flag logic below stays scoped to Latest). Skip UntrackedSource (no
        //    remote to query) + candidates whose container is missing.
        var nexusMods = new List<(ModListCandidate Candidate, ModContainer Container, NexusSource Nexus)>();
        foreach (var candidate in candidates)
        {
            var container = _repository.Get(candidate.ContainerId);
            if (container is null)
            {
                continue;
            }

            if (container.Source is not NexusSource nexus)
            {
                continue;
            }

            nexusMods.Add((candidate, container, nexus));
        }

        // The flaggable subset: Latest-only (Pinned mods are frozen version-wise
        // and must NOT be flagged). Tier logic iterates this subset; name sync
        // iterates the full nexusMods set.
        var checkable = nexusMods
            .Where(m => m.Candidate.Policy is LatestPolicy)
            .ToList();

        if (nexusMods.Count == 0)
        {
            // No Nexus mods at all (not even Pinned): nothing to flag + nothing
            // to name-sync. A profile with only Pinned Nexus mods still runs the
            // batch (for the name sync), so the gate is on nexusMods, not
            // checkable. An empty candidate list lands here too (the local truth
            // that no applicable Nexus update can exist).
            _logger.LogDebug(
                "Update check skipped: no Nexus mods among the {Count} candidate(s) for profile {Profile}.",
                candidates.Count, profileId);
            return Publish(profileId, EmptyResult(thorough, CheckOutcome.NoNexusMods));
        }

        // 3. Query Nexus v2 GraphQL (1 call for ALL Nexus mods, Latest + Pinned).
        //    Best-effort: a non-cancellation failure surfaces as an empty result
        //    rather than propagating to the fire-and-forget caller. Cancellation
        //    propagates so a cancelled check is not misreported as "no updates
        //    found". A NexusRateLimitException is surfaced specifically as a
        //    rate-limited result (not a generic failure) so the UI can show
        //    "check incomplete".
        var modIds = nexusMods.Select(c => c.Nexus.ModId).ToList();
        Response<ModUpdateStatus[]> response;
        try
        {
            response = await _nexus.CheckUpdatesGraphQlAsync(NexusGameIdentity.DarktideGameId, modIds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NexusRateLimitException ex)
        {
            _logger.LogInformation(
                ex,
                "Nexus update check rate-limited; reporting rate-limited result.");
            return Publish(profileId, new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: thorough,
                Outcome: CheckOutcome.RateLimited,
                RateLimitResetsAt: NexusRateLimitReset.ComputeEarliest(ex.Limits, DateTimeOffset.UtcNow)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nexus update check failed; reporting empty result.");
            return Publish(profileId, EmptyResult(thorough, CheckOutcome.Failed));
        }

        // 4. Rate-limit gate (post-call, on a successful response). Guard
        //    against NexusRateLimits.Unknown (the all-zero fallback when the
        //    rate-limit headers are absent): only treat as rate-limited when a
        //    limit was actually reported AND remaining is zero. A naive
        //    Remaining <= 0 check would false-positive on every response that
        //    did not carry the headers (test stubs, non-rate-limited gateways).
        var limits = response.RateLimits;
        bool rateLimited = (limits.DailyLimit > 0 && limits.DailyRemaining <= 0)
                        || (limits.HourlyLimit > 0 && limits.HourlyRemaining <= 0);
        if (rateLimited)
        {
            _logger.LogInformation(
                "Nexus update check rate-limited (daily {DailyRem}/{DailyLim}, hourly {HourlyRem}/{HourlyLim}).",
                limits.DailyRemaining, limits.DailyLimit,
                limits.HourlyRemaining, limits.HourlyLimit);
            return Publish(profileId, new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: thorough,
                Outcome: CheckOutcome.RateLimited,
                RateLimitResetsAt: NexusRateLimitReset.ComputeEarliest(limits, DateTimeOffset.UtcNow)));
        }

        // 5. Map the batch response to the flagged list (tiers 1 + 2). Index the
        //    response nodes by UID so the per-checkable-mod lookup is O(1). First
        //    occurrence wins: the API should not duplicate, but be defensive.
        //    Each flag records whether it was driven by tier 1
        //    (viewerUpdateAvailable) or tier 2 only (version mismatch); tier 3
        //    (below) refines only the tier-2-only flags.
        var byUid = new Dictionary<long, ModUpdateStatus>();
        if (response.Data is not null)
        {
            foreach (var status in response.Data)
            {
                byUid.TryAdd(status.Uid, status);
            }
        }

        var flagged = new List<(ModUpdateInfo Info, ModUpdateStatus Node, bool Tier1Driven)>();
        foreach (var (candidate, container, nexus) in checkable)
        {
            var uid = (long)NexusGameIdentity.DarktideGameId * 4294967296L + nexus.ModId;
            if (!byUid.TryGetValue(uid, out var status))
            {
                // The API did not return a node for this UID (invalid id,
                // removed mod, or a UID that did not resolve). Conservative:
                // do not flag a mod we cannot confirm an update for.
                continue;
            }

            var resolved = container.ResolveVersion(new LatestPolicy());
            var installedVersion = resolved?.VersionString ?? string.Empty;

            // Two-tier update detection (tier 3 refines tier-2-only flags after
            // the loop). viewerUpdateAvailable alone is insufficient: the server
            // tracks a single per-user download timestamp (tied to the Nexus
            // account, not the machine), so it returns false when the user
            // installed an older version, uses multiple PCs with different local
            // versions, or imported manually (no API-tracked download). The
            // version comparison (tier 2) catches these by comparing the
            // installed version directly against the server's current page
            // version. Either signal triggering is sufficient to flag.
            bool tier1Driven = status.ViewerUpdateAvailable == true;
            bool tier2Driven = !tier1Driven
                && !string.IsNullOrEmpty(status.Version)
                && !string.IsNullOrEmpty(installedVersion)
                && !string.Equals(installedVersion, status.Version,
                    StringComparison.OrdinalIgnoreCase);

            if (!tier1Driven && !tier2Driven)
            {
                continue;
            }

            flagged.Add((
                new ModUpdateInfo(
                    candidate.ContainerId,
                    nexus.ModId,
                    container.Name,
                    installedVersion,
                    status.UpdatedAt),
                status,
                tier1Driven));
        }

        // 6. Tier 3: latest-file-version confirmation. For mods flagged solely by
        //    the tier-2 version mismatch, resolve the newest non-archived MAIN file
        //    (the same NexusModFiles.LatestMain filter the download path uses) and
        //    clear the flag when that file's version equals the installed version.
        //    The mod-page header version can lag the latest file (the author bumps
        //    the file without updating the page header), which is exactly the false
        //    positive tier 2 produces; tier 3 confirms against the actual file.
        //    Tier-1 flags are untouched: viewerUpdateAvailable is authoritative and
        //    tier 3 does not second-guess it. Tier 3 only ever removes entries from
        //    flagged; it never adds. Calls run sequentially (the tier-2-only subset
        //    F is small, so simplicity wins over bounded parallelism).
        for (int i = flagged.Count - 1; i >= 0; i--)
        {
            var (info, node, tier1Driven) = flagged[i];
            if (tier1Driven)
            {
                continue;
            }

            var latestFileVersion = await ResolveLatestFileVersionAsync(
                info.ModId, node.Version, node.UpdatedAt, ct).ConfigureAwait(false);

            // Equal file versions: the tier-2 flag was a false positive (the page
            // header version lagged the latest file). Clear it. A different version
            // (a real update) or an unresolved / failed resolution leaves the flag.
            if (latestFileVersion is not null
                && string.Equals(latestFileVersion, info.CurrentVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                flagged.RemoveAt(i);
            }
        }

        // 7. Name sync: piggyback on the same batch response (zero extra API
        //    calls). The v2 query returns the current Nexus mod name for every
        //    id sent; each returned name is compared to the container's stored
        //    Name and the container is renamed when they differ. Covers ALL
        //    NexusSource mods (Latest AND Pinned); the Nexus name wins, identity
        //    (Container.Id) is unchanged. An empty/null name triggers no rename,
        //    and a missing node (a UID that did not resolve) skips the mod. One
        //    rename failure (it should not throw, but defensively) does not
        //    abort the rest of the pass or the check.
        bool namesChanged = false;
        foreach (var (_, container, nexus) in nexusMods)
        {
            var uid = (long)NexusGameIdentity.DarktideGameId * 4294967296L + nexus.ModId;
            if (!byUid.TryGetValue(uid, out var status))
            {
                continue;
            }

            if (string.IsNullOrEmpty(status.Name))
            {
                continue;
            }

            if (string.Equals(status.Name, container.Name, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _repository.RenameContainer(container.Id, status.Name);
                namesChanged = true;
                _logger.LogInformation(
                    "Synced container {Id} name '{Old}' -> '{New}' from Nexus.",
                    container.Id, container.Name, status.Name);
            }
            catch (Exception ex)
            {
                // Defensive: RenameContainer should not throw, but a single
                // failure must not abort the rest of the name-sync pass or the
                // check. The stored name is left unchanged.
                _logger.LogWarning(ex,
                    "Name sync for container {Id} failed; leaving the stored name unchanged.",
                    container.Id);
            }
        }

        _logger.LogInformation(
            "Update check for profile {Profile}: {Count} mod(s) flagged for update.",
            profileId, flagged.Count);
        return Publish(profileId, new UpdateCheckResult(
            flagged.Select(f => f.Info).ToList(),
            DateTimeOffset.UtcNow,
            RateLimited: false,
            Thorough: thorough,
            NamesChanged: namesChanged,
            Outcome: CheckOutcome.Success));
    }

    /// <summary>
    /// Resolves the latest MAIN file version for <paramref name="modId"/> for tier 3,
    /// with an in-memory cache keyed on <see cref="LatestFileCacheKey"/>. A cache hit
    /// within <see cref="LatestFileCacheTtl"/> returns the cached version without an
    /// API call. A cache miss (or an expired entry) calls
    /// <see cref="INexusClient.ListModFilesAsync"/> + <see cref="NexusModFiles.LatestMain"/>
    /// and caches the resolved verdict.
    /// </summary>
    /// <returns>The latest MAIN file version, or <c>null</c> if the mod has no MAIN
    /// file or the resolution failed. A null result means "cannot confirm": the caller
    /// leaves the mod flagged. A failure (network, rate limit, any exception) is NOT
    /// cached (a transient error should not pin the verdict for the TTL window); the
    /// no-MAIN-file verdict IS cached so a mod that genuinely lacks a MAIN file is not
    /// re-queried every check.</returns>
    /// <remarks>
    /// Cancellation (<see cref="OperationCanceledException"/>) propagates, matching the
    /// rest of <see cref="RunCheckAsync"/>'s cancellation contract: a cancelled check is
    /// not misreported as "no updates found".
    /// </remarks>
    private async Task<string?> ResolveLatestFileVersionAsync(
        int modId, string pageVersion, DateTimeOffset? pageUpdatedAt, CancellationToken ct)
    {
        var key = new LatestFileCacheKey(modId, pageVersion, pageUpdatedAt);
        var now = _getNow();

        if (_latestFileCache.TryGetValue(key, out var cached)
            && now - cached.CachedAt < LatestFileCacheTtl)
        {
            return cached.LatestFileVersion;
        }

        try
        {
            var files = await _nexus.ListModFilesAsync(NexusGameIdentity.DarktideDomain, modId, ct)
                .ConfigureAwait(false);
            var latest = NexusModFiles.LatestMain(files.Data);
            var version = latest?.Version;
            // Cache the resolved verdict (including null = no MAIN file) so an
            // unchanged mod makes zero extra calls on the next check.
            _latestFileCache[key] = (version, now);
            return version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a tier-3 failure leaves the mod flagged (the tier-1+2
            // baseline still holds). Do not fail the whole check and do not set the
            // result's RateLimited flag. Not cached: a transient failure should not
            // pin the verdict for the TTL window.
            _logger.LogWarning(ex,
                "Tier-3 latest-file resolution for mod {ModId} failed; leaving the mod flagged.",
                modId);
            return null;
        }
    }

    /// <summary>
    /// Sets <see cref="LastResult"/>, records the result's authoritative outcome
    /// through the persisted known-update store (so it survives a restart + is
    /// available before the next check), and raises <see cref="CheckCompleted"/>
    /// atomically (under <see cref="_publishLock"/>) so an event subscriber
    /// observes the result that was just published. Returns the result for
    /// caller convenience.
    /// </summary>
    /// <param name="profileId">The profile this result is scoped to (the store
    /// records state per-profile so a result from one profile never becomes
    /// another's).</param>
    /// <param name="result">The result to publish + record.</param>
    /// <remarks>
    /// <see cref="IUpdateStateStore.RecordResult"/> applies the replacement rules
    /// (success replaces / clears; no-Nexus-mods clears; failure / no-auth /
    /// rate-limit preserve). Recording happens inside the publish lock so the
    /// persisted state is consistent with the published result an observer sees.
    /// The store is itself lock-protected; this lock only orders publish + record
    /// against a concurrent check. Recording never throws (the store swallows
    /// persistence failures), so it cannot break the publish.
    /// </remarks>
    private UpdateCheckResult Publish(Guid profileId, UpdateCheckResult result)
    {
        lock (_publishLock)
        {
            LastResult = result;
            try
            {
                _stateStore.RecordResult(profileId, result);
            }
            catch (Exception ex)
            {
                // Defensive: RecordResult should not throw (the store swallows
                // persistence failures), but a persistence bug must not break the
                // publish or the CheckCompleted event.
                _logger.LogWarning(ex, "Recording update state for profile {Profile} failed.", profileId);
            }
            CheckCompleted?.Invoke(this, result);
        }
        return result;
    }

    /// <summary>
    /// A fresh empty non-rate-limited result, stamped at the current time, with
    /// the given <paramref name="thorough"/> flag + <paramref name="outcome"/>.
    /// Used by the no-auth + no-checkable-mods + API-failure short-circuit paths.
    /// </summary>
    private static UpdateCheckResult EmptyResult(bool thorough, CheckOutcome outcome) =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: thorough,
            Outcome: outcome);
}
