using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Normalizes a Nexus v1 <see cref="ModInfo"/> into the source-agnostic
/// <see cref="ModDisplayMetadata"/> value object. The single normalization
/// path shared by acquisition (<see cref="ModAcquisitionService"/>) and the
/// later stable-v1 backfill, so the rules cannot drift between the two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalization rules</b> (mirrors the spec): trim <see cref="ModInfo.Summary"/>
/// and <see cref="ModInfo.PictureUrl"/>; an empty summary becomes
/// <see cref="string.Empty"/>; an empty, malformed, or non-HTTPS picture URL
/// becomes <c>null</c>; <see cref="ModInfo.ContainsAdultContent"/> is copied
/// verbatim.</para>
/// <para>
/// The HTTPS guard keeps the UI's thumbnail cache on a single scheme: a
/// non-HTTPS value (including an omitted-scheme relative path or an http URL)
/// is treated as no thumbnail rather than fetched-but-empty, since the cache
/// downloader rejects non-HTTPS URLs anyway. <c>picture_url</c> is bound as a
/// nullable string so an absent wire value (distinct from an empty string)
/// round-trips as <c>null</c> before this mapper runs.</para>
/// </remarks>
internal static class ModDisplayMetadataMapper
{
    /// <summary>
    /// Maps <paramref name="info"/> into a <see cref="ModDisplayMetadata"/>.
    /// Never returns <c>null</c>: a fetched result with no display content is
    /// a non-null object whose <see cref="ModDisplayMetadata.Summary"/> is
    /// empty and whose <see cref="ModDisplayMetadata.ThumbnailUrl"/> is
    /// <c>null</c>, so the container can distinguish fetched-but-empty from
    /// not-fetched.
    /// </summary>
    /// <param name="info">The Nexus v1 mod-page payload. Must not be
    /// <c>null</c>.</param>
    /// <returns>The normalized display metadata.</returns>
    public static ModDisplayMetadata ToDisplayMetadata(ModInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var summary = info.Summary?.Trim() ?? string.Empty;

        return new ModDisplayMetadata
        {
            Summary = summary,
            ThumbnailUrl = NormalizeThumbnailUrl(info.PictureUrl),
            IsAdultContent = info.ContainsAdultContent,
        };
    }

    /// <summary>
    /// Trims + validates the Nexus <c>picture_url</c>: returns the trimmed
    /// absolute HTTPS URL, or <c>null</c> for an empty, malformed, or
    /// non-HTTPS value. <see cref="Uri.TryCreate(UriKind, out Uri)"/> rejects
    /// malformed input without throwing; the scheme check then keeps the
    /// thumbnail cache on HTTPS only.
    /// </summary>
    private static string? NormalizeThumbnailUrl(string? pictureUrl)
    {
        if (string.IsNullOrWhiteSpace(pictureUrl))
        {
            return null;
        }

        var trimmed = pictureUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttps ? trimmed : null;
    }
}
