namespace Modificus.Curator.Mods;

/// <summary>
/// Imports a local mod source (a folder OR an archive) into the global mod
/// repository, and links an external mod folder into the repository without
/// copying it.
/// </summary>
/// <remarks>
/// <para>
/// Import resolves (or creates) the container for the source, validates the
/// source's structure, then extracts an archive / copies a folder into the
/// repository-managed opaque version folder via
/// <see cref="IModRepository.AddVersion"/>. Container dedup: Untracked by name,
/// Nexus by mod id. Version dedup: re-importing the same version tag reuses its
/// folder (refreshed); a new tag creates a new version + flips
/// <see cref="ModVersion.IsLatest"/>.</para>
/// <para>
/// <b>Source validation (both kinds):</b> the source must contain exactly one
/// base directory with a <c>&lt;base&gt;.mod</c> descriptor inside it (the
/// descriptor filename matches the base folder name). An archive is inspected
/// <em>before</em> extraction (single top-level folder, no loose top-level
/// files, matching descriptor); a folder is checked directly (non-empty +
/// matching descriptor). An invalid source throws
/// <see cref="InvalidOperationException"/> immediately, placing no files and
/// creating no container/version. The validated base folder is preserved under
/// <c>&lt;versionFolder&gt;/&lt;base&gt;/</c> for both kinds (the folder import
/// copies the folder <em>itself</em>, not its contents), which is what staging's
/// base-folder discovery relies on. Mods bake their folder name into their code,
/// so the base folder name is load-bearing at staging time.</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds (import
/// the repository copy, then reference it from the profile). The caller is
/// expected to catch a structure-validation (or I/O) failure per mod and surface
/// it rather than crash.</para>
/// <para>
/// <b>Base-name collision hard-block is the caller's responsibility, not
/// <see cref="Import"/>'s.</b> Before importing, the caller may peek the base
/// name (via <see cref="GetBaseName"/>) + the would-be container (via
/// <see cref="FindExistingContainer"/>) and ask
/// <c>IProfileService.GetBaseNameCollision</c> whether any existing profile mod
/// (a different container) resolves to the same base name. Two mods with the
/// same base folder name can't coexist in one profile (the loader can't tell
/// them apart). <see cref="Import"/> itself is unconditional so direct
/// programmatic imports remain usable.</para>
/// <para>
/// <b><see cref="LinkFolder"/></b> is the no-copy add: it records an external
/// folder as a repository container (metadata only) and is staged directly from
/// the external path at launch. It reuses the folder-shape validator (the picked
/// folder must BE the base and contain <c>&lt;base&gt;.mod</c>), rejects any
/// target that overlaps a Curator-managed root, and returns the existing
/// container id on a re-link of the same path (a refresh; copies/deletes
/// nothing).</para>
/// </remarks>
public interface IModImportService
{
    /// <summary>
    /// Imports a local mod source into the global repository, resolving (or
    /// creating) the container + version.
    /// </summary>
    /// <param name="sourcePath">Absolute path to a folder OR an archive file on
    /// disk. A file is treated as an archive when SharpCompress recognizes its
    /// contents (magic-byte detection, format-agnostic: zip, 7z, rar, and the
    /// others SharpCompress supports); a directory is treated as a folder source
    /// and recursively copied. Detection is by content, not by extension.</param>
    /// <param name="modName">The container display name + the untracked dedup
    /// key. Confined to a single direct child of the mod root: it must not
    /// contain path separators, <c>..</c>, or be an absolute path.</param>
    /// <param name="source">The mod's source provenance (Untracked / Nexus).
    /// Nexus dedups by its source identity; Untracked by
    /// <paramref name="modName"/>.</param>
    /// <param name="version">The raw release tag string (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>); the version's dedup key within the container. Pass
    /// <see cref="string.Empty"/> for an untracked/local import with no tag.</param>
    /// <param name="remoteUploadedAt">Optional remote-publish timestamp (UTC)
    /// for remote-source mods (Nexus); captured by the acquisition layer at
    /// download time and forwarded here so <see cref="UpdateCheckService"/>
    /// can compare publish dates rather than import dates. <c>null</c> for
    /// manual imports (folder/archive via the picker or drag-and-drop) and
    /// non-Nexus sources. Forwarded to <see cref="IModRepository.AddVersion"/>
    /// as the version entry's <see cref="ModVersion.RemoteUploadedAt"/>.</param>
    /// <param name="displayMetadata">Optional source-agnostic display metadata
    /// for the container, captured by the acquisition layer at download time
    /// and forwarded to <see cref="IModRepository.AddVersion"/>. A non-null
    /// value replaces the container's
    /// <see cref="ModContainer.DisplayMetadata"/> in the same manifest update
    /// as the version mutation; <c>null</c> (the default, including a manual
    /// re-import) preserves any prior value, so a re-import never erases a
    /// prior Nexus acquisition or backfill. Source-agnostic: Integrations owns
    /// the Nexus DTO mapping + passes the result through; this seam does not
    /// know about Nexus.</param>
    /// <returns>The <c>(containerId, versionId)</c> pair the caller feeds to
    /// <c>IProfileService.AddMod(profileId, containerId, policy)</c>.
    /// <c>versionId</c> is the imported version's opaque on-disk folder id (a
    /// <see cref="ModVersion.Folder"/> value) so the caller can construct a
    /// <see cref="PinnedPolicy"/> pinning the profile entry to exactly the
    /// version just imported. The display tag (<see cref="ModVersion.VersionString"/>)
    /// is recorded in the container manifest; it is not returned here.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="modName"/>
    /// is null/whitespace, or is not a single direct child of the mod root
    /// (it contains path separators, <c>..</c>, or is an absolute path).</exception>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="sourcePath"/>
    /// does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the source's
    /// structure is invalid (not exactly one base directory with a matching
    /// <c>&lt;base&gt;.mod</c> descriptor). No files are placed and no
    /// container/version is created.</exception>
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure (copy,
    /// delete, extract).</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if an archive is
    /// malformed.</exception>
    (Guid ContainerId, string VersionId) Import(
        string sourcePath,
        string modName,
        ModSource source,
        string version,
        DateTimeOffset? remoteUploadedAt = null,
        ModDisplayMetadata? displayMetadata = null);

    /// <summary>
    /// Peeks the mod's base folder name from a source <em>without</em> creating
    /// a container or version (a read-only validation). Used by the add flow to
    /// pre-check a base-name collision against the active profile BEFORE
    /// importing.
    /// </summary>
    /// <param name="sourcePath">Absolute path to a folder OR an archive file on
    /// disk (same detection as <see cref="Import"/>: a file is inspected as an
    /// archive when SharpCompress recognizes its contents via magic-byte
    /// detection, format-agnostic; a directory is treated as a folder
    /// source).</param>
    /// <returns>The validated base folder name (the single top-level directory for
    /// an archive, the picked folder's own name for a folder). The descriptor
    /// filename (<c>&lt;base&gt;.mod</c>) matches the base folder name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is
    /// null.</exception>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="sourcePath"/>
    /// does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the source's structure
    /// is invalid (the same validation as <see cref="Import"/>: not exactly one
    /// base directory with a matching <c>&lt;base&gt;.mod</c> descriptor). No
    /// files are placed and no container/version is created.</exception>
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure reading
    /// the source.</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if an archive is
    /// malformed.</exception>
    /// <remarks>
    /// This is a pure peek: it validates the source structure (reusing the same
    /// private validation as <see cref="Import"/>, so the two cannot drift) and
    /// returns the base name, but creates no container, no version, and places no
    /// files. The add flow calls this before <see cref="Import"/> to decide
    /// whether the import would collide with an existing profile mod; the
    /// subsequent <see cref="Import"/> re-validates (validation is cheap, and
    /// <see cref="Import"/> must remain self-validating for direct callers).
    /// </remarks>
    string GetBaseName(string sourcePath);

    /// <summary>
    /// Resolves the container an import of <c>(<paramref name="source"/>,
    /// <paramref name="modName"/>)</c> <em>would</em> dedup to, if one already
    /// exists, without creating anything. Returns <c>null</c> if the import would
    /// create a new container.
    /// </summary>
    /// <param name="source">The mod's source provenance. Untracked dedups by
    /// <paramref name="modName"/>; Nexus by source identity; Linked by
    /// normalized <see cref="LinkedSource.ExternalPath"/>.</param>
    /// <param name="modName">The container display name + the untracked dedup
    /// key. Ignored for Nexus + Linked (their identity is on the source
    /// record).</param>
    /// <returns>The existing container the import/link would reuse, or
    /// <c>null</c> if it would create a new one.</returns>
    /// <remarks>
    /// Used by the add flow to exclude a re-add of a mod already in the profile
    /// from the base-name collision check: a re-add resolves to the same
    /// container, and <c>IProfileService.AddMod</c> is idempotent on its
    /// containerId, so it must NOT be flagged as a collision. Mirrors
    /// <see cref="Import"/>'s container resolution minus the create, so the dedup
    /// rules live in exactly one place (this service). For Linked the lookup
    /// matches <see cref="LinkFolder"/>'s refresh path: a linked container for
    /// the same <see cref="LinkedSource.ExternalPath"/> is returned.
    /// </remarks>
    ModContainer? FindExistingContainer(ModSource source, string modName);

    /// <summary>
    /// Links an external mod folder into the repository <b>without copying</b>:
    /// records the folder as a <see cref="LinkedSource"/> container (metadata
    /// only) and stages it directly from the external path at launch. The
    /// external folder is the user's; Curator never copies, writes, versions,
    /// renames, or deletes anything inside it.
    /// </summary>
    /// <param name="externalPath">Absolute path to the mod folder to link. The
    /// folder's own name becomes the base name and the container display name;
    /// it must directly contain a <c>&lt;folderName&gt;.mod</c> descriptor (the
    /// same shape a folder import requires, since the loader resolves the
    /// descriptor by the base name).</param>
    /// <returns>The container id (a new one, or the existing one if this exact
    /// external path is already linked, which is a refresh).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="externalPath"/>
    /// is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="externalPath"/> is
    /// not absolute, or cannot be normalized.</exception>
    /// <exception cref="InvalidOperationException">The folder does not exist,
    /// is not readable, has an invalid mod-folder shape (no matching
    /// <c>&lt;base&gt;.mod</c> descriptor), or overlaps a Curator-managed root
    /// (the mods root, the profiles root, or anything they contain) in either
    /// direction. Nothing is created.</exception>
    /// <remarks>
    /// <para>
    /// <b>Containment:</b> a target that overlaps the mods root or the profiles
    /// root (in either direction) is rejected so Curator's own operations never
    /// recurse into the target and staging never creates a cycle. The profiles
    /// root check covers every profile root and every <c>staged/</c> dir.</para>
    /// <para>
    /// <b>Refresh:</b> linking the same normalized external path twice returns
    /// the existing container id. A refresh re-validates the folder shape but
    /// copies, deletes, and rewrites nothing in the target or the manifest.</para>
    /// <para>
    /// This method does NOT add the profile reference (the caller does, via
    /// <c>IProfileService.AddMod</c> with <see cref="ModVersionPolicy.Latest"/>,
    /// which is inert for linked). It does NOT touch the base-name collision
    /// check (that is the caller's job, same as <see cref="Import"/>).</para>
    /// </remarks>
    Guid LinkFolder(string externalPath);
}
