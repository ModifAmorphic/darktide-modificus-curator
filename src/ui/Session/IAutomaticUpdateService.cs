using Modificus.Curator.Integrations;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The opt-in Premium automatic mod-update installer. Chained directly from the
/// update-check runner after a check completes, it enqueues an UpdateInstall
/// download-queue item for each flagged update of the active profile's Nexus
/// Latest mods when the user has enabled it AND a fresh Premium verification
/// passes. The queue's serial worker owns each install end-to-end (the
/// dequeue-time eligibility revalidation, the acquisition, the acknowledge,
/// the per-row progress via the download morph, and the
/// <see cref="IModDownloadQueue.UpdatesApplied"/> reload signal), so the
/// manual update action and an automatic batch share one engine and can never
/// hold two acquisitions at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chained, not subscribed.</b> The runner captures the exact
/// <see cref="UpdateCheckResult"/> from the check invocation (not a potentially
/// raced <see cref="IUpdateCheckService.LastResult"/>) and awaits
/// <see cref="RunAfterCheckAsync"/>. So a manual CheckNow keeps its spinner
/// active through the enqueue phase, and an automatic trigger's check + enqueue
/// form one ordered task (the downloads themselves run on the queue after the
/// batch returns; their completions reload the list through the queue's own
/// event).</para>
/// <para>
/// <b>Gating.</b> Execution starts only when ALL hold: the result's outcome is
/// authoritative <see cref="CheckOutcome.Success"/>, the result has updates,
/// <c>NexusConfig.AutomaticUpdatesEnabled</c> is on, the active profile still
/// matches the check's profile, and a fresh
/// <see cref="INexusAuthService.GetCurrentStateAsync"/> returns
/// <see cref="NexusAuthState.IsPremium"/> == <c>true</c>. The Premium request
/// fires ONLY when a successful check found updates AND auto-update is enabled,
/// so a regular user or an empty result costs no extra API call. This is
/// independent of <c>NexusConfig.AutoUpdateCheckEnabled</c>: periodic checking
/// being off never disables automatic installation (startup + switch + manual
/// checks still drive it).</para>
/// <para>
/// <b>Stop on profile switch.</b> Each iteration re-checks the active profile
/// (a switch mid-batch stops scheduling further entries), and the service
/// cancels its already-admitted items still waiting for the queue's worker
/// when the session's active profile moves away from an item's target (queued
/// cancel semantics; an item the worker already started completes under the
/// queue's own rules). Per-mod resolve failures are isolated; download
/// failures render on their rows.</para>
/// <para>
/// <b>Feedback.</b> A fully successful batch is silent. Download failures are
/// row-hosted (the queue's Failed phase with dismiss + retry), so the only
/// alert is the single aggregated, localized summary over mods whose head
/// release could not even be resolved (no row exists to host those). The list
/// reload after each successful install comes from the queue's
/// <see cref="IModDownloadQueue.UpdatesApplied"/> event, not from this
/// service.</para>
/// </remarks>
public interface IAutomaticUpdateService
{
    /// <summary>
    /// Runs the automatic-install batch for <paramref name="result"/> scoped to
    /// <paramref name="profileId"/>, after the gates (authoritative success,
    /// updates present, automatic updates enabled, profile still active, fresh
    /// Premium verification): one UpdateInstall enqueue per flagged candidate
    /// through <see cref="ModUpdateEnqueuer"/>. Per-mod resolve failures are
    /// isolated (the aggregated summary alert, if any, is shown after the
    /// batch); cancellation propagates.
    /// </summary>
    /// <param name="result">The exact result captured from the check invocation
    /// (not <see cref="IUpdateCheckService.LastResult"/>).</param>
    /// <param name="profileId">The profile the check ran against (the batch only
    /// runs when this still matches the session's active profile).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default);
}
