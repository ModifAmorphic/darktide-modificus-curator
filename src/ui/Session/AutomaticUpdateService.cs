using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// Default <see cref="IAutomaticUpdateService"/>. Registered as a singleton.
/// See the interface remarks for the gating, batch, isolation, and feedback
/// rules. The installs themselves are UpdateInstall items admitted onto the
/// download queue through <see cref="ModUpdateEnqueuer"/> (the queue's serial
/// worker owns the eligibility revalidation, the acquisition, the
/// acknowledgement, the per-row progress, and the
/// <see cref="IModDownloadQueue.UpdatesApplied"/> reload signal); this service
/// owns only the gates, the enqueue batch, the stop/cancel-on-profile-switch
/// policy, and the aggregated resolve-failure alert.
/// </summary>
/// <remarks>
/// <para>
/// <b>No UI-thread affinity required, but single-threaded state.</b> Invoked
/// by the runner after it returns to the UI context; the service's awaits
/// (the Premium check, the per-mod resolves) yield without blocking the UI
/// thread. The outstanding-item tracking + the aggregated alert run on the UI
/// thread: <c>RunAfterCheckAsync</c>, the session's
/// <see cref="INotifyPropertyChanged.PropertyChanged"/>, and the queue's
/// <see cref="IModDownloadQueue.ItemChanged"/> all arrive there. No
/// <c>ConfigureAwait(false)</c> is used (UI-layer convention: stay on the
/// captured context).</para>
/// <para>
/// <b>The fresh Premium check is conditional.</b> It fires only when the
/// gating passed (authoritative success with updates + auto-update enabled +
/// active profile matches), so an empty result or a disabled setting never costs
/// an extra API call.</para>
/// </remarks>
internal sealed class AutomaticUpdateService : IAutomaticUpdateService
{
    private readonly IProfileSession _session;
    private readonly ModUpdateEnqueuer _enqueuer;
    private readonly IModDownloadQueue _queue;
    private readonly INexusAuthService _auth;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<AutomaticUpdateService> _logger;

    /// <summary>
    /// The outstanding batch items (admitted, not yet terminal). Lets the
    /// session watcher cancel the not-yet-started ones when the active
    /// profile changes away from an item's target. UI-thread only (see the
    /// class remarks).
    /// </summary>
    private readonly List<DownloadItem> _outstanding = new();

    public AutomaticUpdateService(
        IProfileSession session,
        ModUpdateEnqueuer enqueuer,
        IModDownloadQueue queue,
        INexusAuthService auth,
        IConfigLoader configLoader,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<AutomaticUpdateService> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Application-lifetime singletons; the subscriptions are never undone
        // (the established session-subscription pattern).
        _session.PropertyChanged += OnSessionPropertyChanged;
        _queue.ItemChanged += OnQueueItemChanged;
    }

    /// <summary>
    /// Prunes terminal items from the outstanding set (both application-
    /// lifetime singletons raise this on the UI thread). A Failed row is
    /// terminal for the batch too: its failure renders on the row + the user
    /// dismisses or retries it there.
    /// </summary>
    private void OnQueueItemChanged(DownloadItem item)
    {
        if (item.IsTerminal)
        {
            _outstanding.RemoveAll(tracked => ReferenceEquals(tracked, item));
        }
    }

    /// <summary>
    /// The stop-on-profile-switch policy: when the session's active profile
    /// changes, every outstanding batch item still WAITING for the worker and
    /// targeting a profile other than the new active one is cancelled (queued
    /// cancel semantics: the row leaves, nothing was downloaded). An item the
    /// worker already started completes under its own rules (its completion
    /// acknowledges against its captured target profile; the applied event
    /// reloads whatever list is showing).
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId))
        {
            return;
        }

        foreach (var item in _outstanding.ToArray())
        {
            CancelIfStillQueued(item, _session.ActiveProfileId);
        }
    }

    /// <summary>
    /// Cancels <paramref name="item"/> when it is still waiting for the worker
    /// (queued cancel semantics) + its target is no longer the active profile.
    /// An item already started by the worker is left to complete under the
    /// queue's own rules. The still-Queued test reads the marshaled
    /// presentation phase, so a just-started item whose Downloading write has
    /// not landed can be caught here too; that benign race resolves safely
    /// through the queue's token-authoritative cancel (the worker lands the
    /// item Canceled with no completion side effects), preserving the policy
    /// intent.
    /// </summary>
    private void CancelIfStillQueued(DownloadItem item, Guid? activeProfileId)
    {
        if (!item.IsTerminal && item.Phase == DownloadPhase.Queued
            && item.TargetProfileId != activeProfileId)
        {
            _logger.LogInformation(
                "Cancelling the queued automatic update of mod {Mod} (target profile {Profile} is no longer active).",
                item.ModId, item.TargetProfileId);
            _queue.Cancel(item);
        }
    }

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

        // 5. The enqueue batch. Each flagged candidate resolves its head file and
        //    admits one UpdateInstall item through the shared enqueue front; the
        //    queue's serial worker owns the rest (the dequeue-time eligibility
        //    revalidation replaces the old per-iteration candidate re-pull). Per-
        //    iteration: re-check the active profile so a switch mid-batch stops
        //    scheduling further entries (the session watcher cancels the ones
        //    already admitted). Per-mod failures are recorded, never aborting
        //    later mods.
        var resolveFailed = new List<string>();
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

            try
            {
                var item = await _enqueuer.EnqueueLatestAsync(
                    info.ModId, info.ContainerId, info.ModName, info.CurrentVersion, profileId, ct);
                _outstanding.Add(item);

                // A switch can land while the resolve was in flight (after the
                // session watcher's event already fired): the item was just
                // admitted for a profile the user left, so cancel it here (a
                // no-op if the worker already started it) + stop scheduling.
                if (_session.ActiveProfileId != profileId)
                {
                    CancelIfStillQueued(item, _session.ActiveProfileId);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation stops the batch (shutdown / user-driven). Do not
                // surface it as a failure; re-raise so the caller (the runner)
                // sees the cancellation. Already-admitted items run to their
                // own completion under the queue.
                _logger.LogInformation("Automatic update batch cancelled.");
                throw;
            }
            catch (KeyNotFoundException)
            {
                // The target profile was deleted mid-batch; there is nowhere left
                // to install. Stop the batch quietly (the session watcher cancels
                // anything already admitted for it).
                _logger.LogInformation(
                    "Automatic update batch stopped: profile {Profile} is gone.", profileId);
                break;
            }
            catch (Exception ex)
            {
                // A resolve failure (API down, no MAIN files): nothing was
                // enqueued, so there is no row to host the failure. Record the
                // mod for the aggregated alert; later mods still enqueue.
                _logger.LogError(ex,
                    "Resolving the latest release of mod {Mod} ({Container}) failed; continuing the batch.",
                    info.ModName, info.ContainerId);
                resolveFailed.Add(info.ModName);
            }
        }

        // 6. Feedback. A fully successful batch is silent. A download failure
        //    renders inline on its row (the queue's Failed phase with retry), so
        //    it needs no alert here; only resolve failures (no row exists to
        //    host them) surface, as one aggregated, localized summary alert
        //    naming the mods.
        if (resolveFailed.Count > 0)
        {
            var names = string.Join(", ", resolveFailed);
            _logger.LogWarning(
                "Automatic update batch completed with {Failed} resolve failure(s): {Names}.",
                resolveFailed.Count, names);
            await _dialogs.ShowAlertAsync(
                _localization["ModList_AutoUpdateFailedTitle"],
                _localization.Format("ModList_AutoUpdateFailedSummary", names));
        }
    }
}
