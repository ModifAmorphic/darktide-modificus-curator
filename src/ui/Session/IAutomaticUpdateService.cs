using Modificus.Curator.Integrations;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The opt-in Premium automatic mod-update installer. Chained directly from the
/// update-check runner after a check completes, it sequentially installs flagged
/// updates for the active profile's Nexus Latest mods when the user has enabled
/// it AND a fresh Premium verification passes. Each install routes through the
/// shared <see cref="IModUpdateInstaller"/>, so the manual update action and an
/// automatic batch never install the same mod concurrently and the per-row
/// spinner tracks both paths from one progress source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chained, not subscribed.</b> The runner captures the exact
/// <see cref="UpdateCheckResult"/> from the check invocation (not a potentially
/// raced <see cref="IUpdateCheckService.LastResult"/>) and awaits
/// <see cref="RunAfterCheckAsync"/>. So a manual CheckNow keeps its spinner
/// active through the installations, and an automatic trigger's check + install
/// form one ordered task (asynchronous + non-blocking to the UI, but sequential
/// within the run).</para>
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
/// <b>Sequential batch with per-iteration re-pull.</b> The batch processes the
/// result's updates one at a time. Each iteration re-checks the active profile
/// (a switch mid-batch stops scheduling further entries) and re-pulls the
/// profile's candidates, feeding them to the installer's in-gate eligibility
/// revalidation. Per-mod failures are caught + recorded; they do not abort
/// later mods.</para>
/// <para>
/// <b>Feedback.</b> A fully successful batch is silent. A batch with one or more
/// failures surfaces a single aggregated, localized summary alert after the
/// batch. <see cref="UpdatesApplied"/> is raised when at least one install
/// succeeded so a subscriber can reload the list (the new versions + cleared
/// flags). Per-row spinners come from the installer's
/// <see cref="IModUpdateInstaller.ModUpdateProgress"/> event (raised per
/// attempt), not from this service.</para>
/// </remarks>
public interface IAutomaticUpdateService
{
    /// <summary>
    /// Raised (on the caller's thread) when at least one install in the last
    /// batch succeeded. A subscriber reloads so the new versions + cleared flags
    /// show.
    /// </summary>
    event EventHandler? UpdatesApplied;

    /// <summary>
    /// Runs the automatic-install batch for <paramref name="result"/> scoped to
    /// <paramref name="profileId"/>, after the gates (authoritative success,
    /// updates present, automatic updates enabled, profile still active, fresh
    /// Premium verification). Sequential; each install goes through the shared
    /// <see cref="IModUpdateInstaller"/> (one install at a time, shared with the
    /// manual path). Per-mod failures are isolated; the aggregated summary alert
    /// (if any) is shown after the batch.
    /// </summary>
    /// <param name="result">The exact result captured from the check invocation
    /// (not <see cref="IUpdateCheckService.LastResult"/>).</param>
    /// <param name="profileId">The profile the check ran against (the batch only
    /// runs when this still matches the session's active profile).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default);
}
