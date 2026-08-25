namespace Modificus.Curator.Mods;

/// <summary>
/// The unified mod repository: storage CRUD keyed by container, each holding
/// one or more versioned mod copies. One container per <c>(source, identity)</c>
/// pair; profiles reference a mod by container id + a version policy.
/// </summary>
/// <remarks>
/// <para>
/// Container identity is by source: Nexus by <see cref="NexusSource.ModId"/>,
/// Untracked by <see cref="ModContainer.Name"/> (the source record carries no
/// identity payload, so Untracked lookup goes through
/// <see cref="FindUntrackedByName"/>). Different source-types never collide and
/// never share.</para>
/// <para>
/// Safe for concurrent callers: the repository is read and mutated from both
/// the UI thread and background update-check work.</para>
/// </remarks>
public interface IModRepository
{
    /// <summary>All containers, in no guaranteed order.</summary>
    IReadOnlyList<ModContainer> List();

    /// <summary>Looks up a container by id. Null if absent.</summary>
    ModContainer? Get(Guid containerId);

    /// <summary>
    /// Looks up a container by its source identity: Nexus by
    /// <see cref="NexusSource.ModId"/>; Linked by normalized
    /// <see cref="LinkedSource.ExternalPath"/> (case-insensitive on Windows,
    /// case-sensitive on Linux). Returns <c>null</c> for
    /// <see cref="UntrackedSource"/> (untracked identity is the container
    /// <see cref="ModContainer.Name"/>; use <see cref="FindUntrackedByName"/>).
    /// </summary>
    ModContainer? FindBySource(ModSource source);

    /// <summary>
    /// Looks up an untracked container by its <see cref="ModContainer.Name"/>
    /// (ordinal). Returns <c>null</c> if absent.
    /// </summary>
    ModContainer? FindUntrackedByName(string name);

    /// <summary>
    /// Creates a new container: generates the <see cref="Guid"/>, writes an
    /// empty <c>container.json</c>, and returns the new container. Does not
    /// check for an existing same-identity container (the caller does that via
    /// <see cref="FindBySource"/> / <see cref="FindUntrackedByName"/> before
    /// deciding to create).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or
    /// whitespace.</exception>
    ModContainer CreateContainer(ModSource source, string name);

    /// <summary>
    /// Adds (or dedup-reuses) a version on the container. The repository creates
    /// the opaque version-folder ID and invokes <paramref name="populateFolder"/>
    /// with the absolute path of an EMPTY TEMP DIRECTORY (a sibling of the
    /// final version folder) so the caller extracts/copies the mod files into
    /// it; on success the repo atomically swaps the temp into the version folder
    /// (a same-volume <c>Directory.Move</c> rename), records the version entry
    /// on the manifest, and re-evaluates <see cref="ModVersion.IsLatest"/> over
    /// the container's versions (the effective-timestamp key; see
    /// <see cref="ModVersion.IsLatest"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transactional overwrite (atomicity contract):</b> the temp directory
    /// is populated first; only on a successful return from
    /// <paramref name="populateFolder"/> does the repo delete the prior version
    /// folder (if any) and rename the temp into its place. On any exception from
    /// <paramref name="populateFolder"/> the temp is deleted (best-effort), the
    /// existing version folder is left UNTOUCHED (for a dedup re-import: the old
    /// version's files survive intact), and the manifest is unchanged. A failed
    /// re-import is therefore non-destructive: the caller sees the original
    /// exception and the mod on disk is exactly as it was before the call.</para>
    /// <para>
    /// <b>Upsert by <see cref="ModVersion.VersionString"/></b>: re-adding a
    /// version whose <paramref name="versionString"/> already exists on the
    /// container reuses its folder (the temp is swapped into the existing
    /// folder name); the reused entry's
    /// <see cref="ModVersion.ImportedAt"/> is left unchanged (a re-import
    /// refreshes the files, not the import stamp), but
    /// <see cref="ModVersion.RemoteUploadedAt"/> +
    /// <see cref="ModVersion.FileId"/> ARE overwritten from the call (a
    /// re-acquired version carries the current remote facts, not the stale
    /// ones from the first import; a manual re-import passes nulls and clears
    /// them). A new <paramref name="versionString"/> creates a new opaque
    /// folder + a new version entry stamped with the current time and the
    /// call's remote facts.</para>
    /// <para>
    /// <b>Latest key:</b> after either branch the repository re-evaluates
    /// <see cref="ModVersion.IsLatest"/> over ALL of the container's versions
    /// with the effective-timestamp key (see <see cref="ModVersion.IsLatest"/>).
    /// Importing an older file therefore never flips latest, and a dedup
    /// re-import promotes the reused entry only when its refreshed remote
    /// timestamp makes it newly newest. Containers whose versions all have a
    /// null <see cref="ModVersion.RemoteUploadedAt"/> (manual imports, linked)
    /// order purely by <see cref="ModVersion.ImportedAt"/>; mixed
    /// null/non-null manifests coalesce per-entry with no migration.</para>
    /// <para>
    /// <b>Display metadata is container-scoped, not version-scoped.</b> A
    /// non-null <paramref name="displayMetadata"/> replaces
    /// <see cref="ModContainer.DisplayMetadata"/> in the same manifest update
    /// as the version mutation (a new version or a dedup); <c>null</c> leaves
    /// any prior value untouched, so a manual re-import (folder/archive via the
    /// picker, no metadata argument) never erases a prior Nexus acquisition or
    /// backfill. A <paramref name="populateFolder"/> failure leaves both the
    /// version files and the prior metadata unchanged: the manifest write is
    /// never reached.</para>
    /// </remarks>
    /// <param name="containerId">The target container.</param>
    /// <param name="versionString">The raw release tag (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>). Dedup key within the container.</param>
    /// <param name="populateFolder">A callback that receives the absolute path
    /// of an empty temp directory (created by the repo, a sibling of the final
    /// version folder) and populates it: extract an archive, copy a folder, etc.
    /// On success the repo atomically swaps the temp into the version folder; on
    /// a thrown exception the temp is deleted and the existing version folder is
    /// left untouched.</param>
    /// <param name="remoteUploadedAt">Optional remote-publish timestamp (UTC)
    /// captured at acquisition for remote-source mods (Nexus). Recorded on the
    /// version entry in BOTH branches: a new version creates the entry with it,
    /// a dedup re-import overwrites the reused entry's value (matching how dedup
    /// refreshes files). <c>null</c> for manual imports (folder/archive) + non-
    /// remote sources, which aren't update-checked anyway. Source-agnostic:
    /// Integrations (the acquisition layer) owns Nexus metadata + passes it
    /// through; this seam does not know about Nexus.</param>
    /// <param name="remoteFileId">Optional remote-source file id (the Nexus
    /// file id) captured at acquisition. Recorded on the version entry in BOTH
    /// branches, mirroring <paramref name="remoteUploadedAt"/>: a new version
    /// creates the entry with it, a dedup re-import overwrites the reused
    /// entry's value (so the first re-acquisition backfills a legacy entry).
    /// <c>null</c> for manual imports + non-remote sources. Source-agnostic:
    /// Integrations owns Nexus metadata + passes it through; this seam does
    /// not know about Nexus.</param>
    /// <param name="displayMetadata">Optional source-agnostic display metadata
    /// captured at acquisition for remote-source mods (Nexus) and applied to
    /// the container in the same manifest update as the version mutation. A
    /// non-null value replaces the container's
    /// <see cref="ModContainer.DisplayMetadata"/>; <c>null</c> (the default,
    /// including a manual re-import) preserves any prior value, so a re-import
    /// never erases a prior Nexus acquisition or backfill. Source-agnostic:
    /// Integrations owns the Nexus DTO mapping + passes the result through;
    /// this seam does not know about Nexus.</param>
    /// <returns>The updated container (with the new/reused version entry
    /// recorded).</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="containerId"/> is
    /// unknown.</exception>
    ModContainer AddVersion(
        Guid containerId,
        string versionString,
        Action<string> populateFolder,
        DateTimeOffset? remoteUploadedAt = null,
        int? remoteFileId = null,
        ModDisplayMetadata? displayMetadata = null);

    /// <summary>
    /// Renames a container's display label (the on-disk
    /// <c>container.json</c> <c>Name</c> field) and persists the manifest.
    /// Identity (<see cref="ModContainer.Id"/>) is unchanged: the on-disk
    /// container directory is keyed by <see cref="ModContainer.Id"/>, so it does
    /// not move. No-op + returns <c>null</c> when the container is unknown, and a
    /// no-op returning the unchanged container when the stored name already
    /// equals <paramref name="newName"/> (ordinal). For an
    /// <see cref="UntrackedSource"/> container the untracked-name index is kept
    /// consistent (the old name key is dropped, the new one recorded); for other
    /// sources the index is untouched (Nexus identity is on the source record,
    /// not the name).
    /// </summary>
    /// <param name="containerId">The target container.</param>
    /// <param name="newName">The new display name.</param>
    /// <returns>The updated container, or <c>null</c> when the container id is
    /// unknown.</returns>
    ModContainer? RenameContainer(Guid containerId, string newName);

    /// <summary>
    /// Edits a managed container's import details in one atomic manifest write:
    /// the display <paramref name="name"/>, the source provenance
    /// (<paramref name="source"/>), and the release tag recorded on the
    /// container's latest version (<paramref name="versionTag"/>). The
    /// correction surface for a container whose import details were wrong or
    /// incomplete: <see cref="ModContainer.Id"/> never changes, so every
    /// profile reference (position, enabled, policy, lock) survives the edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Name:</b> a display rename folding <see cref="RenameContainer"/>
    /// semantics (the untracked-name index is kept coherent for untracked
    /// containers). Must be non-whitespace.</para>
    /// <para>
    /// <b>Downloaded mods are not editable:</b> a version record is
    /// download-grounded when it carries a <see cref="ModVersion.FileId"/> OR
    /// a <see cref="ModVersion.RemoteUploadedAt"/> (only the download path
    /// ever records either fact; the timestamp widens the evidence to
    /// downloads from before FileId persistence). When ANY version on the
    /// container is grounded, every edit throws
    /// <see cref="InvalidOperationException"/>, name-only included: there is
    /// no degraded editing surface for a downloaded mod. The guard is
    /// enforced here, not just in the UI: programmatic callers get the same
    /// refusal.</para>
    /// <para>
    /// <b>The name is Untracked-only:</b> a Nexus mod's name comes from Nexus
    /// (the update check's name-sync renames the container when Nexus's name
    /// changes, so a user-typed name would be reverted), so an edit whose
    /// destination is <see cref="NexusSource"/> throws when it changes the
    /// name. The rule follows the destination: switching to Untracked may
    /// rename, associating to Nexus keeps the name it had.</para>
    /// <para>
    /// <b>Identity reset:</b> changing the identity (a different Nexus id, or
    /// a switch between Nexus and Untracked) keeps only the latest version's
    /// local facts (its folder + <see cref="ModVersion.ImportedAt"/>; its
    /// remote claims, the tag, <see cref="ModVersion.FileId"/>, and
    /// <see cref="ModVersion.RemoteUploadedAt"/>, are reset for the new
    /// identity) and REMOVES every older version (manifest entry + on-disk
    /// folder). Because removal destroys data, a multi-version identity change
    /// requires <paramref name="removeOlderVersions"/> = <c>true</c>; with
    /// <c>false</c> it throws
    /// <see cref="RemovalConfirmationRequiredException"/> (an
    /// <see cref="InvalidOperationException"/> subclass, catchable
    /// specifically) so the primitive can never silently destroy versions.
    /// The caller owns the confirm (the edit card shows an explicit removal
    /// notice before passing <c>true</c>).</para>
    /// <para>
    /// <b>Retag:</b> a same-identity edit retags only the latest version
    /// record. <paramref name="versionTag"/> may be empty ONLY for the
    /// programmatic association path (an empty tag marks the derived
    /// version-unknown state); the edit card always passes non-empty when
    /// saving as Nexus, and empty when saving as Untracked (a single-version
    /// identity change to Untracked clears the tag; a non-empty tag with an
    /// Untracked destination is rejected, since an untracked container is
    /// single-version by construction). A tag colliding with another
    /// surviving version's tag on the same container throws
    /// <see cref="InvalidOperationException"/> (the same upsert-key conflict
    /// <see cref="AddVersion"/> would hit).</para>
    /// <para>
    /// <b>Invariant guard:</b> the repository holds one container per
    /// (source, identity); a Nexus identity another container already carries
    /// is rejected with <see cref="InvalidOperationException"/>. Linked
    /// containers (either side) are rejected: a linked container's identity is
    /// its external path and is never edited.</para>
    /// <para>
    /// Version folders of surviving versions never move, so a profile's
    /// <c>PinnedPolicy</c> pins onto surviving versions stay valid; pins onto
    /// removed versions degrade to the existing staging skip-with-warning
    /// path.</para>
    /// </remarks>
    /// <param name="containerId">The target container.</param>
    /// <param name="name">The new display name (non-whitespace).</param>
    /// <param name="source">The new source provenance (Untracked or Nexus; a
    /// <see cref="LinkedSource"/> is rejected).</param>
    /// <param name="versionTag">The release tag to record on the latest
    /// version. Empty is valid only for the programmatic association path
    /// (Nexus) and required for the Untracked destination; a non-empty tag
    /// with an Untracked destination throws.</param>
    /// <param name="removeOlderVersions">Whether a multi-version identity
    /// change may remove the older versions. Ignored unless the identity
    /// changes; required <c>true</c> then when more than one version
    /// exists.</param>
    /// <returns>The updated container, or <c>null</c> when the container id is
    /// unknown (mirroring <see cref="RenameContainer"/>).</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or
    /// whitespace, <paramref name="source"/> is a
    /// <see cref="LinkedSource"/>, or <paramref name="versionTag"/> is
    /// non-empty with an Untracked destination.</exception>
    /// <exception cref="RemovalConfirmationRequiredException">An identity
    /// change on a multi-version container was made without
    /// <paramref name="removeOlderVersions"/>.</exception>
    /// <exception cref="InvalidOperationException">The container is
    /// download-grounded (any version carries a FileId or a
    /// RemoteUploadedAt) and refuses every edit; a Nexus-destination edit
    /// changes the name; the new tag collides with another
    /// surviving version's tag; the target container is linked; or another
    /// container already carries the new Nexus identity.</exception>
    ModContainer? EditImportDetails(
        Guid containerId,
        string name,
        ModSource source,
        string versionTag,
        bool removeOlderVersions);

    /// <summary>
    /// Initializes a container's <see cref="ModContainer.DisplayMetadata"/> when
    /// it is still <c>null</c> (never fetched). An atomic, missing-only
    /// initialization: the write + the manifest persist run under the
    /// repository's existing lock, and any container whose
    /// <see cref="ModContainer.DisplayMetadata"/> is already non-null (whether
    /// equal or different) returns <c>false</c> with no rewrite. Source-agnostic:
    /// the caller supplies an already-normalized
    /// <see cref="ModDisplayMetadata"/> (Integrations owns the Nexus DTO mapping).
    /// </summary>
    /// <remarks>
    /// This is the missing-only initialization seam. A refresh of an already-
    /// populated container (e.g. a re-acquisition that fetched newer summary or
    /// thumbnail text) goes through <see cref="AddVersion"/>'s non-null
    /// <c>displayMetadata</c> argument, which replaces the prior value in the
    /// same manifest update as the version mutation. This method never
    /// overwrites a value that is already present, so a concurrent writer
    /// (acquisition, another backfill, a manual edit) cannot be silently
    /// clobbered by a stale fetch that raced between the Get and the write.
    /// </remarks>
    /// <param name="containerId">The target container.</param>
    /// <param name="metadata">The display metadata to initialize with. Must not
    /// be <c>null</c>.</param>
    /// <returns><c>true</c> when the metadata was set + persisted (the
    /// container existed and its <see cref="ModContainer.DisplayMetadata"/> was
    /// <c>null</c>). <c>false</c> when the container id is unknown or its
    /// <see cref="ModContainer.DisplayMetadata"/> is already non-null; the
    /// manifest is not rewritten in either case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is
    /// <c>null</c>.</exception>
    bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata);

    /// <summary>
    /// Removes a version from the container's manifest + deletes its folder
    /// (idempotent: a missing container or folder is a no-op). If the removed
    /// version carried <see cref="ModVersion.IsLatest"/>, the newest remaining
    /// version by the effective-timestamp key (see
    /// <see cref="ModVersion.IsLatest"/>) is promoted.
    /// </summary>
    void RemoveVersion(Guid containerId, string versionFolder);

    /// <summary>
    /// Resolves the absolute on-disk path of a container's version folder. The
    /// repository is the path authority; paths are derived, never stored. Does
    /// not check existence (the caller decides what to do when the folder is
    /// absent).
    /// </summary>
    string GetVersionFolderPath(Guid containerId, string versionFolder);

    /// <summary>
    /// Garbage-collects unreferenced versions + empty containers. Every
    /// <c>(containerId, versionFolder)</c> not in <paramref name="referenced"/>
    /// is dropped (manifest entry + on-disk folder); containers left with zero
    /// versions are removed entirely (manifest + directory), <em>unless</em> a
    /// caller marked the container id itself as referenced (the linked-mod path:
    /// a <see cref="LinkedSource"/> container has no versions, so it is kept
    /// solely by containerId reference). Idempotent; intended to run at startup
    /// so a clean state is enforced.
    /// </summary>
    /// <param name="referenced">The set of <c>(containerId, versionFolder)</c>
    /// pairs still referenced by some profile (the caller collects these by
    /// resolving each profile entry's policy against its container). A linked
    /// container is referenced by adding its <c>(containerId, <c>string.Empty</c>)</c>
    /// pair (the empty version folder is a sentinel that never matches a real
    /// version folder, so it cannot affect version dropping; its only role is
    /// to mark the containerId as referenced).</param>
    void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced);

    /// <summary>
    /// Reports whether a linked container's external folder is currently
    /// available on disk. Returns <c>true</c> for any container that is not a
    /// <see cref="LinkedSource"/> (managed containers have no external content;
    /// their availability is checked separately at stage time) and for unknown
    /// ids (defensive; callers should only query linked rows they hold). The
    /// value is a transient, in-memory snapshot seeded when the container is
    /// recorded and recomputed when the index is rebuilt; staging re-checks
    /// <c>Directory.Exists</c> independently and does not rely on this cached
    /// flag.
    /// </summary>
    bool IsExternalAvailable(Guid containerId);
}
