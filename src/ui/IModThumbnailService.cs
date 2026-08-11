using Avalonia.Media;

namespace Modificus.Curator.UI;

/// <summary>
/// Loads and caches mod thumbnail images for display in detailed mod rows. The
/// service owns the disk cache, the in-memory decoded-image cache, and the
/// download path; it degrades to <c>null</c> for every expected failure (invalid
/// URL, HTTP failure, oversize data, decode failure, I/O failure) without
/// throwing or surfacing a modal. Cancellation (<see cref="OperationCanceledException"/>)
/// propagates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adult-content policy is NOT this service's responsibility.</b> The caller
/// (the later detailed-row coordinator) decides whether to request a thumbnail
/// for a given row; the service fetches whatever trusted HTTPS URL it is
/// handed.</para>
/// <para>
/// <b>Application-lifetime ownership.</b> Registered as a singleton. Decoded
/// <see cref="IImage"/> instances are kept alive in an in-memory cache for the
/// app lifetime so multiple rows and reloads share them and no bound row
/// observes a disposed image.</para>
/// </remarks>
public interface IModThumbnailService
{
    /// <summary>
    /// Returns the decoded thumbnail for <paramref name="thumbnailUrl"/>, or
    /// <c>null</c> when the URL is invalid/non-HTTPS, the download or decode
    /// fails, or the data is oversize. A cache hit (in-memory or disk) serves
    /// without a network round-trip. Concurrent same-URL calls coalesce into one
    /// load.
    /// </summary>
    /// <param name="thumbnailUrl">The absolute HTTPS thumbnail URL, or
    /// <c>null</c>. Non-HTTPS, relative, malformed, or empty values return
    /// <c>null</c> without network or cache side effects.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates to THIS caller when its own token fires. The shared load runs
    /// uncancellable (<see cref="CancellationToken.None"/>) so cancelling one
    /// caller never cancels another's load; the shared load continues to
    /// completion and may populate the disk + in-memory caches even when every
    /// current caller cancelled.</param>
    /// <returns>The decoded image, or <c>null</c> on any expected failure.</returns>
    Task<IImage?> GetThumbnailAsync(string? thumbnailUrl, CancellationToken ct = default);
}
