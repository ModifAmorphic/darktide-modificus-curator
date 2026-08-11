namespace Modificus.Curator.General;

/// <summary>
/// Persists non-critical runtime application state: values that capture "where
/// the app left off" (first-run onboarding done, last-selected profile, last
/// update-check timestamp, persisted known-update snapshots) rather than user
/// system settings. Backed by a small JSON file kept under the app-data dir,
/// separate from <see cref="Config.CuratorConfig"/> (which holds system settings
/// only).
/// </summary>
/// <remarks>
/// <para><b>First-run safe:</b> a missing or corrupt state file never throws;
/// reads just return <c>null</c> (or <c>false</c> for
/// <see cref="OnboardingCompleted"/>). Writes are best-effort; runtime
/// app-state is non-critical, so a persistence failure (unwritable dir, full
/// disk) is swallowed rather than crashing the app mid-interaction.</para>
/// </remarks>
public interface IAppStateStore
{
    /// <summary>
    /// Whether the first-run Welcome onboarding has already been shown + the
    /// user made a choice. <c>false</c> until the user completes the Welcome
    /// dialog, then persisted as <c>true</c>. Reading returns the persisted value
    /// (or <c>false</c> on first run / corrupt file); assigning persists
    /// immediately.
    /// </summary>
    bool OnboardingCompleted { get; set; }

    /// <summary>
    /// The last-chosen active profile id, or <c>null</c> when none is recorded.
    /// Reading returns the persisted value (or <c>null</c> on first run /
    /// corrupt file); assigning persists the value immediately.
    /// </summary>
    Guid? ActiveProfileId { get; set; }

    /// <summary>
    /// The UTC timestamp of the last update check that fired (any trigger), or
    /// <c>null</c> when none has been recorded. Reading returns the persisted
    /// value (or <c>null</c> on first run / corrupt file); assigning persists
    /// immediately. Seeds an interval gate so a rapid open/close loop cannot burn
    /// an API call per launch.
    /// </summary>
    DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// The timestamps of successful manual "check now" refreshes within the
    /// rolling 1-hour throttle window, or <c>null</c> when none are recorded.
    /// Reading returns the persisted list (or <c>null</c> on first run / corrupt
    /// file); assigning persists immediately. Carries the manual throttle's
    /// sliding window across restarts.
    /// </summary>
    IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }

    /// <summary>
    /// Profile-scoped "known update available" snapshots, keyed by profile id, or
    /// <c>null</c> when none are recorded. Reading returns the persisted map (or
    /// <c>null</c> on first run / corrupt file); assigning persists immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw key-value storage for the Integrations <c>IUpdateStateStore</c>, which
    /// owns the domain rules over it (which outcomes replace, which preserve, how
    /// entries self-heal on hydration). Each profile's entry list is replaced
    /// wholesale on a write (the store does not merge at this granularity).</para>
    /// <para>
    /// <b>First-run + upgrade safe.</b> An old <c>app-state.json</c> written
    /// before this field existed deserializes it as <c>null</c> (System.Text.Json
    /// default for an absent nullable member), so a first run after upgrade sees
    /// no persisted snapshots + the next check seeds them. Existing fields
    /// (<see cref="ActiveProfileId"/>, <see cref="LastUpdateCheckUtc"/>,
    /// <see cref="ManualRefreshTimestamps"/>) are preserved on every write: the
    /// whole cached model is rewritten on each assignment.</para>
    /// </remarks>
    IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; }

    /// <summary>
    /// The UTC timestamp of the last Nexus display-metadata backfill pass that
    /// attempted at least one API request, or <c>null</c> when none has been
    /// recorded. Reading returns the persisted value (or <c>null</c> on first
    /// run / corrupt file); assigning persists immediately. Seeds a 24-hour gate
    /// so the backfill service runs at most one real pass per day regardless of
    /// how often the UI invokes it.
    /// </summary>
    /// <remarks>
    /// Backward compatible on disk: an old <c>app-state.json</c> written before
    /// this field existed deserializes it as <c>null</c> (System.Text.Json
    /// default for an absent nullable member), so the first run after upgrade
    /// proceeds normally. Sibling fields are preserved on every write: the whole
    /// cached model is rewritten on each assignment.
    /// </remarks>
    DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }
}
