using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Profile + per-profile mod-list management. Owns the profile data model,
/// its on-disk persistence, and the projection of the mod list into a staged
/// mod root (staging links to the repository's resolved version folders) +
/// <c>mods.lst</c> for Mod Relay.
/// </summary>
/// <remarks>
/// <para>
/// A profile references mods by <see cref="ModListEntry.ContainerId"/>; it never
/// stores mod files of its own. Staging resolves each enabled mod's
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/> (via
/// <see cref="IModRepository"/>) and links <c>staged/mods/&lt;name&gt;</c> to the
/// resolved version folder (an NTFS junction on Windows, a symlink on Linux).
/// <b>Staging links, never copies.</b></para>
/// </remarks>
public interface IProfileService
{
    /// <summary>
    /// Raised whenever <see cref="CreateProfile(string, string, LaunchSettings)"/>
    /// successfully persists a new profile. Carries the new profile's summary
    /// (id + name + description).
    /// </summary>
    /// <remarks>
    /// Fires from inside the create call, so a subscriber still in the call
    /// chain sees it synchronously. The hosted Profiles page awaits its DMF
    /// processing immediately after the create + activation, so the subscriber
    /// is guaranteed to exist before any create (the coordinator is resolved
    /// eagerly when the page VM is constructed).
    /// </remarks>
    event EventHandler<ProfileSummary>? ProfileCreated;

    /// <summary>
    /// Every known profile as a lightweight summary (id + name + description),
    /// sorted by name (ordinal). One unreadable profile never breaks listing.
    /// </summary>
    IReadOnlyList<ProfileSummary> ListProfiles();

    /// <summary>Loads the full profile (metadata + mod list).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    Profile GetProfile(Guid id);

    /// <summary>
    /// Creates a new profile: generates the id, scaffolds its directory tree,
    /// and persists name, description, and launch settings in a single write.
    /// </summary>
    /// <param name="name">Display name. Required (non-whitespace after trim);
    /// trimmed before persistence.</param>
    /// <param name="description">Short description. Non-null; may be empty after
    /// trim; must contain no CR or LF; at most
    /// <see cref="Profile.DescriptionMaxLength"/> characters after trim. Trimmed
    /// before persistence.</param>
    /// <param name="launchSettings">Launch settings. Non-null; validated through
    /// <see cref="LaunchSettingsValidator"/> (the single source of truth).</param>
    /// <returns>The newly-created profile.</returns>
    /// <remarks>
    /// Every input is validated and normalized before any persistence. An
    /// invalid input throws and writes nothing.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty,
    /// or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> or
    /// <paramref name="launchSettings"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="description"/> contains
    /// CR or LF, or exceeds <see cref="Profile.DescriptionMaxLength"/> after trim;
    /// or <paramref name="launchSettings"/> fails validation.</exception>
    Profile CreateProfile(string name, string description, LaunchSettings launchSettings);

    /// <summary>
    /// Atomically updates a profile's editable metadata: name, description, and
    /// launch settings in a single read-validate-write. Preserves id, creation
    /// time, mods, mod order, enabled state, and per-mod policies. This is the
    /// single editable-profile write: the hosted Profiles page routes every
    /// metadata + launch-settings edit through it.
    /// </summary>
    /// <param name="id">The profile to update.</param>
    /// <param name="name">Display name. Required (non-whitespace after trim);
    /// trimmed before persistence.</param>
    /// <param name="description">Short description. Non-null; may be empty after
    /// trim; must contain no CR or LF; at most
    /// <see cref="Profile.DescriptionMaxLength"/> characters after trim. Trimmed
    /// before persistence.</param>
    /// <param name="launchSettings">Launch settings. Non-null; validated through
    /// <see cref="LaunchSettingsValidator"/> (the single source of truth).</param>
    /// <remarks>
    /// Every input is validated and normalized before the profile is read or
    /// written, so an invalid input leaves the existing profile unchanged. The
    /// update is one write, not separate metadata + settings writes.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty,
    /// or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> or
    /// <paramref name="launchSettings"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="description"/> contains
    /// CR or LF, or exceeds <see cref="Profile.DescriptionMaxLength"/> after trim;
    /// or <paramref name="launchSettings"/> fails validation.</exception>
    void UpdateProfile(Guid id, string name, string description, LaunchSettings launchSettings);

    /// <summary>Removes the profile entry and its entire on-disk directory tree.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void DeleteProfile(Guid id);

    /// <summary>The profile's mod list (in stored order, not load order).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    IReadOnlyList<ModListEntry> GetModList(Guid id);

    /// <summary>
    /// Reassigns <see cref="ModListEntry.Order"/> so the profile's mods follow
    /// <paramref name="containerIdsInOrder"/>. Mods not mentioned keep their
    /// relative order, appended after the listed ones; ids in the list that
    /// aren't in the profile are ignored. No mods are added or removed.
    /// </summary>
    /// <remarks>
    /// An entry with <see cref="ModListEntry.OrderLocked"/> = true keeps its
    /// current zero-based load-order index: the requested ordering is projected
    /// onto the unlocked slots only, so a locked row cannot be displaced. With
    /// no locks, behavior is a plain reorder of the whole list. <see cref="ModListEntry.Order"/>
    /// values are renumbered dense 0..n-1.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder);

    /// <summary>Toggles <see cref="ModListEntry.Enabled"/> for a single mod.</summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    void SetModEnabled(Guid id, Guid containerId, bool enabled);

    /// <summary>
    /// Toggles <see cref="ModListEntry.OrderLocked"/> for a single mod. Lock
    /// metadata alone preserves order, enabled, and policy, and implies no
    /// staged-game change (the staged mod root is regenerated on the next
    /// <see cref="PrepareModRoot"/>).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    void SetModOrderLocked(Guid id, Guid containerId, bool orderLocked);

    /// <summary>
    /// Adds a mod entry with the given policy and renumbers
    /// <see cref="ModListEntry.Order"/> dense across the list. A fresh add of
    /// DMF (recognized by a deliberately small rule: the container's source is
    /// Nexus mod 8, or the content the given policy would stage resolves to
    /// the canonical lower-case <c>dmf</c> base folder containing
    /// <c>dmf.mod</c>) is inserted at rank 0 with
    /// <see cref="ModListEntry.OrderLocked"/> = true, shifting existing entries
    /// down one rank while preserving their relative order + all metadata
    /// (including lock bits); every other add is appended at the end
    /// (<see cref="ModListEntry.Enabled"/> = true,
    /// <see cref="ModListEntry.OrderLocked"/> = false). <b>List entry only:
    /// does NOT fetch or install mod files</b> (the repository holds the files;
    /// staging links to them). Idempotent: re-adding a
    /// <paramref name="containerId"/> already in the list is a strict no-op
    /// (order/enabled/policy/lock untouched), so a DMF update or re-import
    /// never overrides the user's current arrangement.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void AddMod(Guid id, Guid containerId, ModVersionPolicy policy);

    /// <summary>
    /// Changes a profile mod's <see cref="ModListEntry.Policy"/>. The new policy
    /// takes effect at the next <see cref="PrepareModRoot"/> (the resolved
    /// version folder may change; no on-disk transition is needed because the
    /// profile never stores mod files).
    /// </summary>
    /// <remarks>
    /// A <see cref="PinnedPolicy"/> is validated against the container's current
    /// versions: its <see cref="PinnedPolicy.VersionId"/> must reference a
    /// version that exists on the container (defense-in-depth against a
    /// programmatic call with a stale id; the UI's pin dropdown can only
    /// produce ids the container already holds). <see cref="LatestPolicy"/> needs
    /// no check (it resolves dynamically to the current <see cref="ModVersion.IsLatest"/>).
    /// </remarks>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="policy"/> is a
    /// <see cref="PinnedPolicy"/> whose <see cref="PinnedPolicy.VersionId"/> does
    /// not reference a version present in the container (or the container itself
    /// is missing).</exception>
    void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy);

    /// <summary>
    /// Removes the mod entry (locked or unlocked), then renumbers survivor
    /// <see cref="ModListEntry.Order"/> dense 0..n-1; the new survivor indices
    /// are the new baseline for surviving locks. The repository copy is
    /// <b>not</b> touched (other profiles may still reference it; the startup
    /// prune reclaims it when no profile does).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    void RemoveMod(Guid id, Guid containerId);

    /// <summary>
    /// Pre-checks a base-name collision for the add flow: returns the profile mod
    /// (if any) whose resolved base folder name matches <paramref name="baseName"/>,
    /// excluding <paramref name="excludeContainerId"/> (a re-add of a mod already
    /// in the profile). Used to REFUSE an import that would stage two mods under
    /// the same folder name (the mod loader can't tell them apart).
    /// </summary>
    /// <param name="id">The profile to check.</param>
    /// <param name="baseName">The candidate base folder name (peeked via
    /// <c>IModImportService.GetBaseName</c>).</param>
    /// <param name="excludeContainerId">A container id to skip (the container the
    /// import would dedup to, from
    /// <c>IModImportService.FindExistingContainer</c>); pass <c>null</c> for a
    /// brand-new container.</param>
    /// <returns>The colliding <see cref="ModListEntry"/>, or <c>null</c> if no
    /// profile mod (other than the excluded one) resolves to
    /// <paramref name="baseName"/>.</returns>
    /// <remarks>
    /// Considers <b>all</b> profile mods (enabled <em>and</em> disabled): a
    /// disabled colliding mod could be enabled later. A mod whose base name can't
    /// be resolved (missing container/version, or a corrupted version folder with
    /// zero/multiple subdirs) is skipped silently; it can't collide. Pure query:
    /// no logging, no side effects (the caller decides what to do with a hit).
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is
    /// unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is null,
    /// empty, or whitespace.</exception>
    ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId);

    /// <summary>
    /// The profile's launch settings (environment variables + game arguments).
    /// A focused read (no full profile + mod-list load) used by the launch path;
    /// the hosted Profiles page edits launch settings through the atomic
    /// <see cref="UpdateProfile"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    LaunchSettings GetLaunchSettings(Guid id);

    /// <summary>
    /// Regenerates the profile's staged mod root (the <c>--mod-path</c>) from the
    /// current per-mod version resolution, and writes <c>mods.lst</c> from the
    /// successfully-staged enabled mods in <see cref="ModListEntry.Order"/>.
    /// Idempotent (each call clears + rebuilds <c>staged/</c>). Returns the
    /// <c>--mod-path</c> to pass to the Relay launcher.
    /// </summary>
    /// <remarks>
    /// Staging links, not copies (the repository holds the files). A staging-link
    /// creation failure (e.g. Windows on a non-NTFS volume, or no write access to
    /// the profile's <c>staged/</c> directory) propagates the raised built-in
    /// exception as-is: <see cref="System.ComponentModel.Win32Exception"/> from
    /// the NTFS junction path on Windows, <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> from the symlink path on Linux.
    /// It never silently copies. A mod whose container or resolved version is
    /// missing is skipped with a warning (not a crash); it has no entry in
    /// <c>staged/</c> or <c>mods.lst</c>.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    /// <exception cref="IOException">A staging link could not be created.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">A junction could
    /// not be created on Windows.</exception>
    string PrepareModRoot(Guid id);
}
