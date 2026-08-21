using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The enqueue front for premium mod-update installs: resolves the mod's head
/// file (one file-listing call, no download) and admits an
/// <see cref="DownloadPurpose.UpdateInstall"/> item onto the shared download
/// queue. The queue owns everything downstream under its serial worker: the
/// dequeue-time eligibility revalidation, the acquisition, the
/// acknowledge-on-success, and the <see cref="IModDownloadQueue.UpdatesApplied"/>
/// reload signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>One engine.</b> Both premium install callers (the manual per-row update
/// action and the automatic Premium batch) enqueue through this front, so an
/// update and an nxm click can never hold two acquisitions at once: the
/// queue's single worker is the only gate. A click for a file already live in
/// the queue joins the existing item (the queue's dedupe key is the resolved
/// head file) and pulses its row.</para>
/// <para>
/// <b>Failure presentation stays with the caller.</b> The head resolve is the
/// one step with no row to host a failure on (the API call happens before an
/// item exists), so its exceptions propagate: the manual path surfaces the
/// localized failure alert, the batch aggregates resolve failures into its
/// summary alert. Once an item is admitted, failures land on the row (the
/// queue's Failed phase) and never come back through this front.</para>
/// <para>
/// <b>Threading.</b> Invoked on the UI thread by both callers; the resolve's
/// I/O runs inside the acquisition service. No
/// <c>ConfigureAwait(false)</c> (the UI-layer convention: the caller's context
/// is kept).</para>
/// </remarks>
public sealed class ModUpdateEnqueuer
{
    private readonly IModAcquisitionService _acquisition;
    private readonly IModDownloadQueue _downloadQueue;
    private readonly IProfileService _profiles;

    public ModUpdateEnqueuer(
        IModAcquisitionService acquisition,
        IModDownloadQueue downloadQueue,
        IProfileService profiles)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    /// <summary>
    /// Resolves the current head release of <paramref name="modId"/> and
    /// enqueues one UpdateInstall item for the container.
    /// </summary>
    /// <param name="modId">The Nexus mod id to update.</param>
    /// <param name="containerId">The repository container the update flag was
    /// recorded against (the eligibility revalidation's subject).</param>
    /// <param name="displayName">The row name shown while the item is
    /// queued.</param>
    /// <param name="expectedVersion">The installed version the flag was
    /// recorded against (the eligibility version rule; the dequeue-time
    /// revalidation re-checks it against the container's current state).</param>
    /// <param name="profileId">The profile whose flagged entry is
    /// acknowledged on success (captured as the item's target).</param>
    /// <param name="ct">Cancellation token, honored through the resolve;
    /// <see cref="OperationCanceledException"/> propagates.</param>
    /// <returns>The admitted (or joined, when the same head file is already
    /// live in the queue) item.</returns>
    /// <exception cref="KeyNotFoundException">The target profile no longer
    /// exists (nothing was enqueued).</exception>
    /// <exception cref="Exception">The head resolve failed (the API call
    /// before any item exists; nothing was enqueued).</exception>
    public async Task<DownloadItem> EnqueueLatestAsync(
        int modId,
        Guid containerId,
        string displayName,
        string expectedVersion,
        Guid profileId,
        CancellationToken ct = default)
    {
        // The profile name is display-only on the request, but reading it
        // first also fails fast (no API call) when the target profile was
        // deleted between the check and this enqueue.
        var profileName = _profiles.GetProfile(profileId).Name;

        var (fileId, _) = await _acquisition.ResolveLatestNexusAsync(
            NexusGameIdentity.DarktideDomain, modId, ct);

        return _downloadQueue.Enqueue(new ModDownloadRequest(
            NexusGameIdentity.DarktideDomain, modId, fileId,
            DownloadPurpose.UpdateInstall,
            containerId, displayName, profileId, profileName,
            ExpectedVersion: expectedVersion));
    }
}
