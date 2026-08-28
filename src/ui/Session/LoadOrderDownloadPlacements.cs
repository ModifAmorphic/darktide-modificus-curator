using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// One slot in a profile's pending load-order placement: either a container
/// already placed at apply time, or a remote mod id whose download had not
/// landed yet.
/// </summary>
/// <param name="ContainerId">The known container, or null while the slot waits
/// on its download.</param>
/// <param name="ModId">The Nexus mod id of the pending download (0 for a
/// container slot).</param>
public sealed record LoadOrderPlacementSlot(Guid? ContainerId, int ModId);

/// <summary>
/// The profile-scoped pending placement plans for load-order imports whose
/// remote downloads were enqueued: as each queued download completes, the
/// imported load order converges to the file's order instead of leaving the
/// mod appended at the end of the profile.
/// </summary>
/// <remarks>
/// <para><b>Why this exists apart from the download queue:</b> the queue's
/// contract is transport + completion (download, import, register); where a
/// completed mod belongs in one specific profile's order is load-order import
/// policy, and it stays here rather than widening the generic queue. The
/// component only observes the queue's existing
/// <see cref="IModDownloadQueue.ItemChanged"/> completion signal.</para>
/// <para><b>Lifecycle.</b> A plan is recorded at apply time (one per profile;
/// a later import for the same profile supersedes the prior plan). Each
/// completed <see cref="DownloadPurpose.ProfileAdd"/> item whose (profile,
/// mod id) matches a plan resolves its slot and rewrites the profile's order
/// over every resolved container in file order (one
/// <see cref="IProfileService.SetModOrder"/>; the service's own lock
/// projection governs locked entries). The completion's profile registration
/// is guaranteed to have landed first: the queue registers before publishing
/// the terminal <see cref="IModDownloadQueue.ItemChanged"/> through the same
/// FIFO marshal seam. A FAILED item keeps its slot: the queue's
/// <see cref="IModDownloadQueue.Retry"/> admits a fresh item for the same
/// request, and that item's completion still converges (an unretried failure
/// leaves inert session-only intent that a later import supersedes or a
/// profile deletion clears). A CANCELLED item drops its slot (cancellation
/// is authoritative). The plan is discarded when its last pending slot
/// resolves or drops, or when the profile no longer exists; a transient
/// order-write failure keeps the plan so a later completion retries the
/// write.</para>
/// <para><b>Threading.</b> UI-thread only: <see cref="IModDownloadQueue.ItemChanged"/>
/// is raised on the UI thread, and <see cref="Set"/> / <see cref="Clear"/> are
/// called from the load-order view model's apply path.</para>
/// </remarks>
public sealed class LoadOrderDownloadPlacements
{
    private readonly IModDownloadQueue _queue;
    private readonly IProfileService _profiles;
    private readonly ILogger<LoadOrderDownloadPlacements> _logger;

    /// <summary>
    /// One recorded plan: the mutable slot list (pending slots resolve in
    /// place) plus the set of mod ids still awaiting a download.
    /// </summary>
    private sealed class Plan
    {
        public List<LoadOrderPlacementSlot> Slots { get; } = new();
        public HashSet<int> Pending { get; } = new();
    }

    private readonly Dictionary<Guid, Plan> _plans = new();

    public LoadOrderDownloadPlacements(
        IModDownloadQueue queue,
        IProfileService profiles,
        ILogger<LoadOrderDownloadPlacements> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queue.ItemChanged += OnQueueItemChanged;
    }

    /// <summary>
    /// Raised (UI thread) after a placement rewrote a profile's order (the
    /// mod list reloads + flags pending changes for it). Carries the profile
    /// id.
    /// </summary>
    public event EventHandler<Guid>? PlacementApplied;

    /// <summary>Whether the profile still has an unresolved plan (test
    /// observability).</summary>
    public bool HasPending(Guid profileId) => _plans.ContainsKey(profileId);

    /// <summary>
    /// Records (or replaces) the profile's placement plan. Only slots with a
    /// pending download need a plan at all: the apply-time
    /// <see cref="IProfileService.SetModOrder"/> already fixed the order of
    /// every container that existed at apply time.
    /// </summary>
    public void Set(Guid profileId, IReadOnlyList<LoadOrderPlacementSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var plan = new Plan();
        foreach (var slot in slots)
        {
            plan.Slots.Add(slot);
            if (slot.ContainerId is null)
            {
                plan.Pending.Add(slot.ModId);
            }
        }

        if (plan.Pending.Count == 0)
        {
            // Nothing waits on a download: no plan, no later rewrite.
            _plans.Remove(profileId);
            return;
        }

        _plans[profileId] = plan;
        _logger.LogInformation(
            "Recorded a pending load-order placement for profile {Profile}: {Total} slot(s), {Pending} download(s).",
            profileId, plan.Slots.Count, plan.Pending.Count);
    }

    /// <summary>Discards the profile's plan without touching its order.</summary>
    public void Clear(Guid profileId) => _plans.Remove(profileId);

    private void OnQueueItemChanged(DownloadItem item)
    {
        if (item.Purpose != DownloadPurpose.ProfileAdd
            || !_plans.TryGetValue(item.TargetProfileId, out var plan)
            || !plan.Pending.Contains(item.ModId))
        {
            return;
        }

        switch (item.Phase)
        {
            case DownloadPhase.Completed:
                Resolve(item, plan);
                break;
            case DownloadPhase.Canceled:
                Drop(item, plan);
                break;
                // Failed: the slot stays. Retry admits a fresh item for the same
                // (profile, mod id) key, whose completion resolves it; drop would
                // strand the retried download at the end of the profile.
        }
    }

    /// <summary>
    /// A planned download completed: resolve its slot to the landed container
    /// and rewrite the order over every resolved container in file order.
    /// </summary>
    private void Resolve(DownloadItem item, Plan plan)
    {
        var profileId = item.TargetProfileId;
        plan.Pending.Remove(item.ModId);
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            if (slot.ContainerId is null && slot.ModId == item.ModId && item.ContainerId is { } containerId)
            {
                plan.Slots[i] = slot with { ContainerId = containerId };
            }
        }

        try
        {
            ApplyOrder(profileId, plan);
        }
        catch (KeyNotFoundException ex)
        {
            // The profile is gone: the plan (and this download's placement)
            // has nothing left to write to.
            _logger.LogInformation(
                ex, "Dropping the pending load-order placement for profile {Profile}: the profile is gone.", profileId);
            _plans.Remove(profileId);
            return;
        }
        catch (Exception ex) when (IsExpectedWriteFailure(ex))
        {
            // A transient write failure (IO / access / an invalid stored
            // state): keep the plan so a later completion (a sibling landing
            // or a retry) rewrites the order. Inert until then; a later
            // import supersedes it.
            _logger.LogWarning(
                ex, "Writing the pending load-order placement for profile {Profile} failed; the plan is retained for a later retry.",
                profileId);
            return;
        }

        if (plan.Pending.Count == 0)
        {
            _plans.Remove(profileId);
        }
    }

    /// <summary>
    /// A planned download was cancelled: its slot never resolves (cancellation
    /// is authoritative), so it drops out of the converging order (the
    /// survivors' positions stand).
    /// </summary>
    private void Drop(DownloadItem item, Plan plan)
    {
        plan.Pending.Remove(item.ModId);
        plan.Slots.RemoveAll(s => s.ContainerId is null && s.ModId == item.ModId);
        if (plan.Pending.Count == 0)
        {
            _plans.Remove(item.TargetProfileId);
        }
    }

    /// <summary>
    /// One <see cref="IProfileService.SetModOrder"/> over every resolved
    /// container that is still a profile member, in file order. Non-members
    /// (removed by the user while the download ran) are skipped; unlisted
    /// mods keep their relative order after the listed block per the service's
    /// own semantics.
    /// </summary>
    private void ApplyOrder(Guid profileId, Plan plan)
    {
        var members = _profiles.GetModList(profileId)
            .Select(e => e.ContainerId)
            .ToHashSet();
        var order = plan.Slots
            .Where(s => s.ContainerId is { } id && members.Contains(id))
            .Select(s => s.ContainerId!.Value)
            .ToArray();
        if (order.Length == 0)
        {
            return;
        }

        _profiles.SetModOrder(profileId, order);
        _logger.LogInformation(
            "Applied a pending load-order placement for profile {Profile}: {Count} container(s).",
            profileId, order.Length);
        PlacementApplied?.Invoke(this, profileId);
    }

    /// <summary>
    /// The expected non-profile-gone write-failure families (a transient IO /
    /// access / invalid-state failure worth retrying later). A
    /// <see cref="KeyNotFoundException"/> is handled separately (the profile
    /// is gone) before this filter runs.
    /// </summary>
    private static bool IsExpectedWriteFailure(Exception ex) =>
        ex is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException;
}
