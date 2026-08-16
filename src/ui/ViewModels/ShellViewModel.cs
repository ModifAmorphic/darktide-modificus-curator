using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Modificus Curator main window, the app shell.
/// Owns the SplitView navigation rail (five hosted destinations), the global
/// Launch action, the global running / pending / nxm / app-update status strip,
/// and the shell-owned modal queue (drained on destination entry). The active
/// profile is owned by <see cref="IProfileSession"/>; launch availability
/// derives directly from <see cref="IProfileSession.ActiveProfileId"/> +
/// <see cref="IsGameRunning"/> + the shell's own launch-attempt state, never a
/// cached snapshot.
/// </summary>
/// <remarks>
/// <para><b>Navigation lifecycle:</b> a real destination change runs the current
/// destination's leave effects before switching. Leaving Profiles awaits the
/// unsaved-changes three-choice guard (Cancel/Save-failure keeps the destination
/// unchanged); leaving Nexus cancels in-flight auth + reloads the mod list;
/// leaving Settings reloads the mod list +
/// re-reads the startup-check toggle + refreshes the app-update notice. Enter
/// effects: Settings rehydrates from config synchronously; Nexus
/// awaits its auth refresh so the page paints while its state resolves. After
/// the enter effects the shell drains its modal queue for the entered
/// destination (see <see cref="IShellModalQueue"/>), so a queued modal (the DMF
/// install prompt after a profile create) runs as the topmost modal with the
/// page already painted underneath. Selecting the current destination is a
/// strict no-op (no guards, effects, queue drain, or config reads), so a queued
/// modal survives visits to other destinations and runs only on a real entry
/// into its destination.</para>
/// <para><b>No kitchen-sink lifecycle interface:</b> Profiles, Settings, and
/// Nexus have deliberately different capabilities, so the shell
/// calls each concrete page VM directly rather than routing through a shared
/// <c>IPage</c>/<c>INavigationService</c>. The hosted VMs are
/// application-lifetime singletons; navigation never calls their old Window-
/// close final-cleanup (<c>Detach</c>) paths.</para>
/// <para><b>Modal timing is the queue's, modal content is the enqueuer's:</b>
/// the shell owns WHEN a queued modal fires (on the next real navigation into
/// the modal's destination, after the enter effects, including after the
/// Profiles unsaved-changes guard) while each enqueuing service owns WHAT the
/// modal does + its post-modal follow-up. The shell does not know which
/// services enqueue; that is the point.</para>
/// <para><b>Running-state is live:</b> <see cref="IsGameRunning"/> is mirrored
/// from <see cref="IProfileSession.IsRunning"/>, which a polling timer
/// refreshes, so the status strip + launch-availability react within a few
/// seconds of Darktide starting or stopping.</para>
/// <para><b>Launch-attempt state is the shell's, distinct from
/// running-state:</b> <see cref="IsLaunchAttemptInProgress"/> covers the whole
/// launch attempt (the render yield, the synchronous discovery/staging/spawn
/// call, failure-dialog handling, and the post-spawn wait for the session's
/// detector to notice Darktide and the spawned Relay process to exit). An
/// executable launch sets it before anything else; a successful spawn keeps
/// it until both the session's live running-state signal observes the game
/// and the spawned Relay process exits (its exit observed as a bare
/// completion task from the launch facade), or a bounded timeout elapses
/// releasing the whole combined wait, so the process-detection gap after a
/// spawn can never double-launch and a signal that never arrives can never
/// wedge the button. This is signal observation, not process supervision:
/// the shell holds no process handle and manages no process lifetime, and
/// Darktide stays untracked beyond the session signal.
/// </para>
/// <para><b>Localizable text is live:</b> the status strings + the current page
/// title re-resolve from <see cref="LocalizationService"/> on a culture
/// change.</para>
/// </remarks>
public partial class ShellViewModel : LocalizedViewModel, IShellNavigation
{
    private readonly IProfileSession _session;
    private readonly IRelayLaunchService _launchService;
    private readonly IDialogService _dialogs;
    private readonly ModListViewModel _modList;
    private readonly ProfilesViewModel _profiles;
    private readonly IntegrationsViewModel _integrations;
    private readonly PreferencesViewModel _preferences;
    private readonly SettingsViewModel _settings;
    private readonly IAppUpdateService _appUpdate;
    private readonly IConfigLoader _configLoader;
    private readonly Action<Action> _invokeOnUi;
    private readonly INxmRegistrationState _nxmRegistration;
    private readonly IShellModalQueue _modalQueue;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Func<Task> _yieldForLaunchRender;
    private readonly Func<Task> _launchHandoffTimeout;

    // Whether the automatic startup self-update check is enabled
    // (CuratorConfig.AppUpdates.CheckOnStartup). The status-strip update notice
    // shows only while this is true. Read at construction + re-read when leaving
    // Settings (the sole place it can change), so toggling it off then leaving
    // Settings dismisses a showing notice immediately. No config-change
    // subscription: Settings is the sole mutation point.
    private bool _autoUpdateChecksEnabled;

    public ShellViewModel(
        IProfileSession session,
        IRelayLaunchService launchService,
        IDialogService dialogs,
        LocalizationService localization,
        ProfilesViewModel profiles,
        ModListViewModel modList,
        IntegrationsViewModel integrations,
        PreferencesViewModel preferences,
        SettingsViewModel settings,
        IAppUpdateService appUpdate,
        IShellModalQueue modalQueue,
        Action<Action> invokeOnUi,
        ILogger<ShellViewModel> logger,
        IConfigLoader configLoader,
        INxmRegistrationState nxmRegistration,
        Func<Task>? yieldForLaunchRender = null,
        Func<Task>? launchHandoffTimeout = null)
        : base(localization)
    {
        _session = session;
        _launchService = launchService;
        _dialogs = dialogs;
        _profiles = profiles;
        _modList = modList;
        _integrations = integrations;
        _preferences = preferences;
        _settings = settings;
        _appUpdate = appUpdate;
        _modalQueue = modalQueue ?? throw new ArgumentNullException(nameof(modalQueue));
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger;
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _nxmRegistration = nxmRegistration ?? throw new ArgumentNullException(nameof(nxmRegistration));
        // Timing seams for the launch attempt: production defaults yield once
        // to the Avalonia dispatcher (Loaded priority) and bound the post-spawn
        // handoff with a real 30s delay; tests inject completed or
        // TaskCompletionSource-backed tasks for deterministic control (a real
        // dispatcher yield would hang a unit test).
        _yieldForLaunchRender = yieldForLaunchRender ?? YieldForLaunchRender;
        _launchHandoffTimeout = launchHandoffTimeout ?? (static () => Task.Delay(LaunchHandoffTimeout));
        _autoUpdateChecksEnabled = _configLoader.Load().AppUpdates.CheckOnStartup;

        _isGameRunning = _session.IsRunning;
        _hasPendingStagedChanges = _session.HasPendingChanges;

        // Seed the nxm handler status strip from the shared registration state.
        // The RefreshFromOs call is THE startup seed probe; later publishes (the
        // Nexus-enter probe + register/release actions) arrive via Changed.
        IsNxmRegistered = _nxmRegistration.IsAvailable ? _nxmRegistration.IsRegistered : null;
        _nxmRegistration.Changed += OnNxmRegistrationChanged;
        _nxmRegistration.RefreshFromOs();

        _session.PropertyChanged += OnSessionPropertyChanged;

        // Subscribe to the app self-update state changes so the status-strip
        // notice appears the moment a check resolves an update (the startup
        // check fires on a background task). Also reflect any result that
        // already landed during shell construction.
        _appUpdate.UpdateStateChanged += OnAppUpdateStateChanged;
        RefreshAppUpdateNotice();

        _logger.LogInformation(
            "Shell initialized: active={ActiveId}; Darktide running: {IsRunning}; launch facade: {LaunchFacade}",
            _session.ActiveProfileId?.ToString() ?? "(none)",
            IsGameRunning,
            _launchService.GetType().Name);
    }

    // ---- navigation -------------------------------------------------------

    /// <summary>
    /// The current hosted destination. Starts on Mods. Mutated only through
    /// <see cref="NavigateAsync"/>, which runs the guarded leave/enter
    /// lifecycle; the private setter prevents callers from switching the page
    /// while bypassing those effects.
    /// </summary>
    public ShellDestination CurrentDestination
    {
        get => _currentDestination;
        private set
        {
            if (SetProperty(ref _currentDestination, value))
            {
                OnPropertyChanged(nameof(IsProfilesSelected));
                OnPropertyChanged(nameof(IsModsSelected));
                OnPropertyChanged(nameof(IsNexusIntegrationsSelected));
                OnPropertyChanged(nameof(IsPreferencesSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
                OnPropertyChanged(nameof(IsProfilesVisible));
                OnPropertyChanged(nameof(IsModsVisible));
                OnPropertyChanged(nameof(IsNexusIntegrationsVisible));
                OnPropertyChanged(nameof(IsPreferencesVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(CurrentDestinationTitle));
            }
        }
    }
    private ShellDestination _currentDestination = ShellDestination.Mods;

    /// <summary>Whether the navigation pane is expanded. Starts collapsed
    /// (compact icon rail only). Toggled by the hamburger button.</summary>
    [ObservableProperty]
    private bool _isNavigationPaneOpen;

    /// <summary>The localized title of the current destination, shown in the
    /// global header. Re-resolves on a destination change and on a culture
    /// change.</summary>
    public string CurrentDestinationTitle => _currentDestination switch
    {
        ShellDestination.Profiles => _localization["Profiles_Title"],
        ShellDestination.Mods => _localization["ModList_Header"],
        ShellDestination.NexusIntegrations => _localization["Integrations_Title"],
        ShellDestination.Preferences => _localization["Preferences_Title"],
        ShellDestination.Settings => _localization["Settings_Title"],
        _ => string.Empty,
    };

    // Selected projections: drive the nav-rail buttons' `selected` class.
    public bool IsProfilesSelected => CurrentDestination == ShellDestination.Profiles;
    public bool IsModsSelected => CurrentDestination == ShellDestination.Mods;
    public bool IsNexusIntegrationsSelected => CurrentDestination == ShellDestination.NexusIntegrations;
    public bool IsPreferencesSelected => CurrentDestination == ShellDestination.Preferences;
    public bool IsSettingsSelected => CurrentDestination == ShellDestination.Settings;

    // Visibility projections: drive which hosted page is shown in the content
    // area. Exactly one is true at a time.
    public bool IsProfilesVisible => CurrentDestination == ShellDestination.Profiles;
    public bool IsModsVisible => CurrentDestination == ShellDestination.Mods;
    public bool IsNexusIntegrationsVisible => CurrentDestination == ShellDestination.NexusIntegrations;
    public bool IsPreferencesVisible => CurrentDestination == ShellDestination.Preferences;
    public bool IsSettingsVisible => CurrentDestination == ShellDestination.Settings;

    /// <summary>
    /// Toggles the navigation pane between the compact icon rail and the
    /// expanded icon + label rail.
    /// </summary>
    [RelayCommand]
    private void ToggleNavigationPane() => IsNavigationPaneOpen = !IsNavigationPaneOpen;

    /// <summary>
    /// Navigates to <paramref name="destination"/>, running the guarded
    /// leave/enter lifecycle. A same-destination call is a strict no-op.
    /// Driven by the nav-rail buttons (the destination is passed as the
    /// command parameter).
    /// </summary>
    [RelayCommand]
    private Task Navigate(ShellDestination destination) => NavigateAsync(destination);

    /// <summary>
    /// The guarded navigation core. Same-destination is a strict no-op (so a
    /// queued modal survives same-destination clicks; it is consumed only by a
    /// real navigation into its destination). For a real change: (1) leaving
    /// Profiles awaits the unsaved-changes three-choice guard, which on
    /// Cancel/Save-failure keeps everything unchanged; (2) run the current
    /// destination's other leave effects; (3) switch <see cref="CurrentDestination"/>;
    /// (4) run the target's enter effects (Settings rehydrates synchronously;
    /// Nexus awaits its auth refresh); (5) drain the shell-owned modal queue
    /// for the destination, so a queued modal (the DMF prompt) runs as the
    /// topmost modal over the painted page. The destination is switched before
    /// any enter await so it stays active even if a refresh or a drained modal
    /// reports an error through its own behavior.
    /// </summary>
    public async Task NavigateAsync(ShellDestination destination)
    {
        if (destination == CurrentDestination)
        {
            return;
        }

        var from = CurrentDestination;

        // Leaving Profiles first asks the unsaved-changes guard. A false result
        // (Cancel/ESC/X, or a Save that the service rejected) keeps
        // CurrentDestination + all target lifecycle state unchanged.
        if (from == ShellDestination.Profiles
            && !await _profiles.ConfirmCanNavigateAwayAsync())
        {
            return;
        }

        // Leave effects owned here (the former post-dialog effects for these
        // destinations). Each runs exactly once, at the leave point. Leaving
        // Nexus cancels in-flight auth + reloads the mod list; the nxm
        // registration state is refreshed at Nexus ENTER (its deliberate probe
        // point), never on the way out.
        if (from == ShellDestination.NexusIntegrations)
        {
            _integrations.Deactivate();
            _modList.Reload();
        }
        else if (from == ShellDestination.Settings)
        {
            _modList.Reload();
            _autoUpdateChecksEnabled = _configLoader.Load().AppUpdates.CheckOnStartup;
            RefreshAppUpdateNotice();
        }

        // Switch the destination BEFORE the enter awaits so the page paints
        // underneath any modal the enter path raises (a drained queue modal
        // runs after the destination is already set, so the modal sits over
        // the freshly painted page).
        CurrentDestination = destination;

        // Enter effects. Settings rehydrates from config synchronously so
        // escape-hatch / config changes are visible without a transient stale
        // page; Nexus shows first, then awaits its auth refresh
        // (paint-then-resolve, matching the former dialog behavior).
        if (destination == ShellDestination.Settings)
        {
            _settings.RefreshFromConfig();
        }
        else if (destination == ShellDestination.NexusIntegrations)
        {
            await _integrations.RefreshAsync();
        }

        // Drain the shell-owned modal queue for the entered destination (after
        // the destination switch + the enter effects, so the page is painted
        // underneath the modal). A same-destination call never reaches here,
        // so a queued modal survives visits to other destinations and runs
        // only on a real entry into its destination. Each drained modal owns
        // its own follow-up (the DMF prompt reloads the list itself).
        await _modalQueue.DrainAsync(destination);
    }

    // ---- hosted page view models ------------------------------------------

    /// <summary>The Profiles destination view model.</summary>
    public ProfilesViewModel Profiles => _profiles;

    /// <summary>The Mods destination view model (the dominant content area).</summary>
    public ModListViewModel ModList => _modList;

    /// <summary>The Nexus destination view model.</summary>
    public IntegrationsViewModel Integrations => _integrations;

    /// <summary>The Preferences destination view model.</summary>
    public PreferencesViewModel Preferences => _preferences;

    /// <summary>The Settings destination view model.</summary>
    public SettingsViewModel Settings => _settings;

    // ---- running / pending status -----------------------------------------

    /// <summary>
    /// Whether Darktide is currently running, mirrored LIVE from
    /// <see cref="IProfileSession.IsRunning"/> (a polling timer refreshes it).
    /// Gates launch.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
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
    /// running. One of three mutually-exclusive dot states.
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

    // ---- nxm handler status -----------------------------------------------

    /// <summary>
    /// Whether Curator is currently the OS <c>nxm://</c> handler, mirrored from
    /// the shared <see cref="INxmRegistrationState"/> (last-known), or
    /// <c>null</c> when no platform registrar is available. Seeded by the one
    /// startup probe in the constructor and re-read on each publish (the
    /// Nexus-enter probe + register/release actions). No polling; may be stale
    /// if an external app changed ownership.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NxmHandlerStatusText))]
    [NotifyPropertyChangedFor(nameof(NxmHandlerStatusTooltip))]
    [NotifyPropertyChangedFor(nameof(IsNxmEnabled))]
    private bool? _isNxmRegistered;

    /// <summary>
    /// Whether the green status-strip indicator should show for the nxm handler
    /// state: <c>true</c> only when Curator is the registered OS handler.
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

    // ---- app self-update notice -------------------------------------------

    /// <summary>
    /// Whether the user dismissed the update notice this session. Session-only:
    /// not persisted. Re-shown next startup if an update is still available.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAppUpdateNotice))]
    private bool _isAppUpdateDismissed;

    /// <summary>
    /// Whether the dismissible update pill should show in the status strip:
    /// self-update must be supported, the automatic startup check must be
    /// enabled, a check must have found an update, and the user must not have
    /// dismissed it this session.
    /// </summary>
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

    /// <summary>The status-strip tooltip on the update pill. Localized.</summary>
    public string AppUpdateNoticeTooltip => _localization["AppUpdate_NoticeTooltip"];

    /// <summary>The tooltip on the dismiss button. Localized.</summary>
    public string AppUpdateDismissTooltip => _localization["AppUpdate_DismissTooltip"];

    // ---- subscriptions ----------------------------------------------------

    /// <summary>
    /// Mirrors the session's live running-state + pending-changes flag. Active-
    /// id changes are handled at their known points (navigation + the Profiles
    /// page), not here; this only mirrors the running + pending signals.
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
        else if (e.PropertyName == nameof(IProfileSession.ActiveProfileId))
        {
            // Launch availability derives from the live active id; re-evaluate
            // the moment it changes rather than waiting for the running-state
            // poll.
            LaunchCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The shell's localized property names, re-fired by the shared
    /// culture-refresh base on a culture change (status strip + page title).
    /// </summary>
    protected override IReadOnlyList<string> LocalizedProperties { get; } = new[]
    {
        nameof(CurrentDestinationTitle),
        nameof(GameRunningText),
        nameof(NxmHandlerStatusText),
        nameof(NxmHandlerStatusTooltip),
        nameof(AppUpdateNoticeText),
        nameof(AppUpdateNoticeTooltip),
        nameof(AppUpdateDismissTooltip),
    };

    /// <summary>
    /// Re-reads the OS <c>nxm://</c> handler registration into
    /// <see cref="IsNxmRegistered"/> from the shared registration state, which
    /// publishes on the UI thread after each deliberate probe (the startup
    /// seed, the Nexus-enter refresh, and register/release actions).
    /// </summary>
    private void OnNxmRegistrationChanged() =>
        IsNxmRegistered = _nxmRegistration.IsAvailable
            ? _nxmRegistration.IsRegistered
            : null;

    /// <summary>
    /// The app self-update service published new state (a check resolved an
    /// update, or a download landed). The event fires on a threadpool thread,
    /// so the refresh is marshaled to the UI thread via <see cref="_invokeOnUi"/>
    /// before touching bindings.
    /// </summary>
    private void OnAppUpdateStateChanged(object? sender, EventArgs e)
    {
        _invokeOnUi(RefreshAppUpdateNotice);
    }

    /// <summary>
    /// Re-fires the property-changed events for the notice's computed strings +
    /// the show/hide flag so the status strip re-resolves.
    /// </summary>
    private void RefreshAppUpdateNotice()
    {
        OnPropertyChanged(nameof(ShowAppUpdateNotice));
        OnPropertyChanged(nameof(AppUpdateNoticeText));
        OnPropertyChanged(nameof(AppUpdateNoticeTooltip));
    }

    // ---- app self-update flow --------------------------------------------

    /// <summary>
    /// The notice-click flow: confirms the download, then runs the download
    /// under a modal spinner and applies the update on restart. Cancel on the
    /// confirm dismisses the notice for this session. Download failures
    /// surface an alert and do NOT proceed to apply.
    /// </summary>
    [RelayCommand]
    private async Task CheckAppUpdateNow()
    {
        var info = _appUpdate.LastCheckResult;
        if (info is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _localization["AppUpdate_ConfirmTitle"],
            _localization.Format("AppUpdate_ConfirmMessage", info.TargetVersion));

        if (!confirmed)
        {
            IsAppUpdateDismissed = true;
            return;
        }

        try
        {
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
    /// available.
    /// </summary>
    [RelayCommand]
    private void DismissAppUpdate()
    {
        IsAppUpdateDismissed = true;
    }

    // ---- launch -----------------------------------------------------------

    /// <summary>
    /// How long a successful launch keeps the attempt state while waiting for
    /// the session's running-state signal to observe Darktide and the spawned
    /// Relay process to exit. One cap over the whole combined wait, so a
    /// signal that never arrives (a detector that never sees the game, a
    /// spawn that died silently without exiting observably) still re-enables
    /// Launch. Starts after <see cref="IRelayLaunchService"/> returns
    /// <see cref="LaunchStatus.Launched"/>, never during
    /// discovery/staging/spawn.
    /// </summary>
    internal static readonly TimeSpan LaunchHandoffTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The production render yield: one Avalonia dispatcher yield at
    /// <see cref="DispatcherPriority.Loaded"/> (after layout + render, before
    /// subsequent input), so the freshly-disabled Launch button paints its
    /// disabled style before the synchronous launch work resumes.
    /// </summary>
    private static async Task YieldForLaunchRender() => await Dispatcher.Yield(DispatcherPriority.Loaded);

    /// <summary>
    /// Whether a launch attempt is executing: from the executable launch
    /// request, through the render yield + the synchronous launch call +
    /// failure-dialog handling, until the post-spawn handoff resolves (the
    /// session's running-state signal observing Darktide and the spawned
    /// Relay process exiting, or the bounded timeout releasing the whole
    /// combined wait). Gates <see cref="LaunchCommand"/> alongside the
    /// active-profile + running gates. Shell-owned and distinct from
    /// <see cref="IsGameRunning"/> (the session's process-detected state): the
    /// attempt covers the gap a detector cannot yet see.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private bool _isLaunchAttemptInProgress;

    /// <summary>
    /// Launches the active profile modded. Resolves the active id from the
    /// session at execution time, sets the launch-attempt state (disabling the
    /// button) before anything else, yields once so the disabled style paints,
    /// then calls the launch service and branches on
    /// <see cref="LaunchResult.Status"/>:
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>an
    /// immediate <see cref="IsGameRunning"/> refresh so the indicator +
    /// CanLaunch react at once, and clearing the pending-changes flag (the
    /// successful stage re-staged the mod tree); then the attempt state stays
    /// set until the session's running-state signal observes Darktide and
    /// the spawned Relay process exits (the bare exit task from the result),
    /// or <see cref="LaunchHandoffTimeout"/> elapses releasing the whole
    /// combined wait.</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term>
    /// <description>opens the escape-hatch dialog with the missing fields. No
    /// retry.</description></item>
    /// <item><term><see cref="LaunchStatus.StagingFailed"/></term>
    /// <description>shows a localized modal alert with the framing + the raised
    /// exception's body.</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>shows a
    /// modal alert with the result's message.</description></item>
    /// </list>
    /// The attempt state stays set while a failure dialog is open and clears
    /// after the dialog handling (and on any exception path), so retry becomes
    /// possible exactly then.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        if (_session.ActiveProfileId is not Guid profileId || IsLaunchAttemptInProgress)
        {
            // No active profile: nothing to launch. An attempt already in
            // progress: refuse. CanExecute gates the button; this guards
            // direct/programmatic Execute calls that bypass it.
            return;
        }

        IsLaunchAttemptInProgress = true;
        try
        {
            // One render yield: the attempt state just flipped CanExecute to
            // false; letting layout + render run first paints the disabled
            // button before the synchronous launch work resumes.
            await _yieldForLaunchRender();

            var result = _launchService.Launch(profileId);
            switch (result.Status)
            {
                case LaunchStatus.Launched:
                    // The successful stage+spawn just re-staged the active profile's
                    // mod tree, so any prior pending edits are now reflected: clear
                    // the pending-changes flag (the dirty indicator drops), then
                    // refresh running-state so the indicator + CanLaunch react at
                    // once rather than on the next poll.
                    _session.HasPendingChanges = false;
                    _session.Refresh();
                    _logger.LogInformation("Launched profile {Id}.", profileId);

                    // Fire-and-forget spawn: the detector needs time to notice
                    // Darktide, and Relay finishes its injection work before it
                    // exits. Keep the attempt state until the session observes
                    // the game and the spawned process exits (or the bounded
                    // timeout releases the wait) so the gap cannot double-launch.
                    await WaitForRunningStateHandoffAsync(result.RelayExited);
                    break;

                case LaunchStatus.DiscoveryIncomplete:
                    // No retry: the user explicitly clicks Launch again after
                    // submitting.
                    await _dialogs.ShowDiscoveryEscapeHatchAsync(result.MissingDiscoveryFields);
                    _logger.LogInformation(
                        "Discovery incomplete on launch of {Id}; showed escape-hatch for fields: {Fields}.",
                        profileId, string.Join(", ", result.MissingDiscoveryFields));
                    break;

                case LaunchStatus.StagingFailed:
                    await _dialogs.ShowAlertAsync(
                        _localization["Launch_StagingFailedTitle"],
                        _localization["Launch_StagingFailedMessage"] + " " + (result.Message ?? string.Empty));
                    _logger.LogWarning("Staging failed on launch of {Id}.", profileId);
                    break;

                case LaunchStatus.Error:
                    await _dialogs.ShowAlertAsync(
                        _localization["Launch_ErrorTitle"],
                        result.Message ?? string.Empty);
                    _logger.LogWarning("Launch of {Id} failed: {Message}.", profileId, result.Message);
                    break;
            }
        }
        finally
        {
            // The single clear point: after failure-dialog handling, after the
            // post-spawn handoff resolves (signal or timeout), and on any
            // exception path. When Darktide was observed, IsGameRunning keeps
            // Launch disabled; on timeout, retry becomes possible.
            IsLaunchAttemptInProgress = false;
        }
    }

    /// <summary>
    /// Waits out the process-detection handoff after a successful launch:
    /// resolves when the session's live running-state signal observes Darktide
    /// AND the spawned Relay process exits (the bare exit task from the launch
    /// facade; a null task, e.g. a non-tracking result, is treated as already
    /// complete), or when the bounded timeout elapses, releasing the whole
    /// combined wait. Observes the existing session signal + the facade's
    /// exit task only (never a process handle; the session's polling detector
    /// owns noticing the game, the facade owns the spawned handle's lifetime).
    /// A false polling result never resolves the wait; the temporary
    /// subscription is removed deterministically.
    /// </summary>
    private async Task WaitForRunningStateHandoffAsync(Task? relayExit)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(IProfileSession.IsRunning) && _session.IsRunning)
            {
                observed.TrySetResult();
            }
        };

        // Subscribe before the initial check so a flip between the eager
        // refresh and this wait cannot be missed.
        _session.PropertyChanged += handler;
        try
        {
            if (_session.IsRunning)
            {
                // Already running at handoff entry: the detector half is
                // satisfied, but the relay wait below must still apply.
                observed.TrySetResult();
            }

            var conditions = Task.WhenAll(observed.Task, relayExit ?? Task.CompletedTask);
            if (await Task.WhenAny(conditions, _launchHandoffTimeout()) == conditions)
            {
                return;
            }

            _logger.LogInformation(
                "Launch handoff timed out without observing Darktide and Relay exit; re-enabling Launch.");
        }
        finally
        {
            _session.PropertyChanged -= handler;
        }
    }

    /// <summary>An active profile must exist, the game must not be running,
    /// and no launch attempt may be in progress. Derived directly from the
    /// session + the shell's attempt state so a live active-id change
    /// re-evaluates at once.</summary>
    private bool CanLaunch() =>
        _session.ActiveProfileId is not null && !IsGameRunning && !IsLaunchAttemptInProgress;
}
