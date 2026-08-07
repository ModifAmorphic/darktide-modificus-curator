using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Nxm;
using Modificus.Curator.RelayClient;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Modificus Curator main window, the app shell.
/// The profile controls work: the dropdown switches
/// the active profile (the request flows through <see cref="IProfileSession"/>,
/// which owns the active id + persistence), switching is blocked while Darktide
/// runs (the session gates it), and "Manage profiles…" opens a CRUD dialog. The
/// shell owns only the profile-list snapshot + the dropdown selection binding;
/// the <b>session</b> is the single source of truth for the active id, the
/// can-change gate, and the LIVE running-state. The Preferences
/// affordance (top-bar gear) + dynamic-language refresh of the status text /
/// tooltips runs via <see cref="LocalizationService"/>.
/// </summary>
/// <remarks>
/// <para><b>Running-state is live:</b> the shell mirrors <see cref="IsGameRunning"/>
/// from <see cref="IProfileSession.IsRunning"/>, which a polling timer refreshes.
/// So the status strip, launch-availability, and dropdown-enable react within a
/// few seconds of Darktide starting or stopping while Curator is open.</para>
/// <para><b>Localizable text is live:</b> <see cref="GameRunningText"/> and
/// <see cref="ProfileSwitchTooltip"/> re-resolve from <see cref="LocalizationService"/>
/// when the UI culture changes (Preferences dialog), so the shell copy refreshes
/// in-step with the rest of the UI on a language switch.</para>
/// <para><b>Track C is wired:</b> <see cref="LaunchCommand"/> invokes
/// <see cref="IRelayLaunchService.Launch"/> and branches on
/// <see cref="LaunchResult.Status"/> (Launched -> an immediate
/// <see cref="IsGameRunning"/> refresh; DiscoveryIncomplete -> the focused
/// escape-hatch dialog over the missing fields; Error -> a modal alert), and
/// <see cref="OpenSettingsCommand"/> opens the Settings window (discovery
/// overrides + the open-folder Storage buttons). After Settings closes, the
/// bound <see cref="ModList"/> reloads so any index change is reflected in
/// the mod rows.</para>
/// <para><b>DMF prompt fires after the ManageProfiles dialog closes:</b>
/// <see cref="DmfPromptService"/> subscribes to
/// <see cref="IProfileService.ProfileCreated"/>, which fires FROM inside the
/// ManageProfiles dialog's create call; it records the signal as pending + the
/// shell calls <see cref="DmfPromptService.ProcessPendingAsync"/> after that
/// dialog closes so the DMF prompt is the topmost modal. The auth-triggered DMF
/// flow was removed: configuring Nexus auth no longer surfaces a DMF prompt on
/// its own (the one-time Nexus setup offer lives in the first-run Welcome
/// flow).</para>
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IProfileSession _session;
    private readonly IRelayLaunchService _launchService;
    private readonly IDialogService _dialogs;
    private readonly DmfPromptService _dmfPrompts;
    private readonly LocalizationService _localization;
    private readonly IAppUpdateService _appUpdate;
    private readonly IConfigLoader _configLoader;
    private readonly Action<Action> _invokeOnUi;
    private readonly INxmHandlerRegistrar? _nxmRegistrar;
    private readonly ILogger<ShellViewModel> _logger;

    // Whether the automatic startup self-update check is enabled
    // (CuratorConfig.AppUpdates.CheckOnStartup). The status-strip update notice
    // shows only while this is true: when the user disables automatic checks the
    // notice is suppressed entirely (the manual Settings check stays
    // self-contained with its own Download-and-Restart button). Read at
    // construction + refreshed after the Settings dialog closes (the only place
    // the toggle can change), so a toggle-off-then-close dismisses a showing
    // notice immediately and a toggle-on re-enables it. No config-change
    // subscription: Settings is the sole mutation point.
    private bool _autoUpdateChecksEnabled;

    // Guards selection updates while the shell mirrors the session's authoritative
    // active id back into the dropdown, so re-syncing the selection does not
    // re-request an active change (avoids a feedback loop).
    private bool _syncing;

    /// <summary>
    /// Creates the shell VM, loads the profile list, mirrors the session's current
    /// active id + running-state, queries the nxm handler registration status, and
    /// subscribes to live running-state changes.
    /// </summary>
    public ShellViewModel(
        IProfileService profiles,
        IProfileSession session,
        IRelayLaunchService launchService,
        IDialogService dialogs,
        DmfPromptService dmfPrompts,
        LocalizationService localization,
        ModListViewModel modList,
        IAppUpdateService appUpdate,
        Action<Action> invokeOnUi,
        ILogger<ShellViewModel> logger,
        IConfigLoader configLoader,
        INxmHandlerRegistrar? nxmRegistrar = null)
    {
        _profileService = profiles;
        _session = session;
        _launchService = launchService;
        _dialogs = dialogs;
        _dmfPrompts = dmfPrompts;
        _localization = localization;
        ModList = modList;
        _appUpdate = appUpdate;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger;
        _nxmRegistrar = nxmRegistrar;
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _autoUpdateChecksEnabled = _configLoader.Load().AppUpdates.CheckOnStartup;

        // Set the backing fields directly: no subscribers yet, and setting
        // SelectedProfile through the property would route through the selection
        // handler (which requests an active change) during the initial restore.
        _profiles = _profileService.ListProfiles();
        _isGameRunning = _session.IsRunning;
        _hasPendingStagedChanges = _session.HasPendingChanges;
        _selectedProfile = ResolveActive();

        // Resolve the initial nxm handler status so the status strip paints the
        // right label on startup (enabled / disabled / unavailable).
        RefreshNxmHandlerStatus();

        _session.PropertyChanged += OnSessionPropertyChanged;
        // Re-resolve the localized strings when the UI culture flips so the
        // status strip + tooltip refresh alongside the rest of the UI.
        _localization.PropertyChanged += OnCultureChanged;

        // Subscribe to the app self-update state changes so the status-strip
        // notice appears the moment a check resolves an update (the startup
        // check fires on a background task). Also reflect any result that
        // already landed during shell construction.
        _appUpdate.UpdateStateChanged += OnAppUpdateStateChanged;
        RefreshAppUpdateNotice();

        _logger.LogInformation(
            "Shell initialized: {ProfileCount} profile(s) loaded; active={ActiveId}; " +
            "Darktide running: {IsRunning}; launch facade: {LaunchFacade}",
            Profiles.Count,
            _session.ActiveProfileId?.ToString() ?? "(none)",
            IsGameRunning,
            _launchService.GetType().Name);
    }

    /// <summary>
    /// All known profiles, a snapshot of <see cref="IProfileService.ListProfiles"/>,
    /// refreshed after the management dialog closes. Empty pre-release until a
    /// profile is created.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    private IReadOnlyList<ProfileSummary> _profiles = Array.Empty<ProfileSummary>();

    /// <summary>
    /// Whether at least one profile exists.</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// The active profile's mod-list view model (the dominant content area). A
    /// singleton injected here + bound in <c>MainWindow</c> as
    /// <c>{Binding ModList}</c> onto <c>ModListView</c>. The shell does not touch
    /// the mod list; all mod-list state + edits live on this VM. Exposed so the
    /// view's <c>DataContext</c> binding can reach it.
    /// </summary>
    public ModListViewModel ModList { get; }

    /// <summary>
    /// The currently-selected (active) profile, or <c>null</c>. Bound to the
    /// top-bar dropdown; selecting requests the active change through the session
    /// (which gates it). The shell then re-syncs to the session's authoritative
    /// active id, so a blocked change snaps the dropdown back to the real active.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private ProfileSummary? _selectedProfile;

    /// <summary>
    /// Whether Darktide is currently running, mirrored LIVE from
    /// <see cref="IProfileSession.IsRunning"/> (a polling timer refreshes it).
    /// Gates profile switching (<see cref="CanSwitchProfile"/>) and launch.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowNotRunningDot))]
    [NotifyPropertyChangedFor(nameof(ShowRunningCleanDot))]
    [NotifyPropertyChangedFor(nameof(ShowRunningDirtyDot))]
    private bool _isGameRunning;

    /// <summary>
    /// Whether the active profile has staged edits not yet reflected in the
    /// running game's mod tree, mirrored from
    /// <see cref="IProfileSession.HasPendingChanges"/>. Drives the dirty
    /// status-strip indicator (the yellow dot) alongside
    /// <see cref="IsGameRunning"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    [NotifyPropertyChangedFor(nameof(ShowNotRunningDot))]
    [NotifyPropertyChangedFor(nameof(ShowRunningCleanDot))]
    [NotifyPropertyChangedFor(nameof(ShowRunningDirtyDot))]
    private bool _hasPendingStagedChanges;

    /// <summary>
    /// Whether the grey "not running" status dot should show: Darktide is not
    /// running. One of three mutually-exclusive dot states (see
    /// <see cref="ShowRunningCleanDot"/> + <see cref="ShowRunningDirtyDot"/>).
    /// </summary>
    public bool ShowNotRunningDot => !IsGameRunning;

    /// <summary>
    /// Whether the green "running, in sync" status dot should show: Darktide is
    /// running with no pending (un-staged) profile edits.
    /// </summary>
    public bool ShowRunningCleanDot => IsGameRunning && !HasPendingStagedChanges;

    /// <summary>
    /// Whether the yellow "running, changes pending" status dot should show:
    /// Darktide is running AND the active profile has edits that apply at the
    /// next launch (Curator does not re-stage while the game runs).
    /// </summary>
    public bool ShowRunningDirtyDot => IsGameRunning && HasPendingStagedChanges;

    /// <summary>Status-strip label for the game-running indicator (localized).</summary>
    public string GameRunningText =>
        IsGameRunning
            ? HasPendingStagedChanges
                ? _localization["Status_GameRunningChangesPending"]
                : _localization["Status_GameRunning"]
            : _localization["Status_GameNotRunning"];

    /// <summary>
    /// Whether Curator is currently the OS <c>nxm://</c> handler (per the
    /// registrar's <see cref="INxmHandlerRegistrar.IsRegistered"/>), or
    /// <c>null</c> when no platform registrar is available. Drives
    /// <see cref="NxmHandlerStatusText"/> + <see cref="NxmHandlerStatusTooltip"/>
    /// in the status strip. Refreshed at startup + after the Integrations dialog
    /// closes (the only place the registration can change). No polling: the OS
    /// registration rarely changes out-of-band.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NxmHandlerStatusText))]
    [NotifyPropertyChangedFor(nameof(NxmHandlerStatusTooltip))]
    [NotifyPropertyChangedFor(nameof(IsNxmEnabled))]
    private bool? _isNxmRegistered;

    /// <summary>
    /// Whether the green status-strip indicator should show for the nxm handler
    /// state: <c>true</c> only when Curator is the registered OS handler. Both
    /// the "not registered" (<c>false</c>) and "no platform registrar"
    /// (<c>null</c>) states render the grey indicator, so this collapses the
    /// tri-state <see cref="IsNxmRegistered"/> into the indicator's binary
    /// visibility. Mirrors the game-running <see cref="IsGameRunning"/> pattern.
    /// </summary>
    public bool IsNxmEnabled => IsNxmRegistered == true;

    /// <summary>
    /// The status-strip label for the nxm handler state: enabled when Curator
    /// is registered, disabled when it is not, or unavailable when there is no
    /// platform registrar. Localized; re-resolves on a culture change.
    /// </summary>
    public string NxmHandlerStatusText =>
        IsNxmRegistered switch
        {
            null => _localization["Status_NxmUnavailable"],
            true => _localization["Status_NxmRegistered"],
            false => _localization["Status_NxmNotRegistered"],
        };

    /// <summary>
    /// The status-strip tooltip explaining the current nxm handler state.
    /// Localized; re-resolves on a culture change.
    /// </summary>
    public string NxmHandlerStatusTooltip =>
        IsNxmRegistered switch
        {
            null => _localization["Status_NxmUnavailableTooltip"],
            true => _localization["Status_NxmRegisteredTooltip"],
            false => _localization["Status_NxmNotRegisteredTooltip"],
        };

    /// <summary>
    /// Whether the user dismissed the update notice this session. Session-only:
    /// not persisted (a persisted dismissal would wrongly hide a later update).
    /// Re-shown next startup if an update is still available. Flipping this
    /// re-fires <see cref="ShowAppUpdateNotice"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAppUpdateNotice))]
    private bool _isAppUpdateDismissed;

    /// <summary>
    /// Whether the dismissible update pill should show in the status strip:
    /// self-update must be supported, the automatic startup check must be enabled
    /// (<see cref="CuratorConfig.AppUpdates.CheckOnStartup"/>), a check must have
    /// found an update, and the user must not have dismissed it this session.
    /// </summary>
    /// <remarks>
    /// The notice shows only when the startup check is enabled
    /// (<see cref="CuratorConfig.AppUpdates.CheckOnStartup"/>); when the user
    /// disables automatic checks the notice is suppressed entirely, even if a
    /// check (manual or otherwise) has populated <c>LastCheckResult</c>. The
    /// manual check in Settings is unaffected: it is self-contained, with its own
    /// inline result + Download-and-Restart button.
    /// </remarks>
    public bool ShowAppUpdateNotice =>
        _appUpdate.IsUpdateSupported
            && _autoUpdateChecksEnabled
            && _appUpdate.LastCheckResult is not null
            && !IsAppUpdateDismissed;

    /// <summary>
    /// The status-strip text on the update pill, formatted with the available
    /// version. Localized; re-resolves on a culture change.
    /// </summary>
    public string AppUpdateNoticeText =>
        _localization.Format("AppUpdate_NoticeText", _appUpdate.LastCheckResult?.TargetVersion ?? string.Empty);

    /// <summary>
    /// The status-strip tooltip on the update pill. Localized; re-resolves on a
    /// culture change.
    /// </summary>
    public string AppUpdateNoticeTooltip => _localization["AppUpdate_NoticeTooltip"];

    /// <summary>
    /// The tooltip on the dismiss button. Localized; re-resolves on a culture
    /// change.
    /// </summary>
    public string AppUpdateDismissTooltip => _localization["AppUpdate_DismissTooltip"];

    /// <summary>
    /// Whether the profile dropdown is interactive: a profile must exist and the
    /// game must not be running. The gate itself lives in the session; this just
    /// exposes the running-state so the dropdown can disable while Darktide runs.
    /// </summary>
    public bool CanSwitchProfile => !IsGameRunning && HasProfiles;

    /// <summary>
    /// Tooltip explaining the dropdown's current enabled state (the block reason
    /// when the game is running, or a first-run hint when no profile exists).
    /// Localized + re-resolves on a culture change.
    /// </summary>
    public string ProfileSwitchTooltip =>
        IsGameRunning
            ? _localization["ProfileSwitch_RunningTooltip"]
            : HasProfiles
                ? string.Empty
                : _localization["ProfileSwitch_CreateFirstTooltip"];

    /// <summary>
    /// The dropdown (or a programmatic set) changed the selection. Asks the session
    /// to make it active; the session gates it (only when the game isn't running).
    /// Then re-syncs to the session's authoritative active id so a blocked or
    /// cleared selection reverts, the dropdown never lies about the active profile.
    /// </summary>
    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (value is { } profile)
            {
                _session.RequestActive(profile.Id);
            }

            SelectedProfile = ResolveActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Mirrors the session's live running-state into <see cref="IsGameRunning"/>
    /// (the status strip, launch-availability, and dropdown-enable all cascade
    /// from it), and the session's pending-changes flag into
    /// <see cref="HasPendingStagedChanges"/> (the dirty status-strip indicator).
    /// Active-id changes are handled at the known points they can occur
    /// (dropdown request + after the dialog), not here.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProfileSession.IsRunning))
        {
            IsGameRunning = _session.IsRunning;
        }
        else if (e.PropertyName == nameof(IProfileSession.HasPendingChanges))
        {
            HasPendingStagedChanges = _session.HasPendingChanges;
        }
    }

    /// <summary>
    /// The UI culture flipped (Preferences dialog). Re-fire the property-changed
    /// events for the localized derived strings so bindings re-resolve.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)
            && e.PropertyName != "Item[]")
        {
            return;
        }

        OnPropertyChanged(nameof(GameRunningText));
        OnPropertyChanged(nameof(ProfileSwitchTooltip));
        OnPropertyChanged(nameof(NxmHandlerStatusText));
        OnPropertyChanged(nameof(NxmHandlerStatusTooltip));
        OnPropertyChanged(nameof(AppUpdateNoticeText));
        OnPropertyChanged(nameof(AppUpdateNoticeTooltip));
        OnPropertyChanged(nameof(AppUpdateDismissTooltip));
    }

    /// <summary>
    /// Re-reads the OS <c>nxm://</c> handler registration into
    /// <see cref="IsNxmRegistered"/>. Null when no platform registrar is
    /// available (the status strip shows "unavailable"). Called at startup +
    /// after the Integrations dialog closes (the only place the registration can
    /// change in-app). A probe throw is treated as "not registered" (defensive;
    /// the platform registrars catch their own probe exceptions).
    /// </summary>
    private void RefreshNxmHandlerStatus()
    {
        if (_nxmRegistrar is null)
        {
            IsNxmRegistered = null;
            return;
        }

        try
        {
            IsNxmRegistered = _nxmRegistrar.IsRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IsRegistered probe threw; treating as not registered.");
            IsNxmRegistered = false;
        }
    }

    /// <summary>
    /// The app self-update service published new state (a check resolved an
    /// update, or a download landed). The event fires on a threadpool thread
    /// (the service publishes from its background check), so the property
    /// changes are marshaled to the UI thread via the <see cref="_invokeOnUi"/>
    /// seam before touching <see cref="ObservableObject"/> bindings. Mirrors
    /// <see cref="Modificus.Curator.UI.ViewModels.ModListViewModel"/>'s
    /// <c>CheckCompleted</c> marshaling.
    /// </summary>
    private void OnAppUpdateStateChanged(object? sender, EventArgs e)
    {
        // Marshal to the UI thread: the event fires on a threadpool thread and
        // the notice properties feed UI-bound state.
        _invokeOnUi(RefreshAppUpdateNotice);
    }

    /// <summary>
    /// Re-fires the property-changed events for the notice's computed strings +
    /// the show/hide flag so the status strip re-resolves. Called from
    /// <see cref="OnAppUpdateStateChanged"/> (marshaled to the UI thread) and at
    /// construction so a check that completed during shell construction is
    /// reflected immediately.
    /// </summary>
    private void RefreshAppUpdateNotice()
    {
        OnPropertyChanged(nameof(ShowAppUpdateNotice));
        OnPropertyChanged(nameof(AppUpdateNoticeText));
        OnPropertyChanged(nameof(AppUpdateNoticeTooltip));
    }

    /// <summary>
    /// Opens the "Manage profiles…" dialog, then reloads the profile list and
    /// re-syncs the selection to the session's active id. The dialog applies
    /// active changes live through the session during its session, so by the time
    /// it closes the session already reflects whatever the gate allowed; the shell
    /// just refreshes its list snapshot and follows the authoritative active id.
    /// After the dialog closes, also processes any pending DMF new-profile prompt
    /// the dialog's create may have triggered (the prompt fires here, on the main
    /// window, not dialog-on-dialog).
    /// </summary>
    [RelayCommand]
    private async Task ManageProfiles()
    {
        await _dialogs.ShowManageProfilesAsync();

        // _syncing MUST be set BEFORE the Profiles swap, not after. Replacing the
        // Profiles collection causes the ComboBox to fire spurious SelectedItem
        // changes (first null because the old reference is gone, then a value
        // match against the new collection for the previously-selected name).
        // Those fire OnSelectedProfileChanged with the stale value, which would
        // call RequestActive and revert the session to the pre-dialog selection
        // (e.g. undoing the active change CommitCreate just made). Bracketing the
        // entire Profiles + SelectedProfile re-sync under _syncing=true makes
        // those spurious events no-ops.
        _syncing = true;
        try
        {
            Profiles = _profileService.ListProfiles();
            SelectedProfile = ResolveActive();
        }
        finally
        {
            _syncing = false;
        }

        // Surface any DMF prompt the dialog's create may have triggered. Fires
        // after the ManageProfiles dialog is gone so the prompt is the topmost
        // modal. Safe no-op when no create happened during the dialog.
        await _dmfPrompts.ProcessPendingAsync();

        // The DMF prompt (if it fired + the user accepted) added a mod to the
        // active profile. Reload so the new row shows without a profile switch.
        // Cheap no-op when nothing changed.
        ModList.Reload();
    }

    /// <summary>
    /// Opens the Preferences dialog (theme / font scale / language). Each
    /// preference applies + persists immediately through the dialog, so on
    /// return the shell only needs to let its localized bindings refresh (which
    /// the culture-changed subscription handles for a language switch).
    /// </summary>
    [RelayCommand]
    private async Task OpenPreferences()
    {
        await _dialogs.ShowPreferencesAsync();
    }

    /// <summary>
    /// Opens the Integrations dialog (Nexus auth: OAuth login + API-key validate
    /// + sign-out, plus the explicit nxm:// handler registration toggle).
    /// Nexus-only. After the dialog closes, re-reads the nxm handler status so
    /// the status strip reflects any register/unregister the user did inside the
    /// dialog, and reloads the mod list (cheap no-op when nothing changed). This
    /// is also the Integrations entry point the first-run Welcome onboarding
    /// reuses (via <see cref="OpenIntegrationsAsync"/>), so the nxm handler
    /// status refresh applies identically after a Welcome "Set up Nexus" choice.
    /// </summary>
    [RelayCommand]
    private Task OpenIntegrations() => OpenIntegrationsAsync();

    /// <summary>
    /// The awaitable Integrations flow: shows the dialog, then refreshes the
    /// nxm handler status + reloads the mod list. Called by the
    /// <see cref="OpenIntegrations"/> command wrapper and by the onboarding
    /// coordinator's "Set up Nexus" path (passed in as a delegate at
    /// composition). The DMF auth-trigger processing was removed (the DMF
    /// prompt is profile-creation-only now); this method no longer calls
    /// <see cref="DmfPromptService.ProcessPendingAsync"/>.
    /// </summary>
    internal async Task OpenIntegrationsAsync()
    {
        await _dialogs.ShowIntegrationsAsync();

        // The user may have toggled the nxm handler inside the dialog; re-read
        // the OS state so the status strip label stays accurate.
        RefreshNxmHandlerStatus();

        // Reload so a change inside the dialog is reflected without a profile
        // switch. Cheap no-op when nothing changed.
        ModList.Reload();
    }

    /// <summary>
    /// Opens the Settings dialog (discovery paths + Storage open-folder buttons
    /// + the startup self-update toggle), then reloads the mod list + refreshes
    /// the status-strip update notice. Each setting applies + persists
    /// immediately through the dialog; on close the shell: (1) reloads the mod
    /// list, so any out-of-band change to the repository is reflected in the
    /// <see cref="ModListViewModel.Mods"/> snapshot; and (2) re-reads the
    /// startup-check toggle so a notice shown before the toggle was turned off
    /// is dismissed immediately, and a notice previously hidden by an off
    /// toggle re-enables the moment it is turned back on. (Discovery overrides
    /// are read live by the next <c>Discover()</c> / launch; no shell-side
    /// action needed for those.)
    /// </summary>
    [RelayCommand]
    private async Task OpenSettings()
    {
        await _dialogs.ShowSettingsAsync();
        ModList.Reload();

        // The startup-check toggle can only change inside Settings; re-read it on
        // close so the notice visibility tracks the current config. No
        // config-change subscription is needed because Settings is the sole
        // mutation point.
        _autoUpdateChecksEnabled = _configLoader.Load().AppUpdates.CheckOnStartup;
        RefreshAppUpdateNotice();
    }

    /// <summary>
    /// The notice-click flow: confirms the download, then runs the download under
    /// a modal spinner and applies the update on restart. Cancel on the confirm
    /// dismisses the notice for this session (cancel = "dismiss for now", not
    /// "not now"); the explicit <see cref="DismissAppUpdate"/> command (the x
    /// button) also dismisses for the session. Download failures surface an
    /// alert and do NOT proceed to apply.
    /// </summary>
    [RelayCommand]
    private async Task CheckAppUpdateNow()
    {
        var info = _appUpdate.LastCheckResult;
        if (info is null)
        {
            // Nothing to download (the notice should not be visible in this
            // state, but a race with a clearing check is harmless to guard).
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _localization["AppUpdate_ConfirmTitle"],
            _localization.Format("AppUpdate_ConfirmMessage", info.TargetVersion));

        if (!confirmed)
        {
            // Cancel dismisses the notice for this session; the explicit dismiss
            // button (DismissAppUpdate) also dismisses for the session. The
            // notice re-shows next startup if an update is still available.
            IsAppUpdateDismissed = true;
            return;
        }

        try
        {
            // The download is I/O; offload it to a thread-pool task inside the
            // spinner's work delegate so the UI thread stays free. The
            // ProgressDialog is indeterminate (the final design), so no
            // percentage is surfaced.
            await _dialogs.ShowProgressAsync(
                _localization["AppUpdate_DownloadingTitle"],
                _localization["AppUpdate_DownloadingMessage"],
                () => Task.Run(async () =>
                {
                    // Bare await inside Task.Run (no SynchronizationContext); the
                    // VM-file convention forbids ConfigureAwait(false) entirely.
                    await _appUpdate.DownloadUpdatesAsync();
                    return true;
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App update download failed.");
            await _dialogs.ShowAlertAsync(
                _localization["AppUpdate_DownloadFailedTitle"],
                _localization["AppUpdate_DownloadFailedMessage"] + " " + ex.Message);
            return;
        }

        // Success: terminates this process + relaunches under the new version.
        _appUpdate.ApplyUpdatesAndRestart();
    }

    /// <summary>
    /// Dismisses the update notice for this session (in-memory only; not
    /// persisted). The notice re-shows next startup if an update is still
    /// available. Bound to the drawn close Path on the status-strip pill.
    /// </summary>
    [RelayCommand]
    private void DismissAppUpdate()
    {
        IsAppUpdateDismissed = true;
    }

    /// <summary>
    /// Resolves the session's active id to the matching profile in the current
    /// list (null when the id is unknown or no profile exists).
    /// </summary>
    private ProfileSummary? ResolveActive() =>
        _session.ActiveProfileId is Guid id
            ? Profiles.FirstOrDefault(p => p.Id == id)
            : null;

    /// <summary>
    /// Launches the active profile modded. Branches on
    /// <see cref="LaunchResult.Status"/>:
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>an
    /// immediate <see cref="IsGameRunning"/> refresh so the indicator + CanLaunch
    /// react at once (not on the next poll).</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term><description>
    /// opens the escape-hatch dialog with the missing fields. No retry: the user
    /// clicks Launch again after submitting.</description></item>
    /// <item><term><see cref="LaunchStatus.StagingFailed"/></term><description>shows
    /// a localized modal alert: the framing + hint followed by the raised
    /// exception's body (the runtime/OS error carried on the result).</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>shows a
    /// modal alert with the result's message.</description></item>
    /// </list>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var result = _launchService.Launch(profile.Id);
        switch (result.Status)
        {
            case LaunchStatus.Launched:
                // Refresh running-state right away so the indicator + CanLaunch
                // react at once. The session's polling timer would catch up
                // eventually, but the user just clicked Launch; they should see
                // the change immediately. The session's Refresh is the source
                // of truth; the shell mirrors IsRunning from it.
                // The successful stage+spawn just re-staged the active profile's
                // mod tree, so any prior pending edits are now reflected: clear
                // the pending-changes flag (the dirty indicator drops).
                _session.HasPendingChanges = false;
                _session.Refresh();
                _logger.LogInformation("Launched profile {Id} ('{Name}').", profile.Id, profile.Name);
                break;

            case LaunchStatus.DiscoveryIncomplete:
                // No retry: the user explicitly clicks Launch again after
                // submitting. A loop here would trap them if they could not get
                // the paths right.
                await _dialogs.ShowDiscoveryEscapeHatchAsync(result.MissingDiscoveryFields);
                _logger.LogInformation(
                    "Discovery incomplete on launch of {Id}; showed escape-hatch for fields: {Fields}.",
                    profile.Id, string.Join(", ", result.MissingDiscoveryFields));
                break;

            case LaunchStatus.StagingFailed:
                // A staging link could not be created. RelayLaunchService logged
                // the full exception and carried the raised exception's body on
                // the result. The user sees the localized framing + hint, then
                // the runtime/OS detail appended, mirroring the Update/Import
                // failure alerts.
                await _dialogs.ShowAlertAsync(
                    _localization["Launch_StagingFailedTitle"],
                    _localization["Launch_StagingFailedMessage"] + " " + (result.Message ?? string.Empty));
                _logger.LogWarning("Staging failed on launch of {Id}.", profile.Id);
                break;

            case LaunchStatus.Error:
                await _dialogs.ShowAlertAsync(
                    _localization["Launch_ErrorTitle"],
                    result.Message ?? string.Empty);
                _logger.LogWarning("Launch of {Id} failed: {Message}.", profile.Id, result.Message);
                break;
        }
    }

    /// <summary>A profile must be selected and the game must not be running.</summary>
    private bool CanLaunch() => SelectedProfile is not null && !IsGameRunning;
}
