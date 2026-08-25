namespace Modificus.Curator.Mods;

/// <summary>
/// One version of a mod, persisted as an entry inside its
/// <see cref="ModContainer"/>'s <c>container.json</c>. The mod's files live at
/// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/&lt;Folder&gt;/</c>; this
/// record is the manifest line that maps that opaque folder to its display
/// <see cref="VersionString"/> + the <see cref="IsLatest"/> flag.
/// </summary>
/// <remarks>
/// <para>Immutable record: mutations go through <see cref="IModRepository.AddVersion"/>
/// / <see cref="IModRepository.RemoveVersion"/>, which rebuild the container's
/// manifest on disk. <see cref="Folder"/> is an opaque unique ID (UUID-derived)
/// never the raw version tag, so filesystem-illegal characters + sanitization
/// collisions are non-issues (the raw tag lives only in
/// <see cref="VersionString"/>, for display).</para>
/// <para>
/// <see cref="IsLatest"/> is a single flag on one version per container (the
/// one a <see cref="LatestPolicy"/> profile resolves to). Moving latest is a
/// one-field manifest edit; profiles resolve dynamically at stage time, so
/// <c>isLatest</c> changing never requires a profile-entry update.</para>
/// <para>
/// Which version carries the flag is decided by the most recent ARRIVAL (the
/// newest <see cref="ImportedAt"/>): when the newest arrival is a manual
/// import (no <see cref="RemoteUploadedAt"/>), that version is latest; when
/// it is a download, the latest is the newest downloaded version by
/// <see cref="RemoteUploadedAt"/> with <see cref="ImportedAt"/> breaking
/// exact ties (manual imports are ignored for latest in that branch). The
/// repository re-evaluates the flag with that rule on every add/remove, so
/// importing an older remote file never flips latest, while a download
/// arriving after a manual import correctly takes the flag. An all-manual
/// container orders purely by <see cref="ImportedAt"/> and an all-download
/// container by <see cref="RemoteUploadedAt"/>. <see cref="IModRepository.AddVersion"/>
/// stamps <see cref="ImportedAt"/> on insert and never changes it;
/// re-importing the same <see cref="VersionString"/> reuses the existing
/// entry (refreshed files + remote facts, unchanged import stamp, so a dedup
/// re-import is not a new arrival).</para>
/// </remarks>
public sealed record ModVersion
{
    /// <summary>
    /// The opaque version-folder ID (UUID-derived). The version's files live at
    /// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/&lt;Folder&gt;/</c>.
    /// Never the raw version tag.
    /// </summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>
    /// The raw release tag (e.g. <c>"1.2"</c>, <c>"v2.0.1"</c>,
    /// <c>"1.0.0-beta"</c>), stored verbatim. Used for display + for resolving
    /// a <see cref="PinnedPolicy"/> pin to this version. Arbitrary source tags
    /// are not SemVer; never parsed or normalized at this layer.
    /// </summary>
    public string VersionString { get; init; } = string.Empty;

    /// <summary>
    /// Whether this is the container's current "latest" version (the one a
    /// <see cref="LatestPolicy"/> profile resolves to). Exactly one version per
    /// container carries this flag, decided by the most recent arrival (the
    /// newest <see cref="ImportedAt"/>): a manual import (no
    /// <see cref="RemoteUploadedAt"/>) with the newest arrival is latest;
    /// otherwise the newest downloaded version by
    /// <see cref="RemoteUploadedAt"/> (exact ties fall to the newer
    /// <see cref="ImportedAt"/>). The repository re-evaluates it on every
    /// add/remove.
    /// </summary>
    public bool IsLatest { get; init; }

    /// <summary>
    /// When this version was first imported (UTC). The fallback ordering key
    /// when <see cref="RemoteUploadedAt"/> is unknown (manual imports, legacy
    /// manifests) and the tie-break for equal remote timestamps. Never changes
    /// on a re-import.
    /// </summary>
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>
    /// When the underlying remote file was published (UTC), captured at
    /// acquisition time for remote-source mods (Nexus). <c>null</c> for
    /// non-remote / manual imports (folder/archive via the picker or
    /// drag-and-drop). Retained on the version entry for provenance; the v2
    /// GraphQL update check relies on the server-computed
    /// <c>viewerUpdateAvailable</c> field rather than comparing this against a
    /// remote timestamp, but the field is still populated at acquisition + used
    /// by the acquisition layer for file resolution.
    /// </summary>
    /// <remarks>
    /// Backward-compatible on disk: a <c>container.json</c> from before this
    /// field existed deserializes it to <c>null</c> (System.Text.Json default
    /// for a missing nullable property). No migration. Owned by the acquisition
    /// layer (Integrations), which passes it through
    /// <see cref="IModImportService.Import"/> / <see cref="IModRepository.AddVersion"/>;
    /// those seams stay source-agnostic (the param is nullable + unused for
    /// non-Nexus).
    /// </remarks>
    public DateTimeOffset? RemoteUploadedAt { get; init; }

    /// <summary>
    /// The remote source's file id this version was acquired from (the Nexus
    /// file id): the exact identity that distinguishes two versions of the
    /// same mod beyond their display tags. <c>null</c> for manual imports
    /// (folder/archive via the picker or drag-and-drop) and legacy manifests;
    /// a re-acquisition overwrites the reused entry's value, so legacy
    /// entries self-heal by attrition.
    /// </summary>
    /// <remarks>
    /// Backward-compatible on disk: a <c>container.json</c> from before this
    /// field existed deserializes it to <c>null</c>. No migration. Owned by
    /// the acquisition layer (Integrations), which passes it through
    /// <see cref="IModImportService.Import"/> / <see cref="IModRepository.AddVersion"/>;
    /// those seams stay source-agnostic (the param is nullable + unused for
    /// non-Nexus).
    /// </remarks>
    public int? FileId { get; init; }
}
