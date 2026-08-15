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
/// See the interface remarks for the gating, batch, isolation, and feedback
/// rules. The installs themselves route through <see cref="IModUpdateInstaller"/>
/// (which owns the coordinator, the eligibility revalidation, the
/// acknowledgement, and the per-row progress events); this service owns only
/// the gates + the sequential batch + the aggregated failure alert.
/// </summary>
/// <remarks>
/// <para>
/// <b>No UI-thread affinity required.</b> Invoked by the runner after it returns
/// to the UI context; the service's awaits (the Premium check, the per-mod
/// installs) yield without blocking the UI thread, and the aggregated alert +
/// the <see cref="UpdatesApplied"/> event fire on the UI thread (the runner's
/// context). No <c>ConfigureAwait(false)</c> is used (UI-layer convention: stay
/// on the captured context).</para>
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
    private readonly IModUpdateInstaller _installer;
    private readonly INexusAuthService _auth;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<AutomaticUpdateService> _logger;

    public AutomaticUpdateService(
        IProfileSession session,
        IProfileService profiles,
        IModUpdateInstaller installer,
        INexusAuthService auth,
        IConfigLoader configLoader,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<AutomaticUpdateService> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler? UpdatesApplied;

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

        // 5. The sequential install batch. The installer owns the coordinator,
        //    the in-gate eligibility revalidation, the acknowledge-on-success,
        //    and the per-row progress events; this loop owns the scheduling.
        //    Per-iteration: re-check the active profile (a switch stops the
        //    whole batch) + re-pull the candidates (the installer revalidates
        //    against them). Per-mod failures are recorded; they do not abort
        //    later mods.
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

            IReadOnlyList<ModListCandidate> candidates;
            try
            {
                candidates = _profiles.GetModList(profileId).ToCandidates();
            }
            catch (KeyNotFoundException)
            {
                // The profile was deleted mid-batch; there is nowhere left to
                // install. Stop the batch (the session's reconcile clears the
                // active id, so the next iteration's profile gate would stop
                // it too; this catches the gap).
                _logger.LogInformation(
                    "Automatic update batch stopped: profile {Profile} is gone.", profileId);
                break;
            }

            try
            {
                // The awaiting install semantics: the batch waits its turn
                // behind a manual install under the shared gate, one mod at a
                // time. Cancellation propagates (rethrown below).
                var outcome = await _installer.InstallLatestAsync(
                    profileId, info.ContainerId, info.ModId, info.CurrentVersion, candidates, ct);

                switch (outcome.Status)
                {
                    case ModInstallStatus.Installed:
                        installed++;
                        break;
                    case ModInstallStatus.NotEligible:
                        // The installer already revalidated + logged the
                        // reason; skipping one entry never stops the batch.
                        break;
                    case ModInstallStatus.Failed:
                        failed.Add((info, outcome.Reason));
                        _logger.LogError(
                            "Automatic update of container {Container} (mod {Mod}) failed ({Reason}); continuing the batch.",
                            info.ContainerId, info.ModId, outcome.Reason);
                        break;
                    default:
                        // Busy cannot occur on the awaiting path (it waits its
                        // turn); treat any unexpected shape as a skip so a
                        // future status never wedges the batch.
                        _logger.LogWarning(
                            "Automatic update of container {Container} returned {Status}; continuing the batch.",
                            info.ContainerId, outcome.Status);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation stops the batch (shutdown / user-driven). Do not
                // surface it as a failure; re-raise so the caller (the runner)
                // sees the cancellation.
                _logger.LogInformation("Automatic update batch cancelled.");
                throw;
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
}
