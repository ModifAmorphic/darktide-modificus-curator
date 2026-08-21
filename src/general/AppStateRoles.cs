namespace Modificus.Curator.General;

// The role interfaces over persisted runtime application state: values that
// capture "where the app left off" rather than user system settings. One
// JSON-backed implementation (AppStateStore) covers them all; each consumer
// depends only on the slice it uses. See docs/reference/general.md.

/// <summary>
/// The first-run onboarding flag: whether the Welcome flow has been shown +
/// a choice made. Reading returns the persisted value (or <c>false</c> on a
/// first run / corrupt state file); assigning persists immediately.
/// </summary>
public interface IOnboardingState
{
    /// <summary>
    /// <c>false</c> until the user completes the Welcome dialog, then persisted
    /// as <c>true</c>.
    /// </summary>
    bool OnboardingCompleted { get; set; }
}

/// <summary>
/// The last-chosen active profile id, persisted across restarts. Reading
/// returns the persisted value (or <c>null</c> on a first run / corrupt state
/// file); assigning persists the value immediately.
/// </summary>
public interface IProfileActivationState
{
    /// <summary>The last-chosen active profile id, or <c>null</c> when none is
    /// recorded.</summary>
    Guid? ActiveProfileId { get; set; }
}

/// <summary>
/// The persisted schedule state for the Nexus update check: the shared
/// interval-gate timestamp + the manual "check now" throttle's sliding window.
/// Both survive a close/reopen so a rapid open/close loop or profile switch
/// cannot burn an API call per launch.
/// </summary>
public interface IUpdateCheckScheduleState
{
    /// <summary>
    /// The UTC timestamp of the last update check that fired (any trigger), or
    /// <c>null</c> when none has been recorded. Seeds the interval gate.
    /// </summary>
    DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// The timestamps of successful manual "check now" refreshes within the
    /// rolling 1-hour throttle window, or <c>null</c> when none are recorded.
    /// </summary>
    IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
}

/// <summary>
/// Raw profile-scoped "known update available" snapshots, keyed by profile id.
/// The Integrations <c>IUpdateStateStore</c> owns the domain rules over this
/// storage (which outcomes replace, which preserve, how entries self-heal on
/// hydration); each profile's entry list is replaced wholesale on a write.
/// </summary>
public interface IKnownUpdateState
{
    /// <summary>
    /// The per-profile snapshot lists, or <c>null</c> when none are recorded.
    /// </summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; }
}

/// <summary>
/// The UTC timestamp of the last Nexus display-metadata backfill pass that
/// attempted at least one API request, seeding a 24-hour gate so the backfill
/// runs at most one real pass per day regardless of how often the UI invokes
/// it.
/// </summary>
public interface INexusMetadataBackfillState
{
    /// <summary>
    /// The last backfill pass timestamp, or <c>null</c> when none has been
    /// recorded.
    /// </summary>
    DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }
}

/// <summary>
/// The persisted main-window geometry: the last valid Normal client size in
/// DIP plus whether the last meaningful state was Maximized, written atomically
/// (width, height, and the flag always land together, never a partial triple).
/// The UI layer owns the meaning + the lifetime policy over it.
/// </summary>
public interface IMainWindowStatePersistence
{
    /// <summary>
    /// The persisted window geometry record, or <c>null</c> when none has been
    /// recorded.
    /// </summary>
    AppWindowState? MainWindowState { get; set; }
}

/// <summary>
/// The persisted receipts for foreign game-dir <c>mods</c> entries renamed
/// aside with user consent. Audit data only: assignment replaces the whole
/// recorded list.
/// </summary>
public interface IRenamedModsFoldersState
{
    /// <summary>
    /// The recorded receipts, or <c>null</c> when none are recorded.
    /// </summary>
    IReadOnlyList<RenamedModsFolder>? RenamedModsFolders { get; set; }
}
