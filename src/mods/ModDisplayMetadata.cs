namespace Modificus.Curator.Mods;

/// <summary>
/// Source-agnostic display metadata for a <see cref="ModContainer"/>: the
/// short summary, a thumbnail URL, and a content-safety flag a UI uses to
/// present a richer row. The fields describe display presentation together, so
/// the object is cohesive rather than a per-field patch surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null vs. empty.</b> A <c>null</c> <see cref="ModContainer.DisplayMetadata"/>
/// means Curator has never retrieved display metadata for the container. A
/// non-null object whose <see cref="Summary"/> is empty and whose
/// <see cref="ThumbnailUrl"/> is <c>null</c> is an authoritative fetched result
/// that simply carries no display content. The two states are distinct: the
/// backfill path treats only <c>null</c> as a candidate.</para>
/// <para>
/// <b>Source-agnostic.</b> The record carries no Nexus-specific shape and the
/// Mods library does not reference Integrations. Integrations owns the
/// normalization from its Nexus DTOs into this object through one internal
/// mapper, so the rule cannot drift between acquisition and backfill.</para>
/// <para>
/// Stored on <c>container.json</c>. Backward compatible on disk: a manifest
/// from before this field existed deserializes <see cref="ModContainer.DisplayMetadata"/>
/// to <c>null</c> (System.Text.Json default for a missing nullable property),
/// so no migration pass or schema version is required.</para>
/// </remarks>
public sealed record ModDisplayMetadata
{
    /// <summary>
    /// A short, single-paragraph summary suitable for a dense row. Empty when
    /// the source carried no summary. Plain text; the UI does not render
    /// markup. Defaults to <see cref="string.Empty"/>.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// The absolute thumbnail URL the UI fetches and caches, or <c>null</c>
    /// when the source carried no picture. The UI accepts only HTTPS URLs, so
    /// a mapper that normalizes a Nexus <c>picture_url</c> into this field
    /// downgrades any non-HTTPS value to <c>null</c>.
    /// </summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Whether the source flags the mod as adult content. Copied verbatim from
    /// the upstream flag; the UI uses it to skip the thumbnail and show the
    /// normal placeholder. No filter, setting, or separate workflow hangs off
    /// this boolean.
    /// </summary>
    public bool IsAdultContent { get; init; }
}
