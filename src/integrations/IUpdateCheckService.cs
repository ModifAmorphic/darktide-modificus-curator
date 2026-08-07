using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Checks a profile's Nexus-sourced mods for available updates. Both check
/// shapes (<see cref="CheckAsync"/> + <see cref="CheckThoroughAsync"/>) run the
/// same v2 GraphQL <c>modsByUid</c> batch query (1 API call for all mods) and
/// flag a mod via three tiers: tier 1 the server-computed
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> field (true if the mod has
/// been updated since the user last downloaded it), tier 2 a mod-level version
/// compare (the installed version vs the mod-page header version), and tier 3 a
/// latest-file-version confirmation scoped to tier-2-only flags (resolves the
/// newest MAIN file + clears the flag when it matches the installed version;
/// best-effort + cached). Tier 1 is authoritative and never second-guessed. The
/// two shapes differ only in the result's <see cref="UpdateCheckResult.Thorough"/>
/// flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Nexus-only: Untracked mods have no remote to query.
/// <see cref="PinnedPolicy"/> mods are frozen version-wise, so they are never
/// flagged for an update. The update-FLAG logic (tiers 1/2/3) is scoped to the
/// <see cref="LatestPolicy"/> + <see cref="NexusSource"/> subset. The name-sync
/// pass (below) covers EVERY <see cref="NexusSource"/> mod in the profile,
/// Latest OR Pinned, since the batch query already returns the name for free
/// and the sync piggybacks on it at zero extra API cost.</para>
/// <para>
/// <b>Name sync.</b> The same batch query returns the current Nexus mod
/// <c>name</c> for every id sent. After the tier logic, each returned name is
/// compared to the container's stored <see cref="ModContainer.Name"/> and the
/// container is renamed when they differ (the Nexus name wins; identity
/// <see cref="ModContainer.Id"/> is unchanged). The result's
/// <see cref="UpdateCheckResult.NamesChanged"/> flag signals that a check
/// renamed at least one container, so callers can refresh displayed names.</para>
/// <para>
/// <b>One query, all mods.</b> The v2 <c>modsByUid</c> query takes a batch of
/// mod UIDs (<c>game_id * 2^32 + mod_id</c>) and returns the update status for
/// each in a single call. The server tracks the user's downloads and computes
/// the signal directly.</para>
/// <para>
/// <b>Best-effort, never throws to the caller.</b> A transient API failure, a
/// missing auth config, or an exhausted rate limit all surface as an empty
/// result (with <see cref="UpdateCheckResult.RateLimited"/> set, and
/// <see cref="UpdateCheckResult.RateLimitResetsAt"/> carrying the server-reported
/// reset, when applicable) rather than a thrown exception; cancellation alone
/// propagates. The caller has nothing to gain from catching.</para>
/// </remarks>
public interface IUpdateCheckService
{
    /// <summary>
    /// The periodic check: queries the Nexus v2 GraphQL <c>modsByUid</c> batch
    /// endpoint once (one API call for all checkable mods) and flags each mod via
    /// three tiers (tier 1 <see cref="ModUpdateStatus.ViewerUpdateAvailable"/>,
    /// tier 2 a mod-level version compare, tier 3 a best-effort latest-file-version
    /// confirmation that clears tier-2-only false positives). The result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>false</c>.
    /// </summary>
    /// <param name="profileId">The profile whose mods to check.</param>
    /// <param name="ct">Cancellation token. Honored during the Nexus API call;
    /// <see cref="OperationCanceledException"/> propagates (cancellation is not
    /// a "no updates" result). Other exceptions are caught and surfaced as an
    /// empty result.</param>
    /// <returns>The check result. Never throws for non-cancellation failures:
    /// an empty result is returned instead, and <see cref="CheckCompleted"/> is
    /// raised. A <see cref="KeyNotFoundException"/> from
    /// <c>IProfileService.GetModList</c> propagates (the caller owns passing a
    /// valid profile id).</returns>
    Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The thorough check. Runs the same v2 GraphQL <c>modsByUid</c> batch
    /// query as <see cref="CheckAsync"/>; the result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>true</c>. Both paths run
    /// the same query, so the flag signals no coverage difference.
    /// </summary>
    /// <param name="profileId">The profile whose mods to check.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates; other exceptions are caught + surfaced as an empty result.</param>
    /// <returns>The check result. Never throws for non-cancellation failures.</returns>
    Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The last check result, or <c>null</c> before the first check completes.
    /// Holds the most recent result regardless of which method
    /// (<see cref="CheckAsync"/> or <see cref="CheckThoroughAsync"/>) produced
    /// it; useful for rendering update state without awaiting a check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written from the completing task and read by other threads. The write is
    /// taken under an internal lock together with the <see cref="CheckCompleted"/>
    /// invocation so an event subscriber observes the result that was just
    /// published; reads are lock-free. Reference assignment is atomic on every
    /// target runtime, so the lock-free read can at worst observe a one-check-stale
    /// value (corrected on the next <see cref="CheckCompleted"/>).</para>
    /// </remarks>
    UpdateCheckResult? LastResult { get; }

    /// <summary>
    /// Raised (on the completing thread) when a check finishes, successful or
    /// not. Always raised exactly once per <see cref="CheckAsync"/> /
    /// <see cref="CheckThoroughAsync"/> call (including the no-auth
    /// short-circuit and the rate-limited / failure paths), with the same
    /// result that was just set on <see cref="LastResult"/>.
    /// </summary>
    event EventHandler<UpdateCheckResult?>? CheckCompleted;
}

/// <summary>
/// The result of an update check: the mods with an update available, the check
/// timestamp, whether the check was rate-limited (and, when so, when the
/// exhausted window refills), whether it was the thorough path, and whether the
/// name-sync pass renamed at least one container.
/// </summary>
/// <param name="Updates">The mods with an update available (flagged by any of the
/// three tiers: tier 1 <c>viewerUpdateAvailable</c>, tier 2 a version mismatch, or
/// a tier-2-only flag tier 3 could not clear). May be empty (no updates, or the
/// check was short-circuited by no auth / no checkable mods / a rate limit / an
/// API failure).</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
/// <param name="RateLimited"><c>true</c> if the check was skipped (or aborted)
/// because the Nexus daily or hourly quota was reported exhausted. The result's
/// update set may not reflect the latest state in this case.</param>
/// <param name="Thorough"><c>true</c> if this result came from
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the manual "check now"
/// path); <c>false</c> if it came from the periodic
/// <see cref="IUpdateCheckService.CheckAsync"/>. Both paths run the same v2
/// batch query, so the flag signals no coverage difference.</param>
/// <param name="NamesChanged"><c>true</c> when the name-sync pass (which
/// piggybacks on the batch query at no extra API cost) renamed at least one
/// Nexus-sourced container to match its current Nexus mod name. Callers can
/// refresh the affected displayed names in place rather than reloading. Only the
/// normal completion path can set this <c>true</c>; the short-circuit paths (no
/// auth, no Nexus mods, rate-limited, failure) leave it <c>false</c>.</param>
/// <param name="Outcome">The authoritative outcome of the check, distinguishing a
/// real API success from the short-circuit / failure paths. <see cref="CheckOutcome.Success"/>
/// is the only outcome that may replace persisted known-update state; every other
/// outcome preserves it.</param>
    /// <param name="RateLimitResetsAt">When <paramref name="RateLimited"/> is
    /// <c>true</c>, the soonest server-reported reset of an actually-exhausted
    /// window (UTC), or <c>null</c> when the server reported a rate limit without
    /// a reset (e.g. an HTTP 429 carrying no <c>x-rl-*</c> headers). <c>null</c>
    /// whenever the check was not rate-limited.</param>
public sealed record UpdateCheckResult(
    IReadOnlyList<ModUpdateInfo> Updates,
    DateTimeOffset CheckedAt,
    bool RateLimited,
    bool Thorough,
    bool NamesChanged = false,
    CheckOutcome Outcome = CheckOutcome.Failed,
    DateTimeOffset? RateLimitResetsAt = null);

/// <summary>
/// The authoritative outcome of an update check, distinguishing a real API
/// success from the short-circuit + failure paths. Used to decide whether a
/// result may replace persisted known-update state and whether to act on it.
/// </summary>
public enum CheckOutcome
{
    /// <summary>
    /// A default for a hand-built result with no outcome set. Treated as
    /// non-authoritative (preserves prior state, starts no automatic install),
    /// matching the failure paths.
    /// </summary>
    Failed,

    /// <summary>
    /// The check queried the API + completed normally. This is the ONLY outcome
    /// that authoritatively replaces a profile's known-update state (including
    /// clearing it when the API reports no updates).
    /// </summary>
    Success,

    /// <summary>
    /// No Nexus auth is configured, so the API was never called. Prior known
    /// updates are preserved (the user may have signed out; what was flagged
    /// before is still the best knowledge).
    /// </summary>
    NoAuth,

    /// <summary>
    /// The profile has no Nexus mods at all, so the API was never called. Local
    /// state proves there can be no applicable Nexus update, so the profile's
    /// known-update state is cleared (this is the one short-circuit that clears).
    /// </summary>
    NoNexusMods,

    /// <summary>
    /// The check was rate-limited (the API call threw a rate-limit exception, or
    /// the response headers reported an exhausted window). Prior known updates are
    /// preserved.
    /// </summary>
    RateLimited,
}

/// <summary>
/// One mod flagged by an update check: the identifying fields needed to act on
/// a flagged mod.
/// </summary>
/// <param name="ContainerId">The flagged mod's container id (the join key back
/// to <see cref="ModContainer"/> and the profile entry).</param>
/// <param name="ModId">The Nexus mod id (from <see cref="NexusSource.ModId"/>).</param>
/// <param name="ModName">The flagged mod's display name (from
/// <see cref="ModContainer.Name"/>).</param>
/// <param name="CurrentVersion">The currently imported version's display tag
/// (from <see cref="ModVersion.VersionString"/>, resolved via
/// <see cref="ModContainer.ResolveVersion"/> with a <see cref="LatestPolicy"/>).</param>
/// <param name="LatestUpdateAt">The mod's last update time on Nexus (UTC),
/// from the v2 <c>updatedAt</c> field.</param>
public sealed record ModUpdateInfo(
    Guid ContainerId,
    int ModId,
    string ModName,
    string CurrentVersion,
    DateTimeOffset? LatestUpdateAt);
