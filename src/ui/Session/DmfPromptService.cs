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
/// The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator.
/// Surfaces a modal prompt on the main window when a new profile becomes active
/// and DMF is not already in it: a fresh ask per profile (no persisted flag).
/// Decline is respected; the user can add DMF later via the normal add flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trigger fires from the backend; the prompt fires from the shell on
/// Mods entry.</b> <see cref="IProfileService.ProfileCreated"/> fires
/// synchronously from inside the create call. This coordinator subscribes at
/// construction (resolved eagerly when the shell is built, before any profile
/// can be created), records the signal as pending, and the shell awaits
/// <see cref="ProcessPendingAsync"/> when the user next navigates into the Mods
/// destination, so the DMF prompt runs as the topmost modal with Mods already
/// selected underneath. A pending trigger survives visits to other destinations
/// and is consumed only on a real navigation into Mods.</para>
/// <para>
/// <b>The two DMF cases.</b> On a trigger, the coordinator looks up DMF by
/// source (<c>new NexusSource { ModId = <see cref="DmfModId"/> }</c>) and checks
/// the active profile's mod list. (1) DMF in the repo but not in the profile:
/// a Yes/No confirm, On Yes adds it instantly (no download). (2) DMF not in the
/// repo: a Yes/No confirm. On Yes, premium users get the in-app API download
/// under a spinner (the Nexus <c>download_link</c> endpoint is premium-only)
/// plus the add; everyone else gets the DMF files page opened in their browser
/// (the user downloads DMF there, and either clicks Download if Curator owns
/// the <c>nxm://</c> handler, or imports the archive manually). Inside a Steam
/// Deck Gaming Mode session the browser branch cannot complete, so Premium
/// users still get the confirm + in-app download while everyone else gets an
/// informational Desktop Mode guidance alert.</para>
/// <para>
/// <b>No auth trigger.</b> Configuring Nexus auth no longer surfaces a DMF
/// prompt on its own; the one-time Nexus setup offer lives in the first-run
/// Welcome flow instead. The coordinator never opens the Nexus
/// destination and never stops at an informational dead-end: on a confirmed
/// download it either downloads in-app (premium) or opens the browser (everyone
/// else).</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <see cref="UpdateCheckRunner"/>:
/// the coordinator observes UI-layer singletons (<see cref="IProfileSession"/>,
/// <see cref="IDialogService"/>) and orchestrates Integrations + Profiles +
/// Mods services. Registered as a singleton; the shell resolves it + awaits
/// <see cref="ProcessPendingAsync"/> after navigating into Mods.</para>
/// </remarks>
public sealed class DmfPromptService
{
    /// <summary>
    /// The Nexus mod id of Darktide Mod Framework. DMF is required for most
    /// Darktide mods; the prompt offers to install it when missing.
    /// </summary>
    public const int DmfModId = 8;

    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Curator supports only Darktide
    /// (mirrors <c>ModListViewModel.GameDomain</c> +
    /// <c>ModAcquisitionService</c>).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

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
    private const string DmfFilesUrl = "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files";

    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModAcquisitionService _acquisition;
    private readonly INexusAuthService _auth;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly INxmRegistrationState _nxmRegistration;
    private readonly IGamingModeState _gamingMode;
    private readonly ILogger<DmfPromptService> _logger;
    private readonly IExternalLauncher _externalLauncher;

    // The pending new-profile trigger, set by the event handler (which fires
    // synchronously from CreateProfile) and consumed by ProcessPendingAsync
    // (awaited by the shell on Mods entry). Single-entry: the newest create
    // wins. Read + written on the UI thread only (ProfileCreated fires from
    // ProfileService on the UI thread; the shell's NavigateAsync runs on the UI
    // thread).
    private Guid? _pendingNewProfileId;

    public DmfPromptService(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<DmfPromptService> logger,
        INxmRegistrationState nxmRegistration,
        IGamingModeState gamingMode,
        IExternalLauncher externalLauncher)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nxmRegistration = nxmRegistration ?? throw new ArgumentNullException(nameof(nxmRegistration));
        _gamingMode = gamingMode ?? throw new ArgumentNullException(nameof(gamingMode));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));

        _profiles.ProfileCreated += OnProfileCreated;
    }

    /// <summary>
    /// Records a new-profile-created signal. The shell will await
    /// <see cref="ProcessPendingAsync"/> on the next navigation into Mods; this
    /// method only records the pending trigger.
    /// </summary>
    /// <remarks>
    /// A second create before the first is processed overwrites it (the newest
    /// created id is the relevant one). A profile created + then deleted before
    /// the shell processes the trigger is handled by
    /// <see cref="PromptForNewProfileAsync"/>: it checks the active id, which
    /// no longer points at the deleted profile, so no prompt fires.
    /// </remarks>
    private void OnProfileCreated(object? sender, ProfileSummary e)
    {
        _pendingNewProfileId = e.Id;
        _logger.LogDebug("Recorded pending DMF new-profile trigger for {Id}.", e.Id);
    }

    /// <summary>
    /// Processes any pending new-profile trigger. Awaited by the shell after a
    /// navigation into Mods so the DMF prompt runs as the topmost modal with
    /// the Mods destination already selected underneath. Safe to call when
    /// nothing is pending (a no-op).
    /// </summary>
    /// <returns><c>true</c> when a pending trigger was consumed (a prompt may or
    /// may not have fired depending on the active-id + DMF checks); <c>false</c>
    /// when there was no pending trigger, so the caller knows no mod list reload
    /// is warranted.</returns>
    /// <remarks>
    /// The trigger is consumed (cleared) before it is processed so a thrown
    /// exception in the prompt does not leave it stuck pending for the next
    /// call. A failure inside the prompt is caught + logged so a wiring issue
    /// never blocks the shell's post-navigation return. The boolean carries
    /// only "a trigger was consumed"; whether the prompt fired (DMF missing vs.
    /// already in the profile, declined, etc.) stays internal.</remarks>
    public async Task<bool> ProcessPendingAsync()
    {
        // Snapshot + clear before processing so an exception in the prompt
        // doesn't leave the trigger stuck for the next call.
        var newProfileId = _pendingNewProfileId;
        _pendingNewProfileId = null;

        if (newProfileId is Guid id)
        {
            await RunPromptSafelyAsync(() => PromptForNewProfileAsync(id));
            return true;
        }

        return false;
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
        // Nexus auth): on confirm, premium users get the in-app API download;
        // everyone else gets the DMF files page opened in the browser. The
        // confirm message is tailored to whether Curator owns the nxm handler
        // so the user knows whether to click Download on Nexus (manager path)
        // or download the archive and import it manually. Inside a Gaming Mode
        // session the browser branch cannot complete (the Gaming Mode browser
        // does not hand nxm:// links to Curator, and manual import needs
        // Desktop Mode), so the premium state is resolved up front: Premium
        // keeps the ordinary confirm + in-app download flow, while everyone
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
            await DownloadAndAddAsync(profileId);
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
    /// Downloads the latest DMF MAIN release via the acquisition service, then
    /// adds it to the profile. The download runs under a modal spinner
    /// (<see cref="IDialogService.ShowProgressAsync{T}"/>) so the user sees the
    /// operation is in flight (the acquisition takes a few seconds). Errors are
    /// surfaced as an alert (the user can retry via the normal add flow).
    /// </summary>
    private async Task DownloadAndAddAsync(Guid profileId)
    {
        Guid containerId;
        try
        {
            var (id, _) = await _dialogs.ShowProgressAsync(
                _localization["Dmf_Downloading"],
                _localization["Dmf_DownloadingMessage"],
                () => _acquisition.AcquireLatestNexusAsync(GameDomain, DmfModId));
            containerId = id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF download failed.");
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadFailedTitle"],
                _localization.Format("Dmf_DownloadFailedMessage", ex.Message));
            return;
        }

        try
        {
            _profiles.AddMod(profileId, containerId, ModVersionPolicy.Latest);
            _logger.LogInformation(
                "Downloaded + added DMF container {Container} to profile {Profile} via the DMF prompt.",
                containerId, profileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF AddMod failed after a successful download.");
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadFailedTitle"],
                _localization.Format("Dmf_DownloadFailedMessage", ex.Message));
        }
    }
}
