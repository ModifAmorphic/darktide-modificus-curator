using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator.
/// Surfaces a modal prompt on the main window when a new profile becomes active
/// and DMF is not already in it: a fresh ask per profile (no persisted flag).
/// Decline is respected; the user can add DMF later via the normal add flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger fires from the backend; the prompt fires from the shell's
/// modal queue.</b> <see cref="IProfileService.ProfileCreated"/> fires
/// synchronously from inside the create call. This coordinator subscribes at
/// construction (resolved eagerly at composition, before any profile can be
/// created) and enqueues its prompt onto the <see cref="IShellModalQueue"/> for
/// the Mods destination; the shell drains the queue after switching to Mods +
/// running its enter effects, so the DMF prompt runs as the topmost modal with
/// Mods already selected underneath. The queued entry survives visits to other
/// destinations and is consumed only on a real navigation into Mods; a second
/// create before the entry drains replaces it (the newest created id is the
/// relevant one). After the prompt finishes (accepted, declined, or skipped),
/// the coordinator reloads the mod list itself so an accepted existing-DMF add
/// (case 1) is visible immediately, and a declined or no-op prompt reloads the
/// same authoritative state; an accepted premium download needs no reload here
/// because the download queue's completion owns the add and reloads (the
/// target is the active profile, so the reload always fires).</para>
/// <para>
/// <b>The two DMF cases.</b> On the trigger, the coordinator looks up DMF by
/// source (<c>new NexusSource { ModId = <see cref="DmfModId"/> }</c>) and checks
/// the active profile's mod list. (1) DMF in the repo but not in the profile:
/// a Yes/No confirm, On Yes adds it instantly (no download). (2) DMF not in the
/// repo: a Yes/No confirm. On Yes, premium users (the Nexus
/// <c>download_link</c> endpoint is premium-only) get the download enqueued
/// onto the shared download queue: the concrete head file is resolved first
/// so the queue's dedupe key is real and the download fetches the exact file
/// the user was offered at confirm, then the download row owns progress and
/// the queue's completion owns the add + reload; everyone else gets the DMF
/// files page opened in their browser (the user downloads DMF there, and
/// either clicks Download if Curator owns the <c>nxm://</c> handler, or
/// imports the archive manually). Inside a Steam
/// Deck Gaming Mode session the browser branch cannot complete, so Premium
/// users still get the confirm + enqueued download while everyone else gets
/// an informational Desktop Mode guidance alert.</para>
/// <para>
/// <b>No auth trigger.</b> Configuring Nexus auth no longer surfaces a DMF
/// prompt on its own; the one-time Nexus setup offer lives in the first-run
/// Welcome flow instead. The coordinator never opens the Nexus
/// destination and never stops at an informational dead-end: on a confirmed
/// download it either enqueues the in-app download (premium) or opens the
/// browser (everyone else).</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <see cref="UpdateCheckRunner"/>:
/// the coordinator observes UI-layer singletons (<see cref="IProfileSession"/>,
/// <see cref="IDialogService"/>, <see cref="IShellModalQueue"/>) and orchestrates
/// Integrations + Profiles + Mods services. Registered as a singleton; nothing
/// depends on it (the shell no longer knows it exists), so composition resolves
/// it once at startup to establish the subscription.</para>
/// </remarks>
public sealed class DmfPromptService
{
    /// <summary>
    /// The Nexus mod id of Darktide Mod Framework. DMF is required for most
    /// Darktide mods; the prompt offers to install it when missing.
    /// </summary>
    public const int DmfModId = 8;

    /// <summary>
    /// The queue-owner key for this service's enqueued modal (one pending
    /// entry; a newer create replaces it).
    /// </summary>
    private static readonly object QueueOwner = typeof(DmfPromptService);

    /// <summary>
    /// The Nexus files page for DMF. Opened in the user's browser when DMF is
    /// not in the repository and the user is not premium (the Nexus
    /// <c>download_link</c> endpoint is premium-only, so non-premium users
    /// must visit the site). When Curator owns the <c>nxm://</c> handler the
    /// user clicks Download on the page and the handler picks up the URL, so
    /// DMF is added to the active profile via the standard nxm flow. When
    /// Curator does not own the handler the user downloads the archive and
    /// imports it via the normal add flow.
    /// </summary>
    private const string DmfFilesUrl = "https://www.nexusmods.com/" + NexusGameIdentity.DarktideDomain + "/mods/8?tab=files";

    /// <summary>
    /// The row name for the enqueued DMF download. DMF is not in the
    /// repository on this path (no container to peek), so the well-known name
    /// carries the row until the acquisition swaps in the name Nexus reports.
    /// </summary>
    private const string DmfDisplayName = "Darktide Mod Framework";

    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModAcquisitionService _acquisition;
    private readonly IModDownloadQueue _downloadQueue;
    private readonly INexusAuthService _auth;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly INxmRegistrationState _nxmRegistration;
    private readonly IGamingModeState _gamingMode;
    private readonly ILogger<DmfPromptService> _logger;
    private readonly IExternalLauncher _externalLauncher;
    private readonly IShellModalQueue _modalQueue;
    private readonly IModListRefresh _modListRefresh;

    public DmfPromptService(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModAcquisitionService acquisition,
        IModDownloadQueue downloadQueue,
        INexusAuthService auth,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<DmfPromptService> logger,
        INxmRegistrationState nxmRegistration,
        IGamingModeState gamingMode,
        IExternalLauncher externalLauncher,
        IShellModalQueue modalQueue,
        IModListRefresh modListRefresh)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nxmRegistration = nxmRegistration ?? throw new ArgumentNullException(nameof(nxmRegistration));
        _gamingMode = gamingMode ?? throw new ArgumentNullException(nameof(gamingMode));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        _modalQueue = modalQueue ?? throw new ArgumentNullException(nameof(modalQueue));
        _modListRefresh = modListRefresh ?? throw new ArgumentNullException(nameof(modListRefresh));

        _profiles.ProfileCreated += OnProfileCreated;
    }

    /// <summary>
    /// Records a new-profile-created signal: enqueues the prompt onto the
    /// shell's modal queue for the next real Mods entry. A profile created
    /// while Darktide runs does NOT become active (the session gates it), so
    /// the prompt body skips in that case (the user is still on their previous
    /// profile); a profile created + then deleted before the entry drains is
    /// handled the same way (the active-id check no longer matches).
    /// </summary>
    private void OnProfileCreated(object? sender, ProfileSummary e)
    {
        var createdProfileId = e.Id;
        _modalQueue.Enqueue(QueueOwner, ShellDestination.Mods, () =>
            RunPromptAndReloadAsync(createdProfileId));
        _logger.LogDebug("Enqueued the DMF new-profile prompt for {Id}.", e.Id);
    }

    /// <summary>
    /// The drained modal body: run the prompt (fail-isolated), then reload the
    /// mod list so an accepted existing/Premium DMF add is visible immediately
    /// (a declined, skipped, or browser-open prompt reloads the same
    /// authoritative state). The reload is the enqueuer's business, matching
    /// the post-consumed reload the shell used to run.
    /// </summary>
    private async Task RunPromptAndReloadAsync(Guid createdProfileId)
    {
        await RunPromptSafelyAsync(() => PromptForNewProfileAsync(createdProfileId));
        _modListRefresh.Reload();
    }

    /// <summary>
    /// Runs a prompt, catching any non-cancellation exception so a wiring
    /// failure or service throw does not crash the app or block the shell's
    /// post-dialog return. <see cref="OperationCanceledException"/> is also
    /// swallowed (no cancellation token is wired today; defensive only).
    /// </summary>
    private async Task RunPromptSafelyAsync(Func<Task> prompt)
    {
        try
        {
            await prompt();
        }
        catch (OperationCanceledException)
        {
            // Defensive only; no cancellation token is wired today.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF prompt failed unexpectedly.");
        }
    }

    /// <summary>
    /// The new-profile trigger: prompts for DMF if the just-created profile
    /// became active + DMF is not in it. A profile created while Darktide runs
    /// does NOT become active (the session gates it), so no prompt fires in
    /// that case (correct: the user is still on their previous profile).
    /// </summary>
    private Task PromptForNewProfileAsync(Guid createdProfileId)
    {
        if (_session.ActiveProfileId != createdProfileId)
        {
            // Created but not active (game was running). The active profile did
            // not change, so there is no "new profile" surface to prompt for.
            _logger.LogDebug(
                "Skipping DMF new-profile prompt: created profile {Id} is not the active {Active}.",
                createdProfileId,
                _session.ActiveProfileId);
            return Task.CompletedTask;
        }

        return PromptIfMissingAsync(createdProfileId);
    }

    /// <summary>
    /// The shared prompt body. Looks up DMF in the repo + checks the active
    /// profile's mod list, then surfaces the appropriate case (1: add,
    /// 2: download/add or browser-open). No-op if DMF is already in the
    /// profile.
    /// </summary>
    private async Task PromptIfMissingAsync(Guid profileId)
    {
        var dmf = _repo.FindBySource(new NexusSource { ModId = DmfModId });

        // DMF already in the profile: nothing to prompt about.
        var mods = _profiles.GetModList(profileId);
        if (dmf is not null && mods.Any(m => m.ContainerId == dmf.Id))
        {
            _logger.LogDebug("Skipping DMF prompt: DMF (container {Container}) is already in profile {Profile}.",
                dmf.Id, profileId);
            return;
        }

        if (dmf is not null)
        {
            // Case 1: DMF in the repo but not in this profile. Instant add on
            // confirm (no download).
            var confirmed = await _dialogs.ConfirmAsync(
                _localization["Dmf_AddTitle"],
                _localization["Dmf_AddMessage"]);

            if (confirmed)
            {
                _profiles.AddMod(profileId, dmf.Id, ModVersionPolicy.Latest);
                _logger.LogInformation(
                    "Added existing DMF container {Container} to profile {Profile} via the DMF prompt.",
                    dmf.Id, profileId);
            }
            return;
        }

        // Case 2: DMF not in the repo. Always offer the download (regardless of
        // Nexus auth): on confirm, premium users get the download enqueued onto
        // the shared download queue; everyone else gets the DMF files page
        // opened in the browser. The confirm message is tailored to whether
        // Curator owns the nxm handler so the user knows whether to click
        // Download on Nexus (manager path) or download the archive and import
        // it manually. Inside a Gaming Mode
        // session the browser branch cannot complete (the Gaming Mode browser
        // does not hand nxm:// links to Curator, and manual import needs
        // Desktop Mode), so the premium state is resolved up front: Premium
        // keeps the ordinary confirm + enqueued download flow, while everyone
        // else gets an informational Desktop Mode guidance alert (no Yes/No
        // confirm: there is no action that could run inside Gaming Mode to
        // confirm). No nxm registration probe happens on either path.
        if (_gamingMode.IsGamingMode && (await _auth.GetCurrentStateAsync())?.IsPremium != true)
        {
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadTitle"],
                _localization["Dmf_DownloadMessageGamingMode"]);
            return;
        }

        var ownsHandler = OwnsNxmHandler();
        var message = ownsHandler
            ? _localization["Dmf_DownloadMessage"]
            : _localization["Dmf_DownloadMessageManual"];

        var downloadConfirmed = await _dialogs.ConfirmAsync(
            _localization["Dmf_DownloadTitle"],
            message);

        if (!downloadConfirmed)
        {
            // Decline is respected: do nothing, open no browser, show no
            // integration prompt.
            return;
        }

        var state = await _auth.GetCurrentStateAsync();
        if (state?.IsPremium == true)
        {
            await EnqueueLatestDmfAsync(profileId);
        }
        else
        {
            await OpenDmfFilesPageInBrowser();
        }
    }

    /// <summary>
    /// Whether Curator is registered as the OS <c>nxm://</c> handler, from the
    /// shared last-known registration state (false when no platform registrar
    /// exists). Used only to tailor the download confirm message
    /// (manager-download vs. manual-import guidance); it never probes the OS,
    /// so the guidance is advisory and may be stale after an out-of-band
    /// ownership change (accepted by design).
    /// </summary>
    private bool OwnsNxmHandler() => _nxmRegistration.IsRegistered;

    /// <summary>
    /// Opens DMF's Nexus files page in the user's default browser. Used when DMF
    /// is not in the repository and the user is not premium (the Nexus
    /// <c>download_link</c> endpoint is premium-only). When Curator owns the
    /// <c>nxm://</c> handler the user clicks Download on the page and the
    /// handler catches the URL, so DMF is added to the active profile via the
    /// standard nxm flow. When Curator does not own the handler the user
    /// downloads the archive and imports it via the normal add flow. No
    /// additional confirm before opening (the user already confirmed the
    /// download offer); on a launcher failure, falls back to an alert with the
    /// URL so the user can copy it manually (better than a silent no-op).
    /// </summary>
    private async Task OpenDmfFilesPageInBrowser()
    {
        var uri = new Uri(DmfFilesUrl);
        if (_externalLauncher.OpenUri(uri))
        {
            _logger.LogInformation(
                "Opened DMF files page in browser; the nxm handler will pick up the download if Curator owns it.");
            return;
        }

        // Launcher failed (no default browser, headless, etc.). Surface the URL
        // so the user can copy it; this is a failure alert, not a guidance step.
        _logger.LogWarning("Failed to open the DMF files page in the browser.");
        await _dialogs.ShowAlertAsync(
            _localization["Dmf_DownloadFailedTitle"],
            _localization.Format("Dmf_OpenBrowserFailedMessage", DmfFilesUrl));
    }

    /// <summary>
    /// Enqueues the latest DMF MAIN release onto the download queue targeting
    /// the profile. The concrete head file is resolved first (one file-listing
    /// call, no download) so the queue's dedupe key is real and the download
    /// fetches exactly the file the user was offered at confirm; from here the
    /// download row owns progress and the queue's completion owns the profile
    /// add + the reload (the target is the active profile, so the reload
    /// always fires). A resolve failure (API down, no MAIN files) surfaces the
    /// localized failure alert and enqueues nothing (there is no row to host
    /// it on yet).
    /// </summary>
    private async Task EnqueueLatestDmfAsync(Guid profileId)
    {
        try
        {
            // The resolved release tag is not carried on the request; the row
            // shows the version the queue itself resolves, matching every
            // other enqueue path.
            var (fileId, _) = await _acquisition.ResolveLatestNexusAsync(
                NexusGameIdentity.DarktideDomain, DmfModId);
            var profileName = _profiles.GetProfile(profileId).Name;
            _downloadQueue.Enqueue(new ModDownloadRequest(
                NexusGameIdentity.DarktideDomain, DmfModId, fileId,
                DownloadPurpose.ProfileAdd,
                ContainerId: null, DmfDisplayName, profileId, profileName));
            _logger.LogInformation(
                "Enqueued the DMF download of file {File} for profile {Profile}.",
                fileId, profileId);
        }
        catch (Exception ex)
        {
            // A resolve failure (API down, no MAIN files), a profile that
            // vanished mid-prompt, or an admission failure: nothing was
            // enqueued, so the failure is gate-shaped and surfaces as today's
            // localized alert (the user can retry via the normal add flow).
            _logger.LogError(ex, "Failed to enqueue the DMF download.");
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadFailedTitle"],
                _localization.Format("Dmf_DownloadFailedMessage", ex.Message));
        }
    }
}
