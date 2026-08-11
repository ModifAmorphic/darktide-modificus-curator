using System.Collections.ObjectModel;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Backfills missing Nexus display metadata (summary, thumbnail URL, adult-
/// content flag) for repository containers that have none yet, using the stable
/// Nexus v1 <c>mods/{id}.json</c> endpoint. The service owns the cap, gate, and
/// failure policy; the caller supplies only the active-profile container ids as
/// priority order and a cancellation token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Missing-only.</b> A container is a candidate only when its
/// <see cref="ModContainer.DisplayMetadata"/> is <c>null</c> (Curator has never
/// retrieved display metadata for it). A non-null object, even an empty one, is
/// authoritative + skipped. This keeps the backfill from re-fetching containers
/// the acquisition path or a prior backfill already enriched, and bounds the
/// pass to genuinely-unknown containers.</para>
/// <para>
/// <b>Nexus-only.</b> Only <see cref="NexusSource"/> containers carry a Nexus
/// mod id to query; untracked and linked containers are skipped.</para>
/// <para>
/// <b>Sequential, capped, gated.</b> One <c>GetModInfoAsync</c> call per
/// candidate, at most 25 attempted calls per pass, at most one real pass per
/// 24-hour window (persisted). Active-profile ids are tried first (in caller
/// order), then the remaining repository containers in deterministic Guid
/// order.</para>
/// <para>
/// <b>Best-effort, never throws (except cancellation).</b> Auth failure, rate
/// limiting, per-mod API errors, transport/repository/config failures, and any
/// other unexpected error are all absorbed into an empty or partial result (the
/// caller fires-and-forgets). Cancellation propagates so a cancelled pass is not
/// misreported as success. Existing metadata is never cleared or overwritten on
/// any failure path: persistence goes through
/// <see cref="IModRepository.TryInitializeDisplayMetadata"/>, which is atomic
/// and missing-only.</para>
/// </remarks>
public interface INexusModMetadataService
{
    /// <summary>
    /// Runs one backfill pass: resolves the candidate sequence (active-profile
    /// ids first, then the remaining Nexus containers missing display metadata),
    /// fetches each via the stable v1 <c>GetModInfoAsync</c>, maps through the
    /// shared <c>ModDisplayMetadataMapper</c>, and persists the result through
    /// <see cref="IModRepository.TryInitializeDisplayMetadata"/>.
    /// </summary>
    /// <param name="priorityContainerIds">The active profile's container ids, in
    /// the order they should be tried first. Must not be <c>null</c>. Duplicates
    /// are de-duplicated (first occurrence wins). Non-Nexus containers and
    /// containers that already carry display metadata are skipped without
    /// consuming an attempt.</param>
    /// <param name="ct">Cancellation token. Honored throughout;
    /// <see cref="OperationCanceledException"/> propagates (cancellation is not
    /// a success). A pass cancelled after at least one attempted request still
    /// stamps the 24-hour gate.</param>
    /// <returns>The pass result. Never throws for non-cancellation failures: a
    /// short-circuit (no auth, within the gate window, no candidates) returns an
    /// empty result without stamping; a real pass always stamps the gate, even
    /// when it ends early. A container appears in the result's
    /// <see cref="NexusModMetadataResult.Updated"/> map only when
    /// <see cref="IModRepository.TryInitializeDisplayMetadata"/> returned
    /// <c>true</c> for it.</returns>
    Task<NexusModMetadataResult> BackfillMissingAsync(
        IReadOnlyList<Guid> priorityContainerIds,
        CancellationToken ct = default);
}

/// <summary>
/// The immutable result of one metadata-backfill pass. The
/// <see cref="Updated"/> map is a defensive <see cref="ReadOnlyDictionary{TKey, TValue}"/>
/// copy: a caller cannot mutate the service's internal state through it, and a
/// mutable input to the constructor is copied so the result is isolated from
/// later mutations.
/// </summary>
public sealed class NexusModMetadataResult
{
    /// <summary>
    /// The containers whose display metadata was successfully initialized
    /// during this pass, keyed by container id. A container appears here only
    /// when <see cref="IModRepository.TryInitializeDisplayMetadata"/> returned
    /// <c>true</c> for it (the metadata was <c>null</c> at the atomic
    /// check-and-set, and the write + persist succeeded). May be empty (no
    /// candidates, all skipped, or the pass ended before any successful write).
    /// </summary>
    public IReadOnlyDictionary<Guid, ModDisplayMetadata> Updated { get; }

    /// <summary>
    /// The number of <c>GetModInfoAsync</c> calls made during this pass. Skips
    /// (non-Nexus, already-has-metadata, missing container) do not count. A pass
    /// that stops after the cap carries 25 here.
    /// </summary>
    public int AttemptedCount { get; }

    /// <summary>
    /// <c>true</c> if the pass stopped because the Nexus daily or hourly quota
    /// was reported exhausted (either a <see cref="NexusRateLimitException"/> or
    /// a successful response whose headers reported an exhausted window). The
    /// <see cref="Updated"/> map may still carry results from before the stop;
    /// the metadata from the triggering response (when applicable) is persisted
    /// before the stop.
    /// </summary>
    public bool RateLimited { get; }

    /// <summary>
    /// When <see cref="RateLimited"/> is <c>true</c>, the soonest server-reported
    /// reset of an exhausted window (UTC), or <c>null</c> when the server
    /// reported a rate limit without a usable reset (e.g. an HTTP 429 carrying
    /// all-zero headers). <c>null</c> whenever the pass was not rate-limited.
    /// </summary>
    public DateTimeOffset? RateLimitResetsAt { get; }

    /// <summary>
    /// Constructs an immutable result. The <paramref name="updated"/> dictionary
    /// is defensively wrapped in a <see cref="ReadOnlyDictionary{TKey, TValue}"/>
    /// over a copy of its key-value pairs, so neither a later mutation of the
    /// input nor a downcast to a mutable dictionary can change the result.
    /// </summary>
    public NexusModMetadataResult(
        IReadOnlyDictionary<Guid, ModDisplayMetadata> updated,
        int attemptedCount,
        bool rateLimited,
        DateTimeOffset? rateLimitResetsAt)
    {
        ArgumentNullException.ThrowIfNull(updated);
        Updated = new ReadOnlyDictionary<Guid, ModDisplayMetadata>(
            new Dictionary<Guid, ModDisplayMetadata>(updated));
        AttemptedCount = attemptedCount;
        RateLimited = rateLimited;
        RateLimitResetsAt = rateLimitResetsAt;
    }

    /// <summary>
    /// A shared empty non-rate-limited result (zero attempts, no updates). Safe
    /// to return from any short-circuit path because <see cref="Updated"/> is a
    /// fresh empty <see cref="ReadOnlyDictionary{TKey, TValue}"/> that cannot be
    /// mutated.
    /// </summary>
    public static NexusModMetadataResult Empty { get; } = new(
        new Dictionary<Guid, ModDisplayMetadata>(),
        attemptedCount: 0,
        rateLimited: false,
        rateLimitResetsAt: null);
}
