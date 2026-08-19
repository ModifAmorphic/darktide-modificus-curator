using System.Text;
using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Filesystem-backed <see cref="IProfileService"/>. Each profile lives under
/// <c>&lt;ProfilesBaseFolder&gt;/&lt;guid&gt;/</c> with this layout:
/// </summary>
/// <remarks>
/// <code>
/// &lt;ProfilesBaseFolder&gt;/          (auto-created on first run)
///   &lt;guid&gt;/                        (profile dir; id-named)
///     profile.json                   (metadata + mod list - the source of truth)
///     staged/                        (the staged mod root = the --mod-path;
///                                     REGENERATED each launch - a projection)
///       mods/                        (the mod host folder Relay consumes)
///         &lt;baseName&gt;               (staging link -> &lt;versionFolder&gt;/&lt;baseName&gt;/)
///         mods.lst                   (successfully-staged enabled mods, in order)
///         .curator.json              (Curator's staging ownership marker)
/// </code>
/// <para>
/// A profile references mods by <see cref="ModListEntry.ContainerId"/>; it stores
/// no mod files. Staging resolves each enabled mod's
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/> (via
/// <see cref="IModRepository"/>), discovers the mod's base folder inside the
/// resolved version folder, and links <c>staged/mods/&lt;baseName&gt;</c> to
/// <c>&lt;versionFolder&gt;/&lt;baseName&gt;/</c>. <b>The base name (not the
/// container's display name) is the link + mods.lst name</b>: mods bake their
/// folder name into their code, so the link must carry the base name for the
/// mod's hardcoded paths to resolve. Staging is a simple loop: base-name
/// collisions are blocked at import time (<see cref="GetBaseNameCollision"/>),
/// so staging never sees two mods with the same base folder name in normal use.
/// <b>Staging links, never copies.</b> The repository holds the files;
/// <c>staged/</c> is a staging-link projection (an NTFS junction on Windows, a
/// symlink on Linux).</para>
/// <para>
/// Registered as a singleton: the service holds no per-request state (all state
/// lives on disk). The profiles base folder is read live from
/// <see cref="IConfigLoader"/>.<see cref="IConfigLoader.Load"/> on each public
/// operation (one snapshot per op), so a runtime folder change via the Settings
/// window takes effect immediately. <see cref="Directory.CreateDirectory"/>
/// runs per-op (idempotent) on the live path. Concurrent writes to the same
/// profile are not coordinated (single-UI-thread assumption).</para>
/// </remarks>
internal sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // mods.lst is UTF-8 without BOM (the Lua loader reads it line-by-line; a
    // BOM would surface as a stray prefix on the first mod name).
    private static readonly Encoding ModListEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IConfigLoader _configLoader;
    private readonly IModRepository _repo;
    private readonly StagingLinkCreator _createLink;
    private readonly ILogger<ProfileService> _logger;

    /// <inheritdoc />
    public event EventHandler<ProfileSummary>? ProfileCreated;

    public ProfileService(
        IConfigLoader configLoader,
        IModRepository repo,
        StagingLinkCreator createLink,
        ILogger<ProfileService> logger)
    {
        _configLoader = configLoader;
        _repo = repo;
        _createLink = createLink;
        _logger = logger;
    }

    /// <summary>
    /// Reads the profiles base folder from the live config snapshot and ensures
    /// it exists. Called at the top of each public operation so a runtime folder
    /// change takes effect immediately (the directory is created on the live
    /// path, and subsequent path helpers derive from it).
    /// </summary>
    private string EnsureBaseFolder()
    {
        var baseFolder = _configLoader.Load().ProfilesBaseFolder;
        // ProfilesBaseFolder is non-null by CuratorConfig contract (defaults to
        // <app-data>/profiles). Directory.CreateDirectory is idempotent, so this
        // makes every subsequent op first-run safe without each re-checking.
        Directory.CreateDirectory(baseFolder);
        return baseFolder;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfileSummary> ListProfiles()
    {
        var baseFolder = EnsureBaseFolder();
        var summaries = new List<ProfileSummary>();
        foreach (var dir in Directory.EnumerateDirectories(baseFolder))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var id))
            {
                _logger.LogDebug("Skipping non-profile directory under profiles base: {Dir}", dir);
                continue;
            }

            try
            {
                var profile = ReadProfileFile(dir);
                summaries.Add(new ProfileSummary(id, profile.Name, profile.Description));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A single unreadable profile must not break listing the rest.
                _logger.LogWarning(ex, "Skipping unreadable profile at {Dir}", dir);
            }
        }

        // Predictable order for the UI profile picker: sort by Name, ordinal
        // (stable, so equal names keep enumeration order).
        return summaries.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public Profile GetProfile(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        return ReadProfileFile(ProfileDir(baseFolder, id)); // throws KeyNotFoundException via EnsureReadable
    }

    /// <inheritdoc />
    public Profile CreateProfile(string name, string description, LaunchSettings launchSettings)
    {
        // Validate + normalize every input before any filesystem touch, so an
        // invalid input writes nothing (no partial profile directory or json).
        ArgumentNullException.ThrowIfNull(launchSettings);
        var (normalizedName, normalizedDescription) = ValidateAndNormalizeProfileMetadata(name, description);
        ValidateLaunchSettings(launchSettings);

        var baseFolder = EnsureBaseFolder();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Description = normalizedDescription,
            CreatedAt = DateTimeOffset.UtcNow,
            Mods = Array.Empty<ModListEntry>(),
            LaunchSettings = launchSettings,
        };

        // Scaffold the profile dir + staged/ before persisting so a crash between
        // the two never leaves a profile.json without its tree. staged/ is
        // regenerated each PrepareModRoot.
        Directory.CreateDirectory(ProfileDir(baseFolder, profile.Id));
        Directory.CreateDirectory(StagedDir(baseFolder, profile.Id));
        WriteProfileFile(profile, baseFolder);

        _logger.LogInformation("Created profile {Id} ('{Name}')", profile.Id, profile.Name);

        // Notify subscribers (the DMF new-profile prompt coordinator). Raised
        // AFTER the persist committed so a subscriber that reads the profile
        // back sees it. Raised synchronously; the hosted Profiles page, which
        // drives the create, resolves the coordinator before any create can run
        // and awaits its processing immediately after the create + activation.
        ProfileCreated?.Invoke(this, new ProfileSummary(profile.Id, profile.Name, profile.Description));

        return profile;
    }

    /// <inheritdoc />
    public void UpdateProfile(Guid id, string name, string description, LaunchSettings launchSettings)
    {
        // Validate + normalize every input before reading or writing, so an
        // invalid input leaves the existing profile file unchanged. One read +
        // one write: Id/CreatedAt/Mods/order/enabled/policies are preserved by
        // mutating only Name/Description/LaunchSettings on the loaded aggregate.
        ArgumentNullException.ThrowIfNull(launchSettings);
        var (normalizedName, normalizedDescription) = ValidateAndNormalizeProfileMetadata(name, description);
        ValidateLaunchSettings(launchSettings);

        var baseFolder = EnsureBaseFolder();
        // ReadProfileFile throws KeyNotFoundException via EnsureReadable when the
        // profile is unknown (the caller's contract).
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var previousName = profile.Name;
        profile.Name = normalizedName;
        profile.Description = normalizedDescription;
        profile.LaunchSettings = launchSettings;
        WriteProfileFile(profile, baseFolder);

        _logger.LogInformation(
            "Updated profile {Id} ('{Previous}' -> '{Name}')", id, previousName, profile.Name);
    }

    /// <inheritdoc />
    public void DeleteProfile(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        var dir = ProfileDir(baseFolder, id);
        if (!Directory.Exists(dir))
        {
            throw UnknownProfile(id);
        }

        // staged/ holds staging links (junctions on Windows). A recursive
        // Directory.Delete can't remove a directory junction (the BCL throws
        // UnauthorizedAccessException on the reparse point) and must never follow
        // one into the repository anyway. Clear staged/ reparse-awarely first, so
        // the tree below is link-free before the recursive delete removes it.
        ClearStagedDir(StagedDir(baseFolder, id));
        Directory.Delete(dir, recursive: true);
        _logger.LogInformation("Deleted profile {Id}", id);
    }

    /// <inheritdoc />
    public IReadOnlyList<ModListEntry> GetModList(Guid id) => GetProfile(id).Mods;

    /// <inheritdoc />
    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
    {
        ArgumentNullException.ThrowIfNull(containerIdsInOrder);
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var current = profile.Mods;

        // Canonical visible/load order: a stable sort of the current entries by
        // Order. The zero-based index in this canonical list is what each locked
        // entry reserves; OrderBy is stable, so equal Orders keep storage order.
        var canonical = current.OrderBy(m => m.Order).ToList();

        // Reserved slots: each locked entry holds its current canonical index;
        // the requested ordering cannot displace it.
        var reserved = new Dictionary<int, ModListEntry>();
        for (var i = 0; i < canonical.Count; i++)
        {
            if (canonical[i].OrderLocked)
            {
                reserved[i] = canonical[i];
            }
        }

        // Derive the caller's desired ordering exactly as before: index the
        // request by containerId (first occurrence wins for dupes, Guid.Empty
        // ignored), then stable-sort canonical so listed mods come first in the
        // requested sequence and unmentioned mods follow in their current
        // relative order.
        var desiredIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < containerIdsInOrder.Count; i++)
        {
            var cid = containerIdsInOrder[i];
            if (cid != Guid.Empty && !desiredIndex.ContainsKey(cid))
            {
                desiredIndex[cid] = i;
            }
        }

        var desiredUnlocked = canonical
            .OrderBy(m => desiredIndex.TryGetValue(m.ContainerId, out var idx) ? idx : int.MaxValue)
            .Where(m => !m.OrderLocked)
            .ToList();

        // Walk each slot 0..n-1: a reserved slot takes its locked entry; an open
        // slot takes the next desired-unlocked entry in relative order. The
        // counts balance by construction (unlocked desired == open slots), so
        // the cursor never overruns. Renumber dense 0..n-1 in one pass.
        var result = new List<ModListEntry>(canonical.Count);
        var unlockedCursor = 0;
        for (var i = 0; i < canonical.Count; i++)
        {
            ModListEntry entry = reserved.TryGetValue(i, out var lockedEntry)
                ? lockedEntry
                : desiredUnlocked[unlockedCursor++];
            result.Add(entry with { Order = i });
        }

        profile.Mods = result;
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void SetModEnabled(Guid id, Guid containerId, bool enabled)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Rebuild (immutable entries): swap the matching entry for a copy with
        // the new Enabled. Write-through persists the whole aggregate.
        profile.Mods = profile.Mods
            .Select(m => m.ContainerId == containerId ? m with { Enabled = enabled } : m)
            .ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void SetModOrderLocked(Guid id, Guid containerId, bool orderLocked)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Lock metadata only: swap the matching entry for a copy with the new
        // OrderLocked. Order, Enabled, and Policy are untouched, and no staged
        // change is implied (the staged root is regenerated on the next launch).
        profile.Mods = profile.Mods
            .Select(m => m.ContainerId == containerId ? m with { OrderLocked = orderLocked } : m)
            .ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        if (containerId == Guid.Empty)
        {
            throw new ArgumentException("Container id must not be Guid.Empty.", nameof(containerId));
        }
        ArgumentNullException.ThrowIfNull(policy);

        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));

        // Idempotent: re-adding an existing container is a strict no-op (keeps
        // its order, enabled state, policy, AND lock). Prevents duplicate
        // entries from re-entrancy and is evaluated before any compaction so a
        // re-add never disturbs existing entries. This also means a DMF
        // update/re-import never overrides the user's current arrangement.
        if (profile.Mods.Any(m => m.ContainerId == containerId))
        {
            return;
        }

        // Stable sort by current Order, then insert the new entry and renumber
        // dense 0..n-1 in one pass. A fresh DMF add goes to rank 0 + locked,
        // shifting survivors down one rank with their relative order + all
        // metadata (including lock bits) intact; the shifted indexes are the
        // new structural baseline, consistent with remove compaction. Any
        // other add appends at the end unlocked. One persistence write either
        // way.
        var entries = profile.Mods.OrderBy(m => m.Order).ToList();
        var isDmf = IsDmfAdd(containerId, policy);
        entries.Insert(isDmf ? 0 : entries.Count, new ModListEntry
        {
            ContainerId = containerId,
            Enabled = true,
            OrderLocked = isDmf,
            Policy = policy,
        });
        profile.Mods = entries.Select((m, i) => m with { Order = i }).ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Defense-in-depth: a PinnedPolicy must reference a version that exists
        // in the container. The UI's pin dropdown can only produce ids the
        // container already holds, but a programmatic call (or an id held stale
        // across a repository change) must not silently create a phantom pin
        // that skips+warns at every stage. LatestPolicy needs no check: it
        // resolves dynamically to whatever the container currently marks
        // IsLatest.
        if (policy is PinnedPolicy pinned)
        {
            var container = _repo.Get(containerId);
            if (container is null || !container.Versions.Any(v => v.Folder == pinned.VersionId))
            {
                throw new ArgumentException(
                    $"No version with id '{pinned.VersionId}' exists on container '{containerId}'. " +
                    "A Pinned policy must reference a present version.",
                    nameof(policy));
            }
        }

        // Persist the new policy. Resolution happens at stage time, so there's
        // no on-disk transition (no diverged copy to reconcile).
        profile.Mods = profile.Mods
            .Select(m => m.ContainerId == containerId ? m with { Policy = policy } : m)
            .ToList();
        WriteProfileFile(profile, baseFolder);
        _logger.LogInformation("Set policy for container {Container} on profile {Id} to {Policy}", containerId, id, policy);
    }

    /// <inheritdoc />
    public void RemoveMod(Guid id, Guid containerId)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Drop the entry (locked or unlocked), then compact survivor Order dense
        // 0..n-1 (stable by current Order). A surviving locked entry keeps its
        // lock metadata; its new dense index is the new baseline for future
        // reorders, so removing a row before it lets it shift up.
        profile.Mods = profile.Mods
            .Where(m => m.ContainerId != containerId)
            .OrderBy(m => m.Order)
            .Select((m, i) => m with { Order = i })
            .ToList();
        WriteProfileFile(profile, baseFolder);

        // The repository copy is NOT touched: other profiles may still reference
        // it, and the startup prune reclaims it when no profile does.
    }

    /// <inheritdoc />
    public string PrepareModRoot(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var staged = StagedDir(baseFolder, id);

        // Regenerated each launch: clear the prior projection, then rebuild from
        // the current resolution. ClearStagedDir is symlink-aware (never follows
        // a symlink into the repository - see the method).
        ClearStagedDir(staged);
        var mods = ModsDir(staged);
        Directory.CreateDirectory(mods);

        // Resolve each enabled mod in Order; create the staging link for those
        // that resolve to a present version folder. mods.lst reflects what
        // actually got staged (a skipped mod has no entry in staged/ and must
        // not be listed - otherwise the loader would look for a mod dir that
        // isn't there).
        //
        // This is a SIMPLE loop: base-name collisions are blocked at import time
        // (the add flow calls GetBaseNameCollision), so staging never sees two
        // mods with the same base folder name in normal use. No dedupe / no
        // last-wins / no disambiguation. (A hand-edited profile.json that somehow
        // creates a duplicate base name would throw IOException here on the
        // second link - an accepted edge; no defensive logic is added.)
        var stagedNames = new List<string>();
        foreach (var mod in profile.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var (baseName, target, skipReason) = ResolveStagingTarget(mod);
            if (baseName is null || target is null)
            {
                // The mod couldn't be resolved to a stageable base folder
                // (missing container/version, missing version folder, or a
                // corrupted version folder with zero/multiple subdirs). Skip +
                // warn; it has no entry in staged/ or mods.lst.
                _logger.LogWarning(
                    "Mod {Container} on profile {Id} could not be staged ({Reason}). Skipping.",
                    mod.ContainerId, id, skipReason);
                continue;
            }

            var linkPath = Path.Combine(mods, baseName);
            _createLink(linkPath, target);
            stagedNames.Add(baseName);
            _logger.LogDebug(
                "Staged container {Container} on profile {Id} as '{Link}' -> {Target}",
                mod.ContainerId, id, baseName, target);
        }

        WriteModList(stagedNames, mods);
        WriteOwnershipMarker(mods, profile);
        _logger.LogInformation("Staged {Count} mod(s) for profile {Id} at {Path}", stagedNames.Count, id, staged);
        return staged;
    }

    // ---- staging ownership marker -------------------------------------------

    /// <summary>The marker schema version. Bump only on a breaking marker-shape
    /// change (the game-dir host treats an unreadable marker as absent).</summary>
    internal const int OwnershipMarkerSchema = 1;

    /// <summary>
    /// The persisted shape of <see cref="OwnershipMarkerFileName"/>: identifies
    /// the profile the staged tree was projected for + when. Profiles holds the
    /// profile identity and owns the staged tree, so the write lives here; the
    /// relay-client game-dir host only reads the marker back to prove a link is
    /// Curator's. App version is deliberately absent (Profiles does not know it;
    /// profile identity + timestamp carry the troubleshooting value).
    /// </summary>
    internal sealed record OwnershipMarker(int Schema, Guid ProfileId, string ProfileName, DateTimeOffset ProjectedAtUtc);

    /// <summary>
    /// Rewrites the ownership marker into the staged <c>mods/</c> each pass
    /// (the pass cleared + rebuilt the tree, so the prior marker is gone; a
    /// marker that survived would misattribute a rebuilt tree).
    /// </summary>
    private static void WriteOwnershipMarker(string mods, Profile profile)
    {
        var marker = new OwnershipMarker(OwnershipMarkerSchema, profile.Id, profile.Name, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        File.WriteAllText(Path.Combine(mods, StagingOwnership.MarkerFileName), json, ModListEncoding);
    }

    // ---- DMF fresh-add recognition ------------------------------------------

    /// <summary>
    /// The Nexus mod id of DMF (Darktide Mod Framework). Most Darktide mods
    /// depend on it loading first, so a fresh profile add of DMF is inserted at
    /// rank 0 + order-locked; the user remains free to unlock, reorder,
    /// disable, or remove it afterwards.
    /// </summary>
    private const int DmfNexusModId = 8;

    /// <summary>
    /// The canonical DMF base folder name (ordinal, lower-case) for the
    /// content-based recognition fallback; the folder must contain the matching
    /// <c>dmf.mod</c> descriptor.
    /// </summary>
    private const string DmfBaseFolderName = "dmf";

    /// <summary>
    /// Whether a fresh add of <paramref name="containerId"/> under
    /// <paramref name="policy"/> is DMF: (1) the container's source is Nexus
    /// mod <see cref="DmfNexusModId"/>, or (2) the content the policy would
    /// stage resolves to the canonical lower-case <c>dmf</c> base folder
    /// containing <c>dmf.mod</c>. Deliberately small: no name-based fuzzy
    /// matching, no persisted history. An unknown container id (or content
    /// that resolves to nothing stageable) is not DMF and follows ordinary
    /// append behavior.
    /// </summary>
    private bool IsDmfAdd(Guid containerId, ModVersionPolicy policy)
    {
        var container = _repo.Get(containerId);
        if (container is null)
        {
            return false;
        }

        if (container.Source is NexusSource { ModId: DmfNexusModId })
        {
            return true;
        }

        // Content rule: reuse the staging-target resolution (the same pure
        // resolver staging + the collision check use) so the recognized base
        // folder is exactly what a launch would stage under this policy.
        var (baseName, target, _) = ResolveStagingTarget(new ModListEntry
        {
            ContainerId = containerId,
            Enabled = true,
            Policy = policy,
        });
        return baseName == DmfBaseFolderName
            && target is not null
            && File.Exists(Path.Combine(target, DmfBaseFolderName + ".mod"));
    }

    // ---- alternate mod-manager recognition ----------------------------------

    /// <summary>
    /// The base folder name an alternate mod manager mod occupies (ordinal,
    /// lower-case literal), mirroring the Darktide Mod Loader family
    /// convention. Detection is content-based + manager-agnostic (no source or
    /// Nexus id is consulted); the folder must also contain
    /// <see cref="ModManagerFileName"/>.
    /// </summary>
    private const string ModManagerBaseFolderName = "base";

    /// <summary>
    /// The manager entry file inside a <c>base</c> folder that marks its
    /// container as an alternate mod manager (the file Relay's
    /// <c>--mod-manager</c> consumes). Its absence from the resolved target
    /// yields no manager (never a path to a missing file).
    /// </summary>
    private const string ModManagerFileName = "mod_manager.lua";

    /// <inheritdoc />
    public ActiveModManager? GetActiveModManager(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        // Throws KeyNotFoundException via EnsureReadable when the profile is
        // unknown (the caller's contract).
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));

        // Same resolver + order staging walks, so the answer matches what
        // PrepareModRoot stages. The staged path is derived, not written here:
        // it exists once the launch path's PrepareModRoot has created the link.
        // First candidate in order wins; a second cannot normally exist (the
        // base-name collision block stops two base mods), so first-wins is the
        // documented defense against a hand-shaped profile.
        foreach (var mod in profile.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var (baseName, target, _) = ResolveStagingTarget(mod);
            if (baseName == ModManagerBaseFolderName
                && target is not null
                && File.Exists(Path.Combine(target, ModManagerFileName)))
            {
                return new ActiveModManager(
                    mod.ContainerId,
                    Path.Combine(ModsDir(StagedDir(baseFolder, id)), baseName, ModManagerFileName));
            }
        }

        return null;
    }

    // ---- staging helpers ----------------------------------------------------

    /// <summary>
    /// Resolves a profile mod entry to its on-disk staging target: the mod's base
    /// folder name + the absolute staging-link target (<c>&lt;versionFolder&gt;/&lt;baseName&gt;/</c>
    /// for managed mods, or the external folder itself for a
    /// <see cref="LinkedSource"/> mod). Returns a non-null <c>SkipReason</c>
    /// (and null base name + target) when the entry can't be staged. Pure: no
    /// logging, no side effects. Shared by <see cref="PrepareModRoot"/> (staging,
    /// warns on skip) and <see cref="GetBaseNameCollision"/> (silent), so the two
    /// paths cannot drift.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a managed mod the base name is <b>not stored</b>; it is derived from
    /// the validated on-disk structure (the single subdirectory inside the
    /// version folder, which the import validation guarantees). Mods bake their
    /// folder name into their code, so the staging link MUST carry the base name
    /// (not the container's display name) for the mod's hardcoded paths to
    /// resolve. A version folder with zero or multiple subdirs (corrupted /
    /// legacy data predating the import validation) can't yield a base name and
    /// is skipped.</para>
    /// <para>
    /// For a <see cref="LinkedSource"/> mod the target IS the external folder
    /// (Curator stages it in place; no version resolution, no copy). The base
    /// name is the external folder's own name (Curator never renames it). A
    /// missing/unreadable external folder is skipped with reason "external
    /// folder unavailable" (no fallback copy is created).</para>
    /// </remarks>
    private (string? BaseName, string? Target, string? SkipReason) ResolveStagingTarget(ModListEntry mod)
    {
        var container = _repo.Get(mod.ContainerId);
        if (container is null)
        {
            return (null, null, "container not found");
        }

        // Linked: stage directly from the external folder. Curator does not
        // version, rename, or copy the target; the staging link writes
        // staged/mods/<baseName> -> <externalPath>. The base name is the folder's
        // own name, matching what staging writes. ResolveVersion is never
        // called for linked (a linked container has no versions). Because
        // GetBaseNameCollision calls this method, the linked base-name
        // resolution drives the collision check for free.
        if (container.Source is LinkedSource linked)
        {
            var external = linked.ExternalPath;
            if (!Directory.Exists(external))
            {
                return (null, null, "external folder unavailable");
            }

            // Trim trailing separators so a path stored with a trailing slash
            // still yields its folder name (ExternalPath is normalized at link
            // time, so this is defensive only).
            var linkedBaseName = Path.GetFileName(
                external.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(linkedBaseName))
            {
                return (null, null, "external folder has no base name");
            }

            return (linkedBaseName, external, null);
        }

        var version = container.ResolveVersion(mod.Policy);
        if (version is null)
        {
            return (null, null, $"no version resolves for policy {mod.Policy}");
        }

        var versionFolder = _repo.GetVersionFolderPath(mod.ContainerId, version.Folder);
        if (!Directory.Exists(versionFolder))
        {
            // Defensive: the manifest points at a folder that is not on disk
            // (a hand-delete between prune + stage).
            return (null, null, $"version folder {version.Folder} is missing on disk");
        }

        // Discover the mod's base folder: the import validation guarantees the
        // version folder contains exactly one subdirectory (the base, named to
        // match its <base>.mod descriptor). A corrupted/inconsistent version
        // folder (zero/multiple subdirs) can't yield a base name.
        string[] baseDirs;
        try
        {
            baseDirs = Directory.GetDirectories(versionFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, $"version folder {version.Folder} is not readable");
        }

        if (baseDirs.Length != 1)
        {
            return (null, null,
                $"version folder {version.Folder} has {baseDirs.Length} subdirectories; expected exactly one base folder");
        }

        var baseName = Path.GetFileName(baseDirs[0]);
        // The staging-link target is the base folder inside the version folder:
        // <versionFolder>/<baseName>/.
        return (baseName, Path.Combine(versionFolder, baseName), null);
    }

    /// <inheritdoc />
    public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("Base name must not be null or whitespace.", nameof(baseName));
        }

        // Throws KeyNotFoundException via GetProfile if the profile is unknown.
        var profile = GetProfile(id);

        // Consider ALL mods (enabled + disabled): a disabled colliding mod could
        // be enabled later. excludeContainerId skips a re-add of the same
        // container (AddMod is idempotent on it, so a re-add is a no-op, not a
        // collision). A mod whose base name can't be resolved (missing
        // container/version/corrupted folder) is skipped silently: it can't
        // collide. Base-name comparison is ordinal (folder names are case-sensitive
        // on Linux; an ordinal match is the conservative choice cross-platform).
        foreach (var mod in profile.Mods)
        {
            if (excludeContainerId is Guid exclude && mod.ContainerId == exclude)
            {
                continue;
            }

            var (resolved, _, _) = ResolveStagingTarget(mod);
            if (resolved is not null && string.Equals(resolved, baseName, StringComparison.Ordinal))
            {
                return mod;
            }
        }
        return null;
    }

    // ---- launch settings ----------------------------------------------------

    /// <inheritdoc />
    public string ProfilesRoot => EnsureBaseFolder();

    /// <inheritdoc />
    public LaunchSettings GetLaunchSettings(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        // ReadProfileFile throws KeyNotFoundException via EnsureReadable when the
        // profile is unknown, and coerces a null LaunchSettings to empty.
        return ReadProfileFile(ProfileDir(baseFolder, id)).LaunchSettings;
    }

    /// <summary>
    /// Validates and normalizes the name + description shared by
    /// <see cref="CreateProfile(string, string, LaunchSettings)"/> and
    /// <see cref="UpdateProfile"/>. Returns the trimmed values. Throws before any
    /// filesystem touch on violation so callers can perform all validation up
    /// front and leave nothing partially persisted.
    /// </summary>
    /// <remarks>
    /// Name follows the existing service posture (reject null/empty/whitespace
    /// as <see cref="ArgumentException"/>). Description is non-null at the
    /// boundary (<see cref="ArgumentNullException"/>), rejects CR/LF anywhere
    /// (single-line invariant), is trimmed, and is capped at
    /// <see cref="Profile.DescriptionMaxLength"/> characters after trim; empty
    /// after trim is allowed.
    /// </remarks>
    private static (string Name, string Description) ValidateAndNormalizeProfileMetadata(
        string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name must not be null or whitespace.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(description);

        // Single-line invariant: reject CR/LF anywhere in the raw value (not just
        // after trim). A description that trims to empty but carried a newline is
        // still rejected, since any embedded line break breaks the single-line UI.
        if (description.IndexOf('\r') >= 0 || description.IndexOf('\n') >= 0)
        {
            throw new ArgumentException(
                "Profile description must not contain carriage return or line feed characters.",
                nameof(description));
        }

        var trimmedDescription = description.Trim();
        if (trimmedDescription.Length > Profile.DescriptionMaxLength)
        {
            throw new ArgumentException(
                $"Profile description must be at most {Profile.DescriptionMaxLength} characters after trim.",
                nameof(description));
        }

        return (name.Trim(), trimmedDescription);
    }

    /// <summary>
    /// Validates a <see cref="LaunchSettings"/> by delegating to the shared
    /// <see cref="LaunchSettingsValidator"/> (the single source of truth, shared
    /// with the launch-settings UI). Throws <see cref="ArgumentException"/> on
    /// the first violation with a clear, developer-facing (English) message
    /// identifying the offending entry; the UI surfaces a localized error and
    /// keeps the modal open, while this is the authoritative check at the trust
    /// boundary.
    /// </summary>
    /// <remarks>
    /// The structured errors carry no localization (the Profiles library is
    /// backend-only); the per-kind messages here are developer-facing only. The
    /// <see cref="LaunchSettingsValidationError.Name"/> is echoed in the message
    /// so a log reader can diagnose. Per-entry precedence (empty -> invalid ->
    /// reserved -> duplicate -> value-NUL) is owned by the shared validator.
    /// </remarks>
    private static void ValidateLaunchSettings(LaunchSettings settings)
    {
        var errors = LaunchSettingsValidator.Validate(settings);
        if (errors.Count == 0)
        {
            return;
        }

        // The validator reports one error per offending entry in entry order;
        // throw on the first (matches the prior throw-on-first-violation
        // behavior the service exposed).
        var first = errors[0];
        throw new ArgumentException(ValidationMessage(first), nameof(settings));
    }

    /// <summary>
    /// Maps a structured validation error to the developer-facing message the
    /// service surfaces in its <see cref="ArgumentException"/>. Echoes the
    /// offending name where relevant; never localized.
    /// </summary>
    private static string ValidationMessage(LaunchSettingsValidationError error) => error.Kind switch
    {
        LaunchSettingsValidationErrorKind.NameEmpty =>
            $"Environment variable at position {error.Index} has an empty name.",
        LaunchSettingsValidationErrorKind.NameInvalid =>
            $"Environment variable name '{error.Name}' must not contain '=' or a NUL character.",
        LaunchSettingsValidationErrorKind.NameReserved =>
            $"Environment variable name '{error.Name}' is reserved and cannot be set on a profile.",
        LaunchSettingsValidationErrorKind.NameDuplicate =>
            $"Duplicate environment variable name '{error.Name}' (names are case-insensitive).",
        LaunchSettingsValidationErrorKind.ValueNul =>
            $"Environment variable value for '{error.Name}' must not contain a NUL character.",
        _ => $"Environment variable at position {error.Index} is invalid.",
    };

    /// <summary>
    /// Clears <c>staged/</c> for a rebuild: <b>symlink-aware</b>. It removes
    /// each top-level entry via <see cref="DeleteStagedEntry"/>, which deletes
    /// symlinks as links (never following them into the repository). This is
    /// data-safety-critical: a naive
    /// <c>Directory.Delete(staged, recursive: true)</c> could follow a directory
    /// symlink and delete the repository's mod files.
    /// </summary>
    private void ClearStagedDir(string staged)
    {
        if (!Directory.Exists(staged))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(staged))
        {
            DeleteStagedEntry(entry);
        }
    }

    /// <summary>
    /// Deletes a single staged entry, <b>symlink-aware</b>: a reparse point
    /// (file or directory symlink) is removed as a link only, never followed
    /// into the repository. A real directory is recursed (symlink-aware at
    /// every level, so a nested reparse point is also removed as a link); a
    /// real file is deleted. This is data-safety-critical: the staged tree
    /// holds symlinks into the repository, so a naive recursive delete would
    /// follow them and destroy the mod files.
    /// </summary>
    /// <remarks>
    /// The delete API must match the link's kind, or Windows throws:
    /// <list type="bullet">
    /// <item><description>Directory symlink (ReparsePoint + Directory):
    /// <see cref="Directory.Delete(string)"/>. On Windows,
    /// <see cref="File.Delete(string)"/> on a directory (incl. a dir-symlink)
    /// throws <see cref="UnauthorizedAccessException"/> ("Access denied"; Windows
    /// surfaces "is a directory" via the file-delete API as access-denied).
    /// <see cref="Directory.Delete(string)"/> on a reparse point removes the
    /// point itself, NOT the target, so it stays data-safe on both platforms.
    /// </description></item>
    /// <item><description>File symlink (ReparsePoint, not Directory):
    /// <see cref="File.Delete(string)"/>.</description></item>
    /// <item><description>Real directory: recurse via
    /// <see cref="DeleteStagedEntry"/> per child (NOT
    /// <see cref="Directory.Delete(string, bool)"/> with recursive: true, which
    /// throws <see cref="UnauthorizedAccessException"/> on a child directory
    /// junction on Windows), then remove the now-empty directory.</description>
    /// </item>
    /// </list>
    /// </remarks>
    private static void DeleteStagedEntry(string entry)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(entry);
        }
        catch (FileNotFoundException)
        {
            return; // raced away; nothing to delete
        }
        catch (DirectoryNotFoundException)
        {
            return; // raced away; nothing to delete
        }

        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry); // directory symlink -> remove the link only
            }
            else
            {
                File.Delete(entry);      // file symlink -> remove the link only
            }
        }
        else if ((attrs & FileAttributes.Directory) != 0)
        {
            // Real directory -> recurse, staying symlink-aware so a reparse
            // point nested inside (e.g. the staging links under staged/mods/)
            // is removed as a link, never followed. Directory.Delete(recursive:
            // true) throws UnauthorizedAccessException on a child directory
            // junction on Windows, so the recursion is explicit.
            foreach (var child in Directory.EnumerateFileSystemEntries(entry))
            {
                DeleteStagedEntry(child);
            }
            Directory.Delete(entry);
        }
        else
        {
            File.Delete(entry);                        // real file (mods.lst, etc.)
        }
    }

    // ---- mods.lst generation ------------------------------------------------

    private void WriteModList(List<string> stagedNames, string stagedRoot)
    {
        // The successfully-staged enabled mods, in Order. Faithful to what's in
        // staged/ (skipped mods are absent here too). No DMF-first enforcement,
        // no auto-sort (those are higher-layer concerns).
        var sb = new StringBuilder();
        foreach (var name in stagedNames)
        {
            sb.Append(name).Append('\n');
        }

        File.WriteAllText(ModListPath(stagedRoot), sb.ToString(), ModListEncoding);
    }

    // ---- persistence helpers ------------------------------------------------

    private Profile ReadProfileFile(string profileDir)
    {
        var file = ProfileFilePath(profileDir);
        EnsureReadable(file, profileDir);
        using var stream = File.OpenRead(file);
        var profile = JsonSerializer.Deserialize<Profile>(stream) ?? new Profile();

        // System.Text.Json can leave a non-nullable property as null if the
        // file explicitly carries null (e.g. a hand-edit). Coerce Mods so
        // downstream enumeration never NRE.
        profile.Mods ??= Array.Empty<ModListEntry>();

        // Same coercion for Description (missing property or explicit JSON null
        // both deserialize to null for the ref-type prop). Mirrors the
        // LaunchSettings ??= new() normalization: a pre-description profile.json
        // loads with an empty description, and a hand-edited null is healed.
        profile.Description ??= string.Empty;

        // Same coercion for LaunchSettings (missing property or explicit JSON
        // null both deserialize to null for the ref-type prop). Mirrors the
        // Mods ??= Empty normalization above: a pre-launch-settings profile.json
        // loads as empty settings, and a hand-edited null is healed.
        profile.LaunchSettings ??= new LaunchSettings();

        // Fresh-start tolerance + null-Policy coercion. Two passes:
        //   - drop entries whose ContainerId is Guid.Empty (a legacy entry
        //     deserialized without its container id; the spec is fresh-
        //     start, so these are dropped + logged, not migrated);
        //   - coerce a null Policy to Latest (a hand-edit, or a legacy entry).
        if (profile.Mods.Any(m => m.Policy is null))
        {
            profile.Mods = profile.Mods
                .Select(m => m.Policy is null ? m with { Policy = ModVersionPolicy.Latest } : m)
                .ToList();
        }

        var dropped = profile.Mods.Where(m => m.ContainerId == Guid.Empty).ToList();
        if (dropped.Count > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} legacy mod entries from profile at {Dir} (no ContainerId; legacy shape). " +
                "The spec is fresh-start: re-add mods through the import flow.",
                dropped.Count, profileDir);
            profile.Mods = profile.Mods.Where(m => m.ContainerId != Guid.Empty).ToList();
        }

        // Fresh-start tolerance: a legacy pinned entry (the pre-versionId shape)
        // carries a $kind:"pinned" Policy whose JSON has a "Version" tag string.
        // Under the new shape that property is unrecognized and skipped, leaving
        // the deserialized PinnedPolicy's VersionId empty. A PinnedPolicy with
        // an empty VersionId is a phantom pin (no version resolves); drop it +
        // log so the entry is re-added and re-pinned through the import flow.
        // Same fresh-start posture as the ContainerId drop above.
        var droppedPhantomPins = profile.Mods
            .Where(m => m.Policy is PinnedPolicy p && string.IsNullOrEmpty(p.VersionId))
            .ToList();
        if (droppedPhantomPins.Count > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} phantom-pinned mod entries from profile at {Dir} (empty VersionId; legacy pinned shape). " +
                "The spec is fresh-start: re-pin mods through the policy dropdown.",
                droppedPhantomPins.Count, profileDir);
            profile.Mods = profile.Mods
                .Where(m => !(m.Policy is PinnedPolicy p && string.IsNullOrEmpty(p.VersionId)))
                .ToList();
        }

        return profile;
    }

    private static void EnsureReadable(string file, string profileDir)
    {
        if (!Directory.Exists(profileDir) || !File.Exists(file))
        {
            throw new KeyNotFoundException($"No profile exists at '{profileDir}'.");
        }
    }

    private void WriteProfileFile(Profile profile, string baseFolder)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(ProfileFilePath(ProfileDir(baseFolder, profile.Id)), json, ModListEncoding);
    }

    // ---- path helpers (all internal-only - never leak through the interface) --

    private static string ProfileDir(string baseFolder, Guid id) => Path.Combine(baseFolder, id.ToString());
    private static string ProfileFilePath(string profileDir) => Path.Combine(profileDir, "profile.json");
    private static string StagedDir(string baseFolder, Guid id) => Path.Combine(ProfileDir(baseFolder, id), "staged");
    private static string ModsDir(string stagedRoot) => Path.Combine(stagedRoot, "mods");
    private static string ModListPath(string stagedRoot) => Path.Combine(stagedRoot, "mods.lst");

    private static KeyNotFoundException UnknownProfile(Guid id) =>
        new($"No profile exists with id '{id}'.");

    private static KeyNotFoundException UnknownMod(Guid id, Guid containerId) =>
        new($"Profile '{id}' has no mod with container id '{containerId}'.");
}
