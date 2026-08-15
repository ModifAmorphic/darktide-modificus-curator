using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// Default <see cref="IAutomaticUpdateService"/>. Registered as a singleton.
/// See the interface remarks for the gating, revalidation, isolation, and
/// feedback rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>No UI-thread affinity required.</b> Invoked by the runner after it returns
/// to the UI context; the service's awaits (the Premium check, the per-mod
/// acquisitions, the coordinator) yield without blocking the UI thread, and the
/// aggregated alert + the <see cref="UpdatesApplied"/> event fire on the UI
/// thread (the runner's context). No <c>ConfigureAwait(false)</c> is used
/// (UI-layer convention: stay on the captured context).</para>
/// <para>
/// <b>The fresh Premium check is conditional.</b> It fires only when the
/// gating passed (authoritative success with updates + auto-update enabled +
/// active profile matches), so an empty result or a disabled setting never costs
/// an extra API call.</para>
/// </remarks>
internal sealed class AutomaticUpdateService : IAutomaticUpdateService
{
    private readonly IProfileSession _session;
    private readonly IProfileService _profiles;
    private readonly IModRepository _repository;
    private readonly IModAcquisitionService _acquisition;
    private readonly INexusAuthService _auth;
    private readonly IConfigLoader _configLoader;
    private readonly IUpdateStateStore _updateState;
    private readonly UpdateCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<AutomaticUpdateService> _logger;

    public AutomaticUpdateService(
        IProfileSession session,
        IProfileService profiles,
        IModRepository repository,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        IConfigLoader configLoader,
        IUpdateStateStore updateState,
        UpdateCoordinator coordinator,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<AutomaticUpdateService> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler? UpdatesApplied;

    /// <inheritdoc />
    public event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <inheritdoc />
    public async Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default)
    {
        // 1. Outcome gate: only an authoritative success with updates starts the
        //    batch. A no-Nexus-mods, no-auth, rate-limited, failed, or restored
        //    result never installs. A successful result with zero updates also
        //    stops here (nothing to install).
        if (result.Outcome != CheckOutcome.Success || result.Updates.Count == 0)
        {
            return;
        }

        // 2. Setting gate: the user opted in. Read live. Independent of
        //    AutoUpdateCheckEnabled (the periodic-check toggle): periodic checking
        //    being off never disables automatic installation here.
        if (!_configLoader.Load().Integrations.Nexus.AutomaticUpdatesEnabled)
        {
            return;
        }

        // 3. Active-profile gate: the profile the check ran against must still be
        //    the session's active profile. A switch between the check + this point
        //    aborts the batch (do not install into a profile the user navigated
        //    away from).
        if (_session.ActiveProfileId != profileId)
        {
            return;
        }

        // 4. Fresh Premium verification (ONLY when the gates above passed, so an
        //    empty result or a disabled setting never costs an extra API call).
        //    A cached/stale Premium read is insufficient: the user may have let
        //    Premium lapse, so re-verify against the live account state.
        NexusAuthState? state;
        try
        {
            state = await _auth.GetCurrentStateAsync(ct);
        }
        catch (Exception ex)
        {
            // The verify call failed; do not install on an unverified account.
            _logger.LogWarning(ex, "Automatic update aborted: fresh Premium verification failed.");
            return;
        }
        if (state?.IsPremium != true)
        {
            _logger.LogInformation(
                "Automatic update skipped: account is not verified Premium (state={State}).",
                state is null ? "null" : "non-premium");
            return;
        }

        // 5. The sequential install batch. Per-mod revalidation gates each entry;
        //    a profile switch stops the whole batch. Per-mod failures are caught
        //    + recorded; they do not abort later mods.
        var installed = 0;
        var failed = new List<(ModUpdateInfo Info, string Error)>();
        foreach (var info in result.Updates)
        {
            // Re-validate the active profile on every iteration: a switch mid-batch
            // stops scheduling further entries.
            if (_session.ActiveProfileId != profileId)
            {
                _logger.LogInformation(
                    "Automatic update batch stopped: active profile changed mid-batch.");
                break;
            }

            // Re-validate membership / policy / source / version for this entry.
            // A mismatch (removed, re-pinned, source-changed, or already-updated)
            // skips this entry but does not stop the batch.
            if (!IsStillEligible(profileId, info, out var reason))
            {
                _logger.LogDebug(
                    "Automatic update skipped container {Container}: {Reason}.",
                    info.ContainerId, reason);
                continue;
            }

            try
            {
                // Signal row-level progress: the install for this container is
                // starting. Raised before the acquisition so the list VM can show
                // the spinner on this row. The matching active=false is in the
                // finally below (deterministic start/stop per sequential item).
                RaiseProgress(info.ContainerId, active: true);

                // Acquire the global coordinator per mod: one install at a time,
                // shared with the manual update action. The runner serializes the
                // batch, so this await is uncontended in practice, but the
                // coordinator is the single mutual-exclusion point across both
                // paths. No ConfigureAwait(false): stay on the captured UI context.
                using (await _coordinator.AcquireAsync(ct))
                {
                    await _acquisition.AcquireLatestNexusAsync(NexusGameIdentity.DarktideDomain, info.ModId, ct: ct);
                }

                // Acknowledge immediately so the flag clears without an extra API
                // check. The next authoritative check reconciles naturally.
                _updateState.AcknowledgeInstall(profileId, info.ContainerId);
                installed++;
                _logger.LogInformation(
                    "Automatic update installed container {Container} (mod {Mod}).",
                    info.ContainerId, info.ModId);
            }
            catch (OperationCanceledException)
            {
                // Cancellation stops the batch (shutdown / user-driven). Do not
                // surface it as a failure; re-raise so the caller (the runner)
                // sees the cancellation.
                _logger.LogInformation("Automatic update batch cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                failed.Add((info, ex.Message));
                _logger.LogError(ex,
                    "Automatic update of container {Container} (mod {Mod}) failed; continuing the batch.",
                    info.ContainerId, info.ModId);
            }
            finally
            {
                // Always clear this row's progress: success, failure, or
                // cancellation. This is the deterministic stop that pairs with
                // the active=true above; it cannot be skipped, so a failed or
                // cancelled install never leaves the spinner stuck on.
                RaiseProgress(info.ContainerId, active: false);
            }
        }

        // 6. Feedback. A successful batch is silent. One or more failures surface
        //    a single aggregated, localized summary alert naming the failed mods.
        if (failed.Count > 0)
        {
            var names = string.Join(", ", failed.Select(f => f.Info.ModName));
            _logger.LogWarning(
                "Automatic update batch completed with {Failed} failure(s): {Names}.",
                failed.Count, names);
            await _dialogs.ShowAlertAsync(
                _localization["ModList_AutoUpdateFailedTitle"],
                _localization.Format("ModList_AutoUpdateFailedSummary", names));
        }

        if (installed > 0)
        {
            UpdatesApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Re-validates that <paramref name="info"/> is still an eligible automatic
    /// target in <paramref name="profileId"/>: the mod is still in the profile on
    /// <see cref="LatestPolicy"/>, the container still resolves to a
    /// <see cref="NexusSource"/> with the same <see cref="NexusSource.ModId"/>,
    /// and the installed version still matches the result snapshot's
    /// <see cref="ModUpdateInfo.CurrentVersion"/>. Returns <c>false</c> (with a
    /// short <paramref name="reason"/>) on any mismatch; the caller skips the
    /// entry but continues the batch.
    /// </summary>
    private bool IsStillEligible(Guid profileId, ModUpdateInfo info, out string reason)
    {
        ModListEntry? entry;
        try
        {
            entry = _profiles.GetModList(profileId).FirstOrDefault(e => e.ContainerId == info.ContainerId);
        }
        catch (KeyNotFoundException)
        {
            reason = "profile not found";
            return false;
        }

        if (entry is null)
        {
            reason = "removed from profile";
            return false;
        }

        if (entry.Policy is not LatestPolicy)
        {
            reason = "re-pinned";
            return false;
        }

        var container = _repository.Get(info.ContainerId);
        if (container is null)
        {
            reason = "container gone";
            return false;
        }

        if (container.Source is not NexusSource nexus || nexus.ModId != info.ModId)
        {
            reason = "source changed";
            return false;
        }

        var installedVersion = container.ResolveVersion(new LatestPolicy())?.VersionString ?? string.Empty;
        if (!string.Equals(installedVersion, info.CurrentVersion, StringComparison.OrdinalIgnoreCase))
        {
            reason = "version changed";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Raises <see cref="ModUpdateProgress"/> for <paramref name="containerId"/>.
    /// Called immediately before the acquisition attempt (active=true) and from
    /// the per-mod finally block (active=false). Subscribers marshal to the UI
    /// thread; this method fires on the caller's (runner's) thread.
    /// </summary>
    private void RaiseProgress(Guid containerId, bool active) =>
        ModUpdateProgress?.Invoke(this, new ModUpdateProgressEventArgs(containerId, active));
}
