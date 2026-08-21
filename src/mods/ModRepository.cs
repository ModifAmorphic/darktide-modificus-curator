using System.Text;
using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Mods;

/// <summary>
/// Filesystem-backed <see cref="IModRepository"/>. Each container lives under
/// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/</c> with this layout:
/// </summary>
/// <remarks>
/// <code>
/// &lt;ModsFolder&gt;/              (auto-created on first run)
///   &lt;containerUUID&gt;/                 (container dir; id-named, opaque)
///     container.json                   (id + source + name + versions[] - the manifest)
///     &lt;versionFolder&gt;/                (opaque-ID version subfolder; the mod files)
///     &lt;versionFolder&gt;/
///       ...
/// </code>
/// <para>
/// On construction the repository scans every
/// <c>&lt;ModsFolder&gt;/&lt;*&gt;/container.json</c> into an in-memory
/// index (<c>containerId -> container</c> + an untracked-name lookup). All
/// mutations write through to the per-container manifest; the index stays in
/// sync. A corrupt/unreadable manifest is skipped with a warning (one bad
/// container never breaks the rest), mirroring <c>ProfileService</c>'s
/// "skip unreadable, keep going" posture.</para>
/// <para>
/// Registered as a singleton: it holds the in-memory index (cheap to rebuild).
/// The mods root folder is read live from <see cref="IConfigLoader"/> on each
/// operation (one snapshot per op), so a runtime folder change via the Settings
/// window takes effect immediately; <see cref="Directory.CreateDirectory"/>
/// runs per-op (idempotent) on the live path. The index itself is built once at
/// construction from the mods root configured at that moment: a mid-session
/// change to the mods-repo location in config requires a restart for the index
/// to follow. All public methods are
/// synchronized via an internal lock (<c>_sync</c>), serializing reads and
/// writes so a background-thread mutation (e.g. a reconciliation write from
/// <c>UpdateCheckService</c>) cannot race a UI-thread mutation on the in-memory
/// index or the manifests. The lock is reentrant (Monitor), so a public method
/// that delegates to another on the same thread (e.g.
/// <see cref="PruneUnreferenced"/> calls <see cref="RemoveVersion"/>; 
/// <see cref="FindUntrackedByName"/> calls <see cref="Get"/>) does not
/// self-deadlock.</para>
/// </remarks>
internal sealed class ModRepository : IModRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // Consistent with profile.json / mods.lst: UTF-8 without BOM (hand-edits +
    // diffs stay clean; no consumer expects a BOM here).
    private static readonly Encoding ManifestEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly string VersionManifestFileName = "container.json";

    private readonly IConfigLoader _configLoader;
    private readonly ILogger<ModRepository> _logger;

    // Serializes all public method access so a background-thread write cannot
    // race a UI-thread write on the in-memory index or the manifests. Coarse
    // locking is acceptable: the operations are infrequent and the dictionaries
    // are small. The lock is reentrant (Monitor), so a public method that
    // delegates to another public method on the same thread (e.g.
    // PruneUnreferenced -> RemoveVersion, FindUntrackedByName -> Get) does not
    // self-deadlock.
    private readonly object _sync = new();

    // Primary index: containerId -> container. Source identity lookups are
    // served by scanning this (cheap for dozens of containers); the untracked-
    // name index below is the only dedicated secondary index (untracked dedup
    // happens on every import).
    private readonly Dictionary<Guid, ModContainer> _byId = new();

    // Untracked-name -> containerId (only untracked containers are entered).
    // Nexus lookups scan _byId (identity is fully on the source record).
    private readonly Dictionary<string, Guid> _untrackedByName = new(StringComparer.Ordinal);

    // Linked containerId -> whether its ExternalPath was available on disk when
    // the container was recorded or the index was last built (transient, not
    // persisted): linked availability is a live filesystem question, and
    // staging re-checks Directory.Exists independently at stage time. Absent
    // entries mean "not a linked container" (IsExternalAvailable returns true).
    private readonly Dictionary<Guid, bool> _externalAvailable = new();

    /// <summary>
    /// DI constructor. Builds the in-memory index from the current mods root
    /// (read live from <see cref="IConfigLoader"/>).
    /// </summary>
    public ModRepository(IConfigLoader configLoader, ILogger<ModRepository> logger)
    {
        _configLoader = configLoader;
        _logger = logger;

        // Build the in-memory index from the current mods root. The index is
        // construction-time state (a scan of the disk); live-read changes the
        // per-op path computations, not the index contents.
        var baseFolder = EnsureBaseFolder();
        RebuildIndex(baseFolder);
    }

    /// <summary>
    /// Reads the mods root folder from the live config snapshot and ensures it
    /// exists. Called at the top of each public operation so a runtime folder
    /// change takes effect immediately (the directory is created on the live
    /// path, and subsequent path helpers derive from it).
    /// </summary>
    private string EnsureBaseFolder()
    {
        // ModsFolder is non-null by CuratorConfig contract (defaults to
        // <app-data>/mods). Directory.CreateDirectory is idempotent, so this
        // makes every subsequent op first-run safe without each re-checking.
        var baseFolder = _configLoader.Load().ModsFolder;
        Directory.CreateDirectory(baseFolder);
        return baseFolder;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModContainer> List()
    {
        lock (_sync)
        {
            return _byId.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public ModContainer? Get(Guid containerId)
    {
        lock (_sync)
        {
            return _byId.TryGetValue(containerId, out var c) ? c : null;
        }
    }

    /// <inheritdoc />
    public ModContainer? FindBySource(ModSource source)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(source);
            // Untracked identity is the container Name, not a field on the source.
            // Callers route untracked dedup through FindUntrackedByName; here we
            // return null so a generic FindBySource caller never gets a false match.
            if (source is UntrackedSource)
            {
                return null;
            }

            return source switch
            {
                NexusSource n => _byId.Values.FirstOrDefault(c =>
                    c.Source is NexusSource ns && ns.ModId == n.ModId),
                // Linked identity is the normalized ExternalPath. SamePath
                // normalizes both sides via GetFullPath and compares with the
                // platform-appropriate comparison (case-insensitive on Windows,
                // case-sensitive on Linux). Callers pass an already-normalized
                // path (LinkFolder normalizes), but normalizing again here
                // keeps the lookup correct for any caller.
                LinkedSource l => _byId.Values.FirstOrDefault(c =>
                    c.Source is LinkedSource ls && SamePath(ls.ExternalPath, l.ExternalPath)),
                _ => null,
            };
        }
    }

    /// <inheritdoc />
    public ModContainer? FindUntrackedByName(string name)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(name);
            return _untrackedByName.TryGetValue(name, out var id) ? Get(id) : null;
        }
    }

    /// <inheritdoc />
    public bool IsExternalAvailable(Guid containerId)
    {
        lock (_sync)
        {
            // Absent (non-linked / unknown) defaults to available so a caller
            // that queries a managed or stale id never sees a false "broken"
            // signal. The UI only queries this for linked rows it holds.
            return !_externalAvailable.TryGetValue(containerId, out var available) || available;
        }
    }

    /// <inheritdoc />
    public ModContainer CreateContainer(ModSource source, string name)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Container name must not be null or whitespace.", nameof(name));
            }

            var baseFolder = EnsureBaseFolder();
            var container = new ModContainer
            {
                Id = Guid.NewGuid(),
                Source = source,
                Name = name,
                Versions = Array.Empty<ModVersion>(),
            };

            Directory.CreateDirectory(ContainerDir(baseFolder, container.Id));
            WriteContainer(container, baseFolder);

            _byId[container.Id] = container;
            IndexUntrackedName(container);
            // Keep the availability cache authoritative immediately for a freshly
            // created linked container (absent ids already default to true, and a
            // just-linked folder exists, but the cache should mirror the rescan
            // path for symmetry). No-op for non-linked containers.
            IndexExternalAvailability(container);
            _logger.LogInformation("Created container {Id} ('{Name}', source={Source})", container.Id, name, source);
            return container;
        }
    }

    /// <inheritdoc />
    public ModContainer AddVersion(
        Guid containerId,
        string versionString,
        Action<string> populateFolder,
        DateTimeOffset? remoteUploadedAt = null,
        int? remoteFileId = null,
        ModDisplayMetadata? displayMetadata = null)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(versionString);
            ArgumentNullException.ThrowIfNull(populateFolder);

            if (!_byId.TryGetValue(containerId, out var container))
            {
                throw new KeyNotFoundException($"No mod container with id '{containerId}'.");
            }

            var baseFolder = EnsureBaseFolder();
            var containerDir = ContainerDir(baseFolder, containerId);
            Directory.CreateDirectory(containerDir);

            var existing = container.Versions.FirstOrDefault(v =>
                string.Equals(v.VersionString, versionString, StringComparison.Ordinal));

            List<ModVersion> versions;
            if (existing is not null)
            {
                // Dedup: reuse the existing folder + entry. PopulateAtomically swaps
                // the new content into the existing version folder atomically (the
                // old contents survive any populateFolder failure); the version
                // entry (Folder, VersionString, ImportedAt) is left unchanged so
                // the import stamp stays stable. (Re-importing a version is a file
                // refresh, not a re-stamp.) RemoteUploadedAt + FileId are the
                // entry fields refreshed on re-import: a re-acquired version
                // carries the current remote facts (callers pass nulls for manual
                // re-imports, which clears them; non-remote sources aren't
                // update-checked anyway). Latest is then re-evaluated: a refreshed
                // remote timestamp can make the reused entry newly newest.
                var versionDir = VersionDir(baseFolder, containerId, existing.Folder);
                PopulateAtomically(versionDir, populateFolder);
                var refreshed = existing with { RemoteUploadedAt = remoteUploadedAt, FileId = remoteFileId };
                versions = WithLatestMarked(
                    container.Versions.Select(v => ReferenceEquals(v, existing) ? refreshed : v).ToList());
                _logger.LogInformation(
                    "Re-imported version '{Version}' on container {Id} (folder reused: {Folder})",
                    versionString, containerId, existing.Folder);
            }
            else
            {
                // New version: new opaque folder + new entry stamped now.
                // PopulateAtomically stages into a temp + swaps into the new
                // version folder; on a populateFolder failure nothing is created
                // on disk and the manifest is left untouched (no entry added
                // below). Latest is recomputed over ALL versions with the
                // effective-timestamp key, so importing an older remote file
                // leaves the flag on the existing newest entry.
                var folder = Guid.NewGuid().ToString("N");
                var versionDir = VersionDir(baseFolder, containerId, folder);
                PopulateAtomically(versionDir, populateFolder);

                var entry = new ModVersion
                {
                    Folder = folder,
                    VersionString = versionString,
                    ImportedAt = DateTimeOffset.UtcNow,
                    RemoteUploadedAt = remoteUploadedAt,
                    FileId = remoteFileId,
                };
                versions = WithLatestMarked(container.Versions.Append(entry).ToList());
                _logger.LogInformation(
                    "Added version '{Version}' on container {Id} (folder: {Folder}, isLatest: {IsLatest})",
                    versionString, containerId, folder, versions.First(v => v.Folder == folder).IsLatest);
            }

            // DisplayMetadata is container-scoped: a non-null argument
            // replaces the prior value in the same manifest update as the
            // version mutation; null preserves any prior value, so a manual
            // re-import (folder/archive via the picker, no metadata argument)
            // never erases a prior Nexus acquisition or backfill. A
            // populateFolder failure above rethrows before this point, so the
            // prior metadata survives a failed populate alongside the prior
            // files + manifest.
            var updated = displayMetadata is null
                ? container with { Versions = versions }
                : container with { Versions = versions, DisplayMetadata = displayMetadata };
            _byId[containerId] = updated;
            WriteContainer(updated, baseFolder);
            return updated;
        }
    }

    /// <summary>
    /// Recomputes the <see cref="ModVersion.IsLatest"/> flag over
    /// <paramref name="versions"/>: the newest by effective timestamp carries
    /// it, every other entry does not. Shared by both
    /// <see cref="AddVersion"/> branches and the
    /// <see cref="RemoveVersion"/> promotion so the key cannot drift between
    /// sites.
    /// </summary>
    private static List<ModVersion> WithLatestMarked(List<ModVersion> versions)
    {
        // Effective timestamp: the remote publish date when known, else the
        // import date. ImportedAt breaks exact ties (two files within the
        // remote timestamp granularity); an all-null container degenerates to
        // a plain ImportedAt argmax. MaxBy returns the first max, so exact
        // double-ties stay stable in storage order.
        var newest = versions.MaxBy(v => (v.RemoteUploadedAt ?? v.ImportedAt, v.ImportedAt))!;
        return versions.Select(v => v with { IsLatest = ReferenceEquals(v, newest) }).ToList();
    }

    /// <inheritdoc />
    public ModContainer? RenameContainer(Guid containerId, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        lock (_sync)
        {
            if (!_byId.TryGetValue(containerId, out var container))
            {
                return null;
            }

            // No-op when the name is already current (ordinal). Avoids a
            // pointless manifest rewrite on every name-sync pass for mods whose
            // name has not drifted.
            if (string.Equals(container.Name, newName, StringComparison.Ordinal))
            {
                return container;
            }

            var baseFolder = EnsureBaseFolder();
            var updated = container with { Name = newName };
            _byId[containerId] = updated;
            WriteContainer(updated, baseFolder);

            // Keep the untracked-name index consistent for untracked containers
            // (their dedup key is the name). Nexus identity is on the source
            // record, so the index is not involved for those + stays untouched
            // (the name-sync that drives this path targets Nexus containers, for
            // which the index never held an entry).
            if (container.Source is UntrackedSource)
            {
                _untrackedByName.Remove(container.Name);
                // Mirror IndexUntrackedName: if newName is already held by a
                // different untracked container (a hand-edit edge case), the
                // index now points at this one and the other is reachable only
                // via Get(id) + List(). Log so it is visible rather than silent.
                if (_untrackedByName.TryGetValue(newName, out var collision) && collision != container.Id)
                {
                    _logger.LogWarning(
                        "Duplicate untracked container name '{Name}' (ids {Prior} + {Current}); index points at {Current}.",
                        newName, collision, container.Id, container.Id);
                }
                _untrackedByName[newName] = container.Id;
            }

            _logger.LogInformation(
                "Renamed container {Id} '{Old}' -> '{New}'",
                containerId, container.Name, newName);
            return updated;
        }
    }

    /// <inheritdoc />
    public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_sync)
        {
            if (!_byId.TryGetValue(containerId, out var container))
            {
                return false;
            }

            // Missing-only initialization: any existing non-null metadata
            // (whether equal or different) returns false with no rewrite. This
            // makes the method safe against a concurrent writer (acquisition,
            // another backfill, a manual edit) that set the value between the
            // caller's Get and this call. The atomic check-and-set under the
            // lock is the correctness boundary; a caller's pre-write Get is an
            // optimization, not a guard.
            if (container.DisplayMetadata is not null)
            {
                return false;
            }

            var baseFolder = EnsureBaseFolder();
            var updated = container with { DisplayMetadata = metadata };
            // Write the manifest BEFORE publishing into the in-memory index so a
            // WriteContainer failure (disk full, I/O error, permission denied)
            // leaves the in-memory aggregate's DisplayMetadata null and
            // retryable. If the write throws, _byId still holds the original
            // container (null metadata) and the caller can retry later.
            WriteContainer(updated, baseFolder);
            _byId[containerId] = updated;

            _logger.LogInformation(
                "Initialized display metadata for container {Id} (summary {HasSummary}, thumbnail {HasThumb}, adult {Adult})",
                containerId,
                metadata.Summary.Length > 0,
                metadata.ThumbnailUrl is not null,
                metadata.IsAdultContent);
            return true;
        }
    }

    /// <summary>
    /// Stages <paramref name="populateFolder"/>'s output into a temp directory
    /// that is a SIBLING of <paramref name="versionDir"/> (so the final swap is
    /// a same-volume atomic <see cref="Directory.Move"/>), then swaps it into
    /// <paramref name="versionDir"/>. On any exception from
    /// <paramref name="populateFolder"/> the temp is deleted (best-effort) and
    /// the existing <paramref name="versionDir"/> is left untouched (for dedup:
    /// the old version survives on disk; for new-version: nothing was created),
    /// and the original exception is rethrown as-is (no swallowing, no wrapping).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the transactional core of <see cref="AddVersion"/>. Populating
    /// the temp succeeds BEFORE the existing version folder is touched, so any
    /// exception from <paramref name="populateFolder"/> (CRC error, disk full,
    /// I/O error) leaves the old version intact and the manifest unchanged: no
    /// manifest/disk inconsistency on a mod potentially referenced by a profile
    /// (the startup prune only reclaims containers no profile references).</para>
    /// <para>
    /// <b>tempDir location:</b> a sibling of <paramref name="versionDir"/> under
    /// the same container dir, named <c>&lt;versionFolder&gt;.tmp.&lt;guid&gt;</c>.
    /// It MUST be a sibling (not under the system temp): a cross-volume
    /// <see cref="Directory.Move"/> throws <see cref="IOException"/> rather than
    /// falling back to a copy, and the system temp is commonly on a different
    /// volume from the mods root.</para>
    /// <para>
    /// <b>Swap order:</b> populate succeeds first (into the temp), THEN
    /// <see cref="CleanTarget"/>(<paramref name="versionDir"/>) runs (a no-op
    /// on the new-version branch since it does not exist yet; deletes the old
    /// version on dedup), THEN <see cref="Directory.Move"/>(tempDir,
    /// versionDir). A failure of populate therefore never destroys the old
    /// version; the only window in which both old and new are absent is between
    /// CleanTarget and Move, both of which are near-instant BCL calls on a
    /// same-volume path.</para>
    /// <para>
    /// <b>Crash recovery:</b> if the process dies between CreateDirectory(temp)
    /// and Move, the temp is left as an orphan under the container dir. The
    /// repo's index is built from <c>container.json</c>, not by scanning version
    /// subfolders, so the orphan is invisible to the index but occupies disk.
    /// <see cref="SweepOrphanTemps"/> deletes any <c>*.tmp.*</c> directories
    /// under the container dir at the start of each call as best-effort
    /// cleanup.</para>
    /// </remarks>
    private void PopulateAtomically(string versionDir, Action<string> populateFolder)
    {
        var containerDir = Path.GetDirectoryName(versionDir)!;
        SweepOrphanTemps(containerDir);

        var tempDir = versionDir + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(tempDir);
            populateFolder(tempDir);
        }
        catch
        {
            // The existing versionDir is untouched (CleanTarget has not run yet
            // for dedup; the new-version branch never created it). Best-effort
            // delete of the partial temp; an orphan left here is reclaimed by
            // SweepOrphanTemps on the next call. Rethrow as-is: no swallowing,
            // no wrapping, callers see the actual failure.
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not delete orphan temp dir {Path} after populateFolder failed.",
                    tempDir);
            }
            throw;
        }

        // Swap: remove the old version (no-op when versionDir does not exist
        // yet, i.e. the new-version branch), then atomic same-volume rename.
        CleanTarget(versionDir);
        Directory.Move(tempDir, versionDir);
    }

    /// <summary>
    /// Best-effort sweep of orphan temp directories left under
    /// <paramref name="containerDir"/> by a <see cref="PopulateAtomically"/>
    /// call whose process crashed before the swap. Recognizes the
    /// <c>&lt;versionFolder&gt;.tmp.&lt;guid&gt;</c> naming convention; any
    /// delete failure is logged + skipped so one locked dir does not abort the
    /// rest of the sweep or the caller. Version folders are 32-char hex GUIDs
    /// and container UUIDs are also GUIDs, so the <c>.tmp.</c> substring never
    /// matches a real tracked folder.
    /// </summary>
    private void SweepOrphanTemps(string containerDir)
    {
        if (!Directory.Exists(containerDir))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(containerDir))
        {
            var name = Path.GetFileName(dir);
            // Match the <versionFolder>.tmp.<guid> naming only when ".tmp." is
            // preceded by the version-folder name. Real orphans carry the
            // 32-char hex GUID prefix, so ".tmp." sits at position 32, never 0;
            // <= 0 skips both not-found and a hypothetical prefix-less ".tmp.*".
            if (name.IndexOf(".tmp.", StringComparison.Ordinal) <= 0)
            {
                continue;
            }
            try
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Swept orphan temp dir {Path} left by a prior crashed import.", dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete orphan temp dir {Path}.", dir);
            }
        }
    }

    /// <inheritdoc />
    public void RemoveVersion(Guid containerId, string versionFolder)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(versionFolder);

            if (!_byId.TryGetValue(containerId, out var container))
            {
                return; // idempotent: unknown container is a no-op.
            }

            var existing = container.Versions.FirstOrDefault(v =>
                string.Equals(v.Folder, versionFolder, StringComparison.Ordinal));
            if (existing is null)
            {
                return; // idempotent: unknown folder is a no-op.
            }

            var baseFolder = EnsureBaseFolder();
            var wasLatest = existing.IsLatest;
            var versions = container.Versions.Where(v => !ReferenceEquals(v, existing)).ToList();

            // If the removed entry was latest, re-evaluate the flag over the
            // survivors with the same effective-timestamp key AddVersion uses
            // (remote publish date when known, else import date, ImportedAt
            // tie-break).
            if (wasLatest && versions.Count > 0)
            {
                versions = WithLatestMarked(versions);
            }

            var updated = container with { Versions = versions };
            _byId[containerId] = updated;
            WriteContainer(updated, baseFolder);

            // Best-effort: drop the on-disk folder. A missing / unwritable folder
            // is logged + swallowed so a prune never crashes on a stray lock.
            DeleteVersionDir(baseFolder, containerId, versionFolder);
            _logger.LogInformation(
                "Removed version folder '{Folder}' from container {Id}", versionFolder, containerId);
        }
    }

    /// <inheritdoc />
    public string GetVersionFolderPath(Guid containerId, string versionFolder)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(versionFolder);
            var baseFolder = EnsureBaseFolder();
            return VersionDir(baseFolder, containerId, versionFolder);
        }
    }

    /// <inheritdoc />
    public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
    {
        lock (_sync)
        {
            ArgumentNullException.ThrowIfNull(referenced);
            var baseFolder = EnsureBaseFolder();

            // The set of container ids a caller marked as referenced, regardless
            // of version folder. A LinkedSource container has no versions, so it
            // is kept solely by containerId reference: the caller (ModCleanup)
            // adds (containerId, string.Empty) for each linked profile entry.
            // The empty version folder is a sentinel that never matches a real
            // opaque version id, so it does not affect the version-drop loop
            // below; its only role here is to keep a referenced linked
            // container's id in this set. Managed containers with zero versions
            // are never in this set (ResolveVersion returns null for them), so
            // they remain pruned as before.
            var referencedContainerIds = referenced.Select(p => p.ContainerId).ToHashSet();

            // Drop unreferenced versions per container. Snapshot the containers
            // first (RemoveVersion mutates the index); each RemoveVersion call
            // operates on the live index, not the snapshot, so consecutive calls on
            // the same container see the updated state.
            foreach (var container in _byId.Values.ToArray())
            {
                var orphans = container.Versions
                    .Where(v => !referenced.Contains((container.Id, v.Folder)))
                    .Select(v => v.Folder)
                    .ToArray();
                foreach (var folder in orphans)
                {
                    RemoveVersion(container.Id, folder);
                }
            }

            // Remove containers left with zero versions (manifest + dir), UNLESS
            // a caller marked the container id itself as referenced (linked
            // containers: zero versions by design, kept by containerId). The
            // external target of a linked container is NEVER touched here: only
            // the container dir (which holds only container.json for linked) is
            // removed. Snapshot again; the index shrank above.
            foreach (var empty in _byId.Values
                .Where(c => c.Versions.Count == 0 && !referencedContainerIds.Contains(c.Id))
                .ToArray())
            {
                _byId.Remove(empty.Id);
                _externalAvailable.Remove(empty.Id);
                if (empty.Source is UntrackedSource)
                {
                    _untrackedByName.Remove(empty.Name);
                }
                var containerDir = ContainerDir(baseFolder, empty.Id);
                if (Directory.Exists(containerDir))
                {
                    try
                    {
                        Directory.Delete(containerDir, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not delete empty container dir {Path}", containerDir);
                    }
                }
                _logger.LogInformation("Pruned empty container {Id} ('{Name}')", empty.Id, empty.Name);
            }
        }
    }

    /// <summary>
    /// Ordinal, case-insensitive path equality after full-path normalization.
    /// Used to compare linked external-path identities. Trailing directory
    /// separators are trimmed (<see cref="Path.TrimEndingDirectorySeparator(string)"/>)
    /// so <c>/a/b</c>, <c>/a/b/</c>, and <c>/a/b/./</c> all compare equal (a
    /// plain <see cref="Path.GetFullPath"/> leaves a trailing separator in some
    /// input forms, which would otherwise break identity).
    /// </summary>
    private static bool SamePath(string a, string b)
    {
        var na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(na, nb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // ---- index rebuild ------------------------------------------------------

    private void RebuildIndex(string baseFolder)
    {
        // Clear the availability index at the start of a rebuild: it is fully
        // recomputed below from the linked containers the scan loads.
        _externalAvailable.Clear();

        foreach (var dir in Directory.EnumerateDirectories(baseFolder))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var id))
            {
                _logger.LogDebug("Skipping non-container directory under mods root: {Dir}", dir);
                continue;
            }

            // Sweep orphan temp dirs (<versionFolder>.tmp.<guid>) left by a
            // prior crashed import into this container. Completes the per-
            // AddVersion reactive sweep into a full cleanup: an orphan in a
            // container that is never re-imported is reclaimed at the next
            // index build (construction/rescan) instead of lingering on disk.
            SweepOrphanTemps(dir);

            var manifest = ContainerManifestPath(baseFolder, id);
            if (!File.Exists(manifest))
            {
                _logger.LogDebug("Skipping container directory with no container.json: {Dir}", dir);
                continue;
            }

            ModContainer? container;
            try
            {
                using var stream = File.OpenRead(manifest);
                container = JsonSerializer.Deserialize<ModContainer>(stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt manifest must not break the rest of the index.
                // IgnoreUnrecognizedTypeDiscriminators = false means an unknown
                // $kind (e.g. a newer source type this build does not know)
                // throws JsonException here and is gracefully skipped.
                _logger.LogError(ex, "Container manifest at {Path} is unreadable; skipping.", manifest);
                continue;
            }

            if (container is null || container.Id != id)
            {
                // Defensive: a hand-edited manifest whose id does not match its
                // directory is treated as unreadable (the directory is the
                // source of truth for the path; mismatched ids would shadow).
                _logger.LogError(
                    "Container manifest at {Path} has a missing or mismatched id (dir={Dir}); skipping.",
                    manifest, id);
                continue;
            }

            _byId[id] = container;
            IndexUntrackedName(container);
            IndexExternalAvailability(container);
        }
    }

    /// <summary>
    /// Records a linked container's external-folder availability into
    /// <see cref="_externalAvailable"/>. A non-linked container is a no-op
    /// (availability is only tracked for linked containers; managed containers
    /// report <c>true</c> from <see cref="IsExternalAvailable"/> by absence).
    /// </summary>
    private void IndexExternalAvailability(ModContainer container)
    {
        if (container.Source is not LinkedSource linked)
        {
            return;
        }

        // Directory.Exists is cheap + sufficient for the cached signal. A
        // missing/unreadable folder records false; staging re-checks
        // independently so a transient flip between scan + stage is handled.
        _externalAvailable[container.Id] = Directory.Exists(linked.ExternalPath);
    }

    private void IndexUntrackedName(ModContainer container)
    {
        if (container.Source is not UntrackedSource)
        {
            return;
        }

        // Two untracked containers with the same Name is an edge case (the
        // import service dedups, so it can only arise from a hand-edit). The
        // last one wins the index; FindUntrackedByName returns it, and the
        // other is still reachable via Get(id) + List(). Log so it's visible.
        if (_untrackedByName.TryGetValue(container.Name, out var prior) && prior != container.Id)
        {
            _logger.LogWarning(
                "Duplicate untracked container name '{Name}' (ids {Prior} + {Current}); index points at {Current}.",
                container.Name, prior, container.Id, container.Id);
        }
        _untrackedByName[container.Name] = container.Id;
    }

    // ---- manifest persistence ------------------------------------------------

    private void WriteContainer(ModContainer container, string baseFolder)
    {
        var dir = ContainerDir(baseFolder, container.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(container, JsonOptions);
        File.WriteAllText(ContainerManifestPath(baseFolder, container.Id), json, ManifestEncoding);
    }

    /// <summary>
    /// Removes a prior version dir/file at <paramref name="path"/> so a re-import
    /// is a clean repopulate. Idempotent (missing target is a no-op). A file
    /// (not a dir) at the target is also removed: defends against weird prior
    /// state.
    /// </summary>
    private static void CleanTarget(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    private void DeleteVersionDir(string baseFolder, Guid containerId, string versionFolder)
    {
        var dir = VersionDir(baseFolder, containerId, versionFolder);
        if (!Directory.Exists(dir) && !File.Exists(dir))
        {
            return;
        }

        try
        {
            CleanTarget(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete version folder {Path}", dir);
        }
    }

    // ---- path helpers (all internal-only - paths derive from the config root + ids) --

    private static string ContainerDir(string baseFolder, Guid containerId) =>
        Path.Combine(baseFolder, containerId.ToString());
    private static string VersionDir(string baseFolder, Guid containerId, string versionFolder) =>
        Path.Combine(ContainerDir(baseFolder, containerId), versionFolder);
    private static string ContainerManifestPath(string baseFolder, Guid containerId) =>
        Path.Combine(ContainerDir(baseFolder, containerId), VersionManifestFileName);
}
