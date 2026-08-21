using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The result of one Nexus acquisition: where the mod landed in the repository
/// plus the facts the caller needs to register or present it.
/// </summary>
/// <param name="ContainerId">The repository container the mod was imported
/// into (existing container reused when the mod was already known).</param>
/// <param name="VersionId">The imported version's opaque folder id (the
/// <see cref="PinnedPolicy.VersionId"/> foreign key).</param>
/// <param name="Version">The acquired file's release tag (e.g.
/// <c>"1.2"</c>), for display.</param>
/// <param name="IsHeadFile">Whether the acquired file is the mod's newest
/// non-archived MAIN file (the same <c>LatestMain</c> filter the download path
/// uses), computed from the files listing the acquisition already reads; zero
/// extra API calls.</param>
public sealed record NexusAcquisitionResult(
    Guid ContainerId,
    string VersionId,
    string Version,
    bool IsHeadFile);

/// <summary>
/// Acquires a mod from a remote source: resolves the download link, fetches the
/// mod's metadata, downloads the archive to a temp file, and imports it into the
/// unified mod repository via <see cref="IModImportService.Import"/>. The caller
/// owns profile registration: this service returns the
/// <see cref="NexusAcquisitionResult"/> and the caller feeds it to
/// <c>IProfileService.AddMod</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nexus-only: the service resolves the download link, fetches the mod's
/// metadata, downloads the archive to a temp file, and imports it into the
/// unified mod repository via <see cref="IModImportService.Import"/>. The
/// signature carries an <see cref="IProgress{T}"/> so a caller can wire a
/// progress indicator without retooling the seam.</para>
/// <para>
/// <b>No degraded metadata fallback.</b> If the metadata fetch (mod name or file
/// version) fails, the acquisition fails with a clear error. A mod stored under
/// its numeric id as a name is worse than a clean failure message; the caller
/// surfaces the error and nothing partial lands in the repository.</para>
/// <para>
/// <b>Temp file cleanup.</b> The downloaded archive lives in a temp file that is
/// deleted once <see cref="IModImportService.Import"/> returns (the import
/// extracts/copies the content into the repository, so the source archive is no
/// longer needed). On any failure the temp file is also deleted, so no partial
/// state is left on disk.</para>
/// </remarks>
public interface IModAcquisitionService
{
    /// <summary>
    /// Downloads a Nexus mod file, extracts it into the repository via
    /// <see cref="IModImportService.Import"/>, and returns the
    /// <see cref="NexusAcquisitionResult"/> the caller feeds to
    /// <c>IProfileService.AddMod</c>.
    /// </summary>
    /// <param name="gameDomain">The Nexus game domain (the host of the
    /// <c>nxm://</c> URL; see <see cref="NexusGameIdentity.DarktideDomain"/>).</param>
    /// <param name="modId">The Nexus mod id.</param>
    /// <param name="fileId">The Nexus file id (the specific release to
    /// download).</param>
    /// <param name="nxmKey">The per-file download key from the <c>nxm://</c> URL,
    /// or <c>null</c> when absent. When both <paramref name="nxmKey"/> and
    /// <paramref name="nxmExpires"/> are non-null, the free-user download-link
    /// endpoint is used (the key + expiry are per-file tokens for free users);
    /// otherwise the premium (auth-only) endpoint is used. The key is NOT a
    /// substitute for auth: the caller gates on
    /// <c>NexusConfig.AuthMethod != None</c> first.</param>
    /// <param name="nxmExpires">The per-file download expiry (epoch seconds) from
    /// the <c>nxm://</c> URL, or <c>null</c> when absent.</param>
    /// <param name="progress">Optional progress receiver: cumulative bytes
    /// received plus the total from the response <c>Content-Length</c> when the
    /// server sent one (null total = unknown; no separate HEAD call is made), or
    /// <c>null</c> when the caller has no progress UI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The acquisition result (container + version ids, the release
    /// tag, and whether the file is the mod's current head release). The
    /// import service resolves (or creates) the container keyed by
    /// <see cref="NexusSource.ModId"/> and adds the version keyed by the file's
    /// version string.</returns>
    /// <exception cref="NexusApiException">The Nexus API returned a non-success
    /// response (download links, mod info, or mod files). Thrown by
    /// <see cref="INexusClient"/>.</exception>
    /// <exception cref="InvalidOperationException">The mod metadata was unusable:
    /// the mod name was empty, or the requested <paramref name="fileId"/> was not
    /// listed among the mod's files. No degraded fallback.</exception>
    /// <exception cref="System.IO.IOException">The archive download or the temp
    /// file could not be completed.</exception>
    /// <exception cref="System.IO.InvalidDataException">The downloaded archive is
    /// malformed (propagated from <see cref="IModImportService.Import"/>).</exception>
    Task<NexusAcquisitionResult> AcquireFromNexusAsync(
        string gameDomain,
        int modId,
        int fileId,
        string? nxmKey = null,
        long? nxmExpires = null,
        IProgress<(long Received, long? Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Acquires the latest MAIN release of a Nexus mod: resolves the newest
    /// non-archived MAIN file via <see cref="ResolveLatestNexusAsync"/>, then
    /// delegates to <see cref="AcquireFromNexusAsync"/> with the resolved
    /// <paramref name="fileId"/> + <c>null</c> nxm key/expires (the premium /
    /// auth-only download path). Use when the mod id is known but the file id is
    /// not, and the current release should be picked.
    /// </summary>
    /// <param name="gameDomain">The Nexus game domain (e.g.
    /// the Darktide domain; see <see cref="NexusGameIdentity.DarktideDomain"/>).</param>
    /// <param name="modId">The Nexus mod id.</param>
    /// <param name="progress">Optional cumulative-bytes progress receiver.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The acquisition result (the same shape
    /// <see cref="AcquireFromNexusAsync"/> returns; the acquired file is the
    /// head by construction, so <see cref="NexusAcquisitionResult.IsHeadFile"/>
    /// is true).</returns>
    /// <exception cref="InvalidOperationException">The mod has no MAIN files (or
    /// none that are non-archived), so no "latest release" exists to acquire.
    /// The caller surfaces this as a user-facing alert.</exception>
    /// <exception cref="NexusApiException">The Nexus API returned a non-success
    /// response (propagated from <see cref="INexusClient"/>).</exception>
    /// <exception cref="System.IO.IOException">The archive download or the temp
    /// file could not be completed.</exception>
    /// <exception cref="System.IO.InvalidDataException">The downloaded archive is
    /// malformed (propagated from <see cref="IModImportService.Import"/>).</exception>
    Task<NexusAcquisitionResult> AcquireLatestNexusAsync(
        string gameDomain,
        int modId,
        IProgress<(long Received, long? Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the newest non-archived MAIN file of a Nexus mod without
    /// downloading it (one <see cref="INexusClient.ListModFilesAsync"/> call,
    /// nothing else). For callers that know the mod id but need a concrete file
    /// id up front (a queue dedupe key, a pre-download confirmation) and will
    /// acquire the file later through <see cref="AcquireFromNexusAsync"/>.
    /// </summary>
    /// <param name="gameDomain">The Nexus game domain.</param>
    /// <param name="modId">The Nexus mod id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The head file's id and release tag. This is the same
    /// <c>LatestMain</c> resolution <see cref="AcquireLatestNexusAsync"/>
    /// acquires, from one implementation, so the two call sites cannot disagree
    /// on "current release".</returns>
    /// <exception cref="InvalidOperationException">The mod has no MAIN files (or
    /// none that are non-archived).</exception>
    /// <exception cref="NexusApiException">The Nexus API returned a non-success
    /// response (propagated from <see cref="INexusClient"/>).</exception>
    Task<(int FileId, string Version)> ResolveLatestNexusAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default);
}
