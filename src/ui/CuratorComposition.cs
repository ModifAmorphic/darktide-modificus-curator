using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Views;
using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.Nxm;

namespace Modificus.Curator.UI;

/// <summary>
/// The DI composition root. Loads config, builds the structured logger, wires
/// every library's <c>Add&lt;Library&gt;()</c> extension, and registers the UI
/// surface (main window + view model + dialog service). All data operations flow
/// through the registered library services; the UI never touches files or APIs
/// directly.
/// </summary>
public static class CuratorComposition
{
    /// <summary>Builds and returns the application service provider.</summary>
    public static IServiceProvider Build()
    {
        // 1. One config loader: used for the transient startup snapshot (to
        //    build the logger) AND registered as the live-read IConfigLoader
        //    singleton so every consumer re-reads the current disk state on
        //    each operation (the config file is tiny; a startup cache would
        //    only create staleness for the Settings window, which writes
        //    config at runtime).
        var loader = new ConfigLoader();

        // The startup snapshot feeds the logger (logging config is a one-off;
        // it does not change at runtime in v1).
        var config = loader.Load();

        // 2. Build the structured logger (console + file, config-honored).
        var loggerFactory = LoggingBootstrap.CreateLoggerFactory(config);

        // 3. Compose services: General infra + every domain library + UI.
        //    The same loader instance is registered as the IConfigLoader
        //    singleton before AddGeneral (which TryAdd-skips its own default).
        //    AddMods() is called explicitly (and idempotently again inside
        //    AddProfiles()) so the repository is discoverable at the root +
        //    IProfileService always resolves its staging dependency.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(loader);
        services.AddGeneral(loggerFactory);
        services.AddMods();
        services.AddProfiles();
        services.AddIntegrations();
        services.AddSteam();
        services.AddRelayClient();
        // The game-dir mods host (the ladder + takeover over <game>/mods).
        // Registered here rather than inside AddRelayClient because it wires
        // cross-library collaborators: the Profiles staging-link primitive
        // (junction/symlink per OS, registered by AddProfiles above) + the
        // app-state receipts role (AddGeneral). The launch service reads the
        // ladder; the shell performs the consented takeover.
        services.AddSingleton<IGameDirModsHost>(sp => new GameDirModsHost(
            sp.GetRequiredService<StagingLinkCreator>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IRenamedModsFoldersState>(),
            sp.GetRequiredService<ILogger<GameDirModsHost>>()));
        // The nxm scheme-handler plumbing: IPC server (single-instance via
        // process enumeration, pipe bind degrades gracefully on failure), router
        // + no-op handler defaults, and the platform OS registrar. The IPC
        // server is bound + started after the provider is built (see
        // StartNxmServer).
        services.AddNxm();

        // The serial Nexus download queue: one download at a time, FIFO, deduped
        // by (game domain, mod id, file id). Application-lifetime singleton
        // owning the per-item pipeline (dequeue-time auth re-check, repository
        // hit check, acquisition with progress, per-purpose completion) and the
        // observable item collection the download rows render from. Registered
        // after AddNxm() and before the INxmModDownloadHandler override below
        // (the handler takes it); like the handler, its factories resolve
        // dependencies lazily at first use, so registrations that appear later
        // in the collection (the Action<Action> marshal seam) are available by
        // the time anything resolves. The mod-list refresh is a Func so the
        // queue can be constructed BEFORE the list VM singleton it forwards to
        // (the list VM itself consumes the queue for its download rows; an
        // eager refresh dependency here would make that pair a construction
        // cycle). The Func resolves on the first completed download, long
        // after both singletons exist.
        services.AddSingleton<IModDownloadQueue>(sp => new ModDownloadQueue(
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IUpdateStateStore>(),
            sp.GetRequiredService<IConfigLoader>(),
            () => sp.GetRequiredService<IModListRefresh>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<ModDownloadQueue>>()));

        // The enqueue front for premium mod-update installs: resolves the head
        // release + admits an UpdateInstall item onto the queue above, so the
        // manual per-row update action and the automatic Premium batch share
        // one download engine with the nxm path (the queue's serial worker is
        // the only install gate). Registered after the queue; both callers
        // resolve it lazily through their own registrations below.
        services.AddSingleton(sp => new ModUpdateEnqueuer(
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IModDownloadQueue>(),
            sp.GetRequiredService<IProfileService>()));

        // The profile-scoped pending placement plans for load-order imports:
        // observes the queue's completion signal and converges each profile's
        // order as the enqueued downloads land (load-order policy stays out of
        // the queue's contract). Subscribes to the queue at construction;
        // registered after it, before the load-order workspace VM that records
        // plans and the mod-list VM that reloads on the applied event.
        services.AddSingleton(sp => new LoadOrderDownloadPlacements(
            sp.GetRequiredService<IModDownloadQueue>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<ILogger<LoadOrderDownloadPlacements>>()));

        // Replace the no-op INxmModDownloadHandler (registered inside AddNxm)
        // with the real enqueue adapter. MS DI resolves the LAST registration
        // for an interface, so this AddSingleton supersedes the no-op. The
        // handler gates each clicked nxm:// link (game domain, auth via live
        // config, active profile; failures keep the modal-alert path since
        // there is no row to host them), peeks the repository for a row name,
        // enqueues onto the queue above, and returns within milliseconds; the
        // queue owns the acquisition, the profile registration, the
        // acknowledge, and the reload. Registered with a factory that wires the
        // UI-thread marshaling seam (Dispatcher.UIThread.InvokeAsync)
        // explicitly for the gate alerts.
        services.AddSingleton<INxmModDownloadHandler>(sp => new NxmModDownloadHandler(
            invokeOnUi: action => Dispatcher.UIThread.InvokeAsync(action),
            sp.GetRequiredService<IModDownloadQueue>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<NxmModDownloadHandler>>()));

        // UI surface. MainWindow is a singleton: the desktop lifetime installs
        // the resolved instance as desktop.MainWindow, and DialogService resolves
        // the same one as the owner for modal dialogs. IProfileSession is the
        // single active-profile + running-state authority shared by the shell,
        // the Profiles destination, and the hosted page VMs (its polling timer
        // drives the live status).
        // LocalizationService + IPreferencesService are the i18n + preference
        // authorities (singletons so the whole app shares one culture + theme).
        services.AddSingleton<IProfileSession>(sp => new ProfileSession(
            sp.GetRequiredService<ISteamService>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileActivationState>(),
            StartRunningStatePolling));
        services.AddSingleton<LocalizationService>();
        // Whether the app runs inside a Steam Deck Gaming Mode session,
        // captured once here (the session cannot change without restarting
        // Curator). The theme application reads it: the System preference
        // resolves to Dark inside Gaming Mode, where no usable OS
        // color-scheme preference exists.
        services.AddSingleton<IGamingModeState>(new GamingModeState());
        services.AddSingleton<IPreferencesService, PreferencesService>();
        // MainWindow is a singleton resolved as desktop.MainWindow + the modal
        // dialog owner. Built through an explicit factory that supplies
        // IMainWindowStatePersistence via the internal production constructor
        // before the window is returned/shown; the public parameterless
        // constructor stays available for Avalonia's XAML runtime/designer
        // loader (AVLN3001 clean), and production construction never uses a
        // service locator.
        services.AddSingleton<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<IMainWindowStatePersistence>()));
        // The active profile's mod-list VM: a singleton (one list, the dominant
        // content area). Resolves IModImportService (via AddMods),
        // already registered above.
        // The UI-thread marshal seam for ModListViewModel's CheckCompleted handler
        // (the event fires on a threadpool thread; the handler iterates the
        // UI-bound Mods collection). Production wires Dispatcher.UIThread.Post.
        services.AddSingleton<Action<Action>>(_ => action => Dispatcher.UIThread.Post(action));
        // The narrow "reload the mod list" seam consumed by the nxm download
        // handler: a plain interface forward to the list VM singleton (resolved
        // lazily, so the registration introduces no construction-time cycle).
        services.AddSingleton<IModListRefresh>(sp => sp.GetRequiredService<ModListViewModel>());
        // The shared last-known OS nxm:// registration state. Its deliberate
        // probe points: one seed refresh at shell construction, one refresh on
        // each Nexus-enter, one after each register/release action. Every other
        // consumer surface (shell status strip, Mods empty-state hint, DMF
        // prompt wording) reads last-known and accepts staleness. Registered
        // before the VMs/services that inject it.
        services.AddSingleton<INxmRegistrationState>(sp => new NxmRegistrationState(
            sp.GetService<INxmHandlerRegistrar>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<NxmRegistrationState>>()));
        // The opt-in Premium automatic mod-update installer. Chained from the
        // update-check runner after each check; each flagged candidate is
        // enqueued as an UpdateInstall item onto the shared download queue
        // through ModUpdateEnqueuer (the queue's serial worker owns the
        // eligibility revalidation, the acquisition, the acknowledge, and the
        // UpdatesApplied reload signal the mod list consumes). Independent of
        // ModListViewModel (to avoid the ModListViewModel ->
        // UpdateCheckRunner dependency becoming circular).
        services.AddSingleton<IAutomaticUpdateService, AutomaticUpdateService>();

        // The mod-thumbnail disk/in-memory cache + download orchestrator. A UI-
        // layer singleton (decoded images are kept alive for the app lifetime so
        // multiple rows + reloads share them). Resolves IHttpClientFactory
        // (registered by AddIntegrations via AddHttpClient) for a plain factory-
        // created HttpClient per download; production decode uses
        // Bitmap.DecodeToWidth at ModThumbnailService.DecodeWidth px (sized for
        // the 192-DIP detailed-row thumbnail at 2x display scaling). Registered
        // before ModListViewModel and the later detailed-row coordinator so it is
        // available when they resolve.
        services.AddSingleton<IModThumbnailService>(sp => new ModThumbnailService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient,
            cacheDirOverride: null,
            decode: stream => Bitmap.DecodeToWidth(
                stream, ModThumbnailService.DecodeWidth, BitmapInterpolationMode.HighQuality),
            logger: sp.GetRequiredService<ILogger<ModThumbnailService>>()));

        // The Compact/Detailed density coordinator. An application-lifetime
        // singleton registered BEFORE ModListViewModel (which takes it as a
        // child). Owns the persisted density selection, metadata backfill, and
        // thumbnail hydration lifecycle.
        services.AddSingleton(sp => new DetailedModRowsViewModel(
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<INexusModMetadataService>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModThumbnailService>(),
            sp.GetRequiredService<ILogger<DetailedModRowsViewModel>>()));

        // The inline local-import workflow VM: an application-lifetime singleton
        // registered BEFORE ModListViewModel (which takes it as a child + listens
        // to its narrow ItemImported event). Owns the batch state machine, the
        // per-item editing form, and the per-item import orchestration. The view
        // hosts its card below the Mods toolbar; the mod-list Add split button +
        // drag-and-drop forward paths to its StartBatchCommand.
        // The shared hosted-card activity gate: the import workflow + the
        // load-order workspace report their activity to it (mutual exclusion +
        // the mod-list VM's any-card projections). Registered before both
        // card VMs.
        services.AddSingleton<ModCardsGate>();

        services.AddSingleton(sp => new ImportWorkflowViewModel(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModImportService>(),
            sp.GetRequiredService<ModCardsGate>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<ImportWorkflowViewModel>>()));

        // The load-order import workspace VM: an application-lifetime singleton
        // child registered before ModListViewModel (which takes it as a
        // child + reloads on its OrderApplied event). Owns the mode choice,
        // the review list, and the apply; the view's txt picker forwards to
        // its StartImport command. Shares the card gate with the import
        // workflow. The pending-placement component (registered above the
        // card VMs) records its download-order intent.
        services.AddSingleton(sp => new LoadOrderImportViewModel(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<ILoadOrderReconciler>(),
            sp.GetRequiredService<INexusClient>(),
            sp.GetRequiredService<IModImportService>(),
            sp.GetRequiredService<INexusAuthService>(),
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IModDownloadQueue>(),
            sp.GetRequiredService<LoadOrderDownloadPlacements>(),
            sp.GetRequiredService<ModCardsGate>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<LoadOrderImportViewModel>>()));

        // The link-external-folder child VM: an application-lifetime singleton
        // registered BEFORE ModListViewModel (which takes it as a child +
        // reloads the active list when its link flow finishes). Owns the
        // picker-driven peek / collision-check / LinkFolder / AddMod loop, its
        // failure alerts, and the linked row's open-external-folder badge
        // command; the view's picker + badge forward paths route to its
        // commands (the same forwarder shape the Add split button uses for the
        // import workflow).
        services.AddSingleton(sp => new LinkedModsViewModel(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModImportService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<IExternalLauncher>(),
            // Gaming Mode gates the linked-row open-folder badge (the picker
            // side is gated by the Add split button).
            sp.GetRequiredService<IGamingModeState>(),
            sp.GetRequiredService<ILogger<LinkedModsViewModel>>()));
        // The shared row context (premium / gaming): created once before the
        // mod-list VM, it reads the Nexus premium state at construction
        // (fire-and-forget). The mod-list VM passes the same instance to every
        // row; the rows read their global halves off it instead of receiving
        // per-flag pushes. Install-busy state is not here: an update in flight
        // is a queue item rendered as the row's download morph.
        services.AddSingleton(sp => new ModRowContext(
            sp.GetRequiredService<INexusAuthService>(),
            sp.GetRequiredService<IGamingModeState>(),
            sp.GetRequiredService<ILogger<ModRowContext>>()));

        services.AddSingleton(sp => new ModListViewModel(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<IUpdateStateStore>(),
            // The runner owns the refresh gate + surfaces the check completion
            // on the UI thread; the VM renders its state + hydrates rows from
            // it.
            sp.GetRequiredService<UpdateCheckRunner>(),
            sp.GetRequiredService<ModRowContext>(),
            sp.GetRequiredService<ImportWorkflowViewModel>(),
            sp.GetRequiredService<LoadOrderImportViewModel>(),
            sp.GetRequiredService<ModCardsGate>(),
            sp.GetRequiredService<DetailedModRowsViewModel>(),
            sp.GetRequiredService<LinkedModsViewModel>(),
            // The OS shell-open launcher: the Add NexusMods browser open +
            // the regular-tier update action's files-page open.
            sp.GetRequiredService<IExternalLauncher>(),
            // The shared last-known nxm registration state feeds the
            // empty-state Nexus hint; the mod list never probes the OS.
            sp.GetRequiredService<INxmRegistrationState>(),
            // The download queue feeds the mod list's download rows (the
            // in-place morphs + the appended section) + raises the
            // update-applied reload. Constructing the queue here is safe: its
            // refresh dependency is lazy.
            sp.GetRequiredService<IModDownloadQueue>(),
            // The premium update-action front: resolves the head release +
            // admits the UpdateInstall item onto the queue above.
            sp.GetRequiredService<ModUpdateEnqueuer>(),
            // The load-order pending placements: a completed download's
            // order convergence reloads this list.
            sp.GetRequiredService<LoadOrderDownloadPlacements>(),
            sp.GetRequiredService<ILogger<ModListViewModel>>()));

        // The hosted destination view models: singletons (one instance per page,
        // kept alive + subscribed for the application lifetime). Each page VM is
        // injected into ShellViewModel and bound in MainWindow to its hosted
        // UserControl. Registered with the same production dependencies the
        // DialogService previously wired for each Window-constructed VM
        // (UI-thread marshal, typed logger, platform-resolved seams).
        //
        // ProfilesViewModel is narrowly coupled to profile workflow: after a
        // successful create-and-activate it does no DMF or mod-list work. The
        // DMF (Darktide Mod Framework) install-prompt coordinator
        // (DmfPromptService, registered below) subscribes to the synchronous
        // IProfileService.ProfileCreated event at construction; resolving
        // ShellViewModel at startup establishes the subscription before any
        // profile can be created. When ProfilesViewModel.Save calls
        // CreateProfile, the event fires synchronously into the already-
        // subscribed coordinator, which enqueues its prompt onto the
        // shell-owned modal queue; the shell drains the queue after the next
        // real navigation into Mods, and the coordinator's own post-prompt
        // reload surfaces an accepted existing-DMF add (a premium download
        // lands on the download queue, whose completion owns its add +
        // reload).
        services.AddSingleton<ProfilesViewModel>(sp => new ProfilesViewModel(
            sp.GetRequiredService<IProfileService>(),
            // The focused clone capability; AddProfiles maps it to the same
            // ProfileService singleton as IProfileService.
            sp.GetRequiredService<IProfileCloner>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<ProfilesViewModel>>()));
        services.AddSingleton(sp => new IntegrationsViewModel(
            sp.GetRequiredService<INexusAuthService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<IDialogService>(),
            // The registrar performs the register/release mutations (null on
            // platforms without one); the shared state carries the status.
            sp.GetService<INxmHandlerRegistrar>(),
            sp.GetRequiredService<INxmRegistrationState>(),
            sp.GetRequiredService<IExternalLauncher>(),
            sp.GetRequiredService<ILogger<IntegrationsViewModel>>()));
        services.AddSingleton(sp => new PreferencesViewModel(
            sp.GetRequiredService<IPreferencesService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<LocalizationService>(),
            OperatingSystem.IsWindows()));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<ISteamService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<IAppUpdateService>(),
            sp.GetRequiredService<IDialogService>(),
            // Gaming Mode gates the discovery Browse buttons + the Storage
            // open-folder buttons; manual path entry stays available.
            sp.GetRequiredService<IGamingModeState>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<SettingsViewModel>>(),
            sp.GetRequiredService<IExternalLauncher>()));

        // The shell-owned modal queue: services enqueue deferred modals for a
        // destination; the shell drains them in its navigation lifecycle after
        // the destination switch + enter effects. Registered before every
        // enqueuer (the DMF coordinator + the shell itself).
        services.AddSingleton<IShellModalQueue, ShellModalQueue>();

        // The DMF (Darktide Mod Framework) install-prompt coordinator.
        // Subscribes to the synchronous IProfileService.ProfileCreated event at
        // construction; when ProfilesViewModel.Save later calls CreateProfile
        // (firing ProfileCreated), the already-subscribed coordinator enqueues
        // its prompt onto the shell-owned modal queue for the next real
        // navigation into Mods, where the shell drains it after the
        // destination switch + enter effects: the DMF prompt runs as the
        // topmost modal with Mods already selected underneath, and the
        // coordinator's own post-prompt reload surfaces an accepted
        // existing-DMF add. Takes the shared nxm registration state so
        // the download confirm can tailor its message to the last-known handler
        // ownership (manager-download vs. manual-import guidance; no probe),
        // and the Gaming Mode state so the case-2 browser branch can divert to
        // Desktop Mode guidance there (Premium keeps the in-app download path,
        // now the shared download queue: the premium branch resolves the head
        // file + enqueues, and the queue's completion owns the add + reload).
        // Nothing depends on the coordinator (the shell no longer knows it
        // exists), so it is resolved once eagerly after the provider is built
        // to establish the subscription before any profile can be created.
        services.AddSingleton(sp => new DmfPromptService(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IModDownloadQueue>(),
            sp.GetRequiredService<INexusAuthService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<DmfPromptService>>(),
            sp.GetRequiredService<INxmRegistrationState>(),
            sp.GetRequiredService<IGamingModeState>(),
            sp.GetRequiredService<IExternalLauncher>(),
            sp.GetRequiredService<IShellModalQueue>(),
            sp.GetRequiredService<IModListRefresh>()));

        // ShellViewModel owns navigation + drains the modal queue on
        // destination entry. The shell has no knowledge of which services
        // enqueue (that is the queue's point); its nxm status strip reads the
        // shared registration state (seeded by the one startup probe inside
        // its constructor).
        services.AddSingleton(sp => new ShellViewModel(
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IRelayLaunchService>(),
            sp.GetRequiredService<IGameDirModsHost>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ProfilesViewModel>(),
            sp.GetRequiredService<ModListViewModel>(),
            sp.GetRequiredService<IntegrationsViewModel>(),
            sp.GetRequiredService<PreferencesViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<IAppUpdateService>(),
            sp.GetRequiredService<IShellModalQueue>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<ShellViewModel>>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<INxmRegistrationState>()));
        // The narrow factory for the one dialog VM with service dependencies
        // (the discovery escape hatch), so DialogService shows dialogs without
        // constructing view models or carrying their dependencies.
        services.AddSingleton<IDiscoveryEscapeHatchFactory>(sp => new DiscoveryEscapeHatchFactory(
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<ISteamService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<IGamingModeState>()));
        services.AddSingleton<IDialogService>(sp =>
            new DialogService(
                sp.GetRequiredService<MainWindow>(),
                sp.GetRequiredService<LocalizationService>(),
                sp.GetRequiredService<IDiscoveryEscapeHatchFactory>()));

        // The UI-layer glue that fires an update check
        // (IUpdateCheckService, registered above via AddIntegrations) on the
        // automatic triggers: startup (when a profile is restored),
        // active-profile switch, and a periodic timer. Owns the candidate
        // pull: each fire reads the profile's mod list through
        // IProfileService + maps the entries to ModListCandidates at the call
        // site (so Integrations holds no Profiles reference); a pull failure
        // (a deleted profile) is logged + skipped. All three triggers share
        // one shared interval gate (read live from config) so a rapid
        // open/close loop or rapid profile switching cannot burn API calls;
        // the gate's last-check timestamp is persisted via the app-state
        // so it survives a close/reopen. The toggle gates only the periodic
        // timer. Subscribes to IProfileSession.PropertyChanged for switches
        // + fires the opening check for the restored active id. Started after
        // the provider is built (see StartUpdateCheck); best-effort, never
        // blocks startup. Singleton: owns the session subscription for the
        // app lifetime. The periodic timer is wired to a DispatcherTimer (the
        // established ProfileSession pattern); the runner takes the timer-start
        // delegate as a seam so it stays unit-testable.
        // The manual-refresh countdown timer seams (the throttle's live m:ss
        // tooltip + the rate-limit pill's clearing), owned by the runner's
        // UpdateRefreshGate. Production manages a single 1-second
        // DispatcherTimer, created lazily on first start, with Tick wired once;
        // Start/Stop control whether it runs (mirrors
        // StartUpdateCheckPolling's established timer pattern). The start
        // delegate is idempotent: a second start while the timer is running is
        // a no-op (DispatcherTimer.Start is safe to re-call, and the Tick
        // handler is wired exactly once). Composition happens on the UI thread
        // during app startup, so the DispatcherTimer affinity is correct. The
        // gate's StateChanged marshals through the shared Action<Action> seam.
        services.AddSingleton(sp =>
        {
            DispatcherTimer? countdownTimer = null;
            Action<Action> startCountdownTimer = tick =>
            {
                if (countdownTimer is null)
                {
                    countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    countdownTimer.Tick += (_, _) => tick();
                }
                countdownTimer.Start();
            };
            Action stopCountdownTimer = () => countdownTimer?.Stop();
            return new UpdateCheckRunner(
                sp.GetRequiredService<IProfileSession>(),
                sp.GetRequiredService<IProfileService>(),
                sp.GetRequiredService<IUpdateCheckService>(),
                sp.GetRequiredService<IConfigLoader>(),
                sp.GetRequiredService<IUpdateCheckScheduleState>(),
                sp.GetRequiredService<IAutomaticUpdateService>(),
                sp.GetRequiredService<ILogger<UpdateCheckRunner>>(),
                StartUpdateCheckPolling,
                invokeOnUi: sp.GetRequiredService<Action<Action>>(),
                startCountdownTimer: startCountdownTimer,
                stopCountdownTimer: stopCountdownTimer);
        });

        // The Curator self-update service (Velopack). Conditional on
        // CURATOR_VELOPACK: a Velopack-packaged build (Windows install or Linux
        // AppImage) gets the real VelopackAppUpdateService; everything else
        // (standalone Linux, dev builds without CuratorUseVelopack=true) gets
        // the no-op implementation that reports IsUpdateSupported=false.
        // Consumers talk to IAppUpdateService unconditionally and gate their
        // affordances on IsUpdateSupported. The Velopack impl resolves
        // IConfigLoader so it can read CuratorConfig.AppUpdates.SourceOverride
        // once at construction: null (the default) builds the production
        // anonymous GithubSource; a set value (a local directory path or URL)
        // builds the manager from UpdateManager's urlOrPath overload, the
        // local-testing / self-hosted feed path with no code change.
#if CURATOR_VELOPACK
        services.AddSingleton<IAppUpdateService>(sp => new VelopackAppUpdateService(
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<ILogger<VelopackAppUpdateService>>()));
#else
        services.AddSingleton<IAppUpdateService, NoopAppUpdateService>();
#endif

        // The UI-layer glue that fires one self-update availability check on
        // startup (fire-and-forget, profile-independent: app updates do not
        // depend on the active profile or the Nexus auth). The result lands
        // through IAppUpdateService.UpdateStateChanged; the runner itself
        // surfaces nothing. The startup check is gated on
        // CuratorConfig.AppUpdates.CheckOnStartup (read live on startup); the
        // manual "Check for Updates" button in Settings always works and calls
        // IAppUpdateService.CheckForUpdatesAsync directly. Started after the
        // provider is built (see StartAppUpdateCheck); best-effort, never
        // blocks startup. Singleton: owns the single startup fire for the app
        // lifetime.
        services.AddSingleton(sp => new AppUpdateCheckRunner(
            sp.GetRequiredService<IAppUpdateService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<ILogger<AppUpdateCheckRunner>>()));

        // The first-run Welcome onboarding coordinator. Shows the Welcome modal
        // once, the first time the app starts with onboarding not yet complete,
        // persists completion, and on a "Set up Nexus" choice navigates the shell
        // to Nexus through IShellNavigation (a plain forward to the shell
        // singleton below, resolved lazily so the registration introduces no
        // construction-time cycle; the leave-Integrations nxm/mod-list refresh
        // applies after the Welcome-driven visit too). Singleton: owns the
        // in-process "already shown" guard. Started from App after the main
        // window opens; best-effort, never blocks startup.
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton(sp => new OnboardingService(
            sp.GetRequiredService<IOnboardingState>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IShellNavigation>(),
            sp.GetRequiredService<ILogger<OnboardingService>>()));

        var provider = services.BuildServiceProvider();

        // Startup prune: drop repository versions no profile references + empty
        // containers (spec §5). Best-effort: a failure is logged + swallowed so
        // cleanup never blocks startup (the repository is still usable, and the
        // next startup retries).
        RunStartupPrune(provider, loggerFactory);

        // Startup discovery: an ordinary ISteamService.Discover() so automatic
        // mode (the default) runs the platform discoverer and replaces the
        // active-platform snapshot, which is what the Settings rows display.
        // Non-blocking: a missing-fields result is logged as a warning + the
        // user can still use the app (browse mods, manage profiles); they just
        // cannot launch until resolved (the launch-time Discover re-runs and
        // surfaces the escape-hatch when incomplete).
        RunStartupDiscovery(provider, loggerFactory);

        // Start the nxm IPC server. Bind runs two checks: (1) single-instance via
        // process enumeration, which throws NxmSingleInstanceException if another
        // Curator is running (propagates out of Build() so the caller, App, shuts
        // down before the window shows); and (2) the pipe bind, which degrades
        // gracefully on IOException (the app continues without the IPC server).
        // Intentionally NOT wrapped in a try/catch (unlike the best-effort prune
        // + discovery above): a single-instance violation is fatal-by-design for
        // this process. The pipe-bind degradation is handled inside Bind itself.
        StartNxmServer(provider, loggerFactory.CreateLogger(nameof(CuratorComposition)));

        // Maintain the nxm handler registration AFTER StartNxmServer returns.
        // StartNxmServer returns both on a successful pipe bind AND on a degraded
        // bind (IOException swallowed inside Bind), so maintenance runs in either
        // non-fatal case. A NxmSingleInstanceException propagates out of
        // StartNxmServer (and out of Build) without reaching this call, so a
        // single-instance violation never triggers maintenance. Best-effort:
        // failures are logged + swallowed; the call is synchronous (its
        // sanitized xdg-mime child can take time on Linux; a hung desktop
        // helper hangs here rather than being masked, deliberately).
        // On Linux AppImage runs this refreshes the durable handler copy + the
        // AppImage symlink; everywhere else it is a no-op.
        MaintainNxmRegistration(provider, loggerFactory);

        // Resolve the DMF prompt coordinator once so its ProfileCreated
        // subscription exists before the window shows (nothing else depends on
        // it; the shell reaches its prompt only through the modal queue).
        // Best-effort: a failure is logged + swallowed (the DMF prompt simply
        // never fires this session).
        ResolveDmfPromptService(provider, loggerFactory);

        // Start the update-check runner so a check fires on profile load
        // (startup with the restored id + active-profile switches).
        // Best-effort: a failure is logged + swallowed so a wiring problem never
        // blocks app startup (the mod-list update badges just stay blank until restart).
        StartUpdateCheck(provider, loggerFactory);

        // Start the app self-update runner so an availability check fires once
        // on startup. Best-effort: a failure is logged + swallowed so a wiring
        // problem never blocks startup (the user sees nothing; the self-update
        // notice simply never appears).
        StartAppUpdateCheck(provider, loggerFactory);

        return provider;
    }

    /// <summary>
    /// Resolves the <see cref="DmfPromptService"/> singleton once so its
    /// <c>ProfileCreated</c> subscription exists before the window shows (the
    /// shell no longer injects it; the service is reached only through the
    /// modal queue it feeds). Best-effort: a failure is logged + swallowed so
    /// a wiring problem never blocks startup (the DMF prompt simply never
    /// fires this session).
    /// </summary>
    private static void ResolveDmfPromptService(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            _ = provider.GetRequiredService<DmfPromptService>();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Failed to resolve the DMF prompt service (best-effort).");
        }
    }

    /// <summary>
    /// Runs <see cref="ModCleanup.PruneUnreferenced"/> once after composition.
    /// Best-effort: any failure is logged + swallowed so a cleanup failure never
    /// blocks app startup (the repository is still usable; the next startup
    /// retries).
    /// </summary>
    private static void RunStartupPrune(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            var logger = loggerFactory.CreateLogger(nameof(CuratorComposition));
            var profiles = provider.GetRequiredService<IProfileService>();
            var repo = provider.GetRequiredService<IModRepository>();
            ModCleanup.PruneUnreferenced(profiles, repo);
            logger.LogInformation("Startup mod prune complete.");
        }
        catch (Exception ex)
        {
            // Swallow: cleanup is best-effort. Log + continue.
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Startup mod prune failed (best-effort; will retry next startup).");
        }
    }

    /// <summary>
    /// Runs <see cref="ISteamService.Discover"/> once after composition so
    /// automatic mode (the default) runs the platform discoverer up front and
    /// the persisted snapshot reflects the current install (the Settings rows
    /// display that snapshot). Best-effort + non-blocking: any failure is
    /// logged + swallowed so a discovery problem never blocks app startup. A
    /// missing-fields result is logged as a warning so the operator knows they
    /// cannot launch yet; the launch-time Discover re-runs and surfaces the
    /// escape-hatch when incomplete.
    /// </summary>
    private static void RunStartupDiscovery(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            var logger = loggerFactory.CreateLogger(nameof(CuratorComposition));
            var steam = provider.GetRequiredService<ISteamService>();
            var result = steam.Discover();
            if (result.Status == DiscoveryStatus.Complete)
            {
                logger.LogInformation("Startup discovery complete.");
            }
            else
            {
                // Non-blocking: the user can still use the app; they just cannot
                // launch until the missing fields are resolved (the launch-time
                // Discover re-runs discovery and surfaces the escape-hatch when
                // still incomplete).
                logger.LogWarning(
                    "Startup discovery is {Status}: missing fields will block launch until resolved " +
                    "(steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
                    result.Status,
                    result.SteamInstallPath ?? "(missing)",
                    result.DarktideGameBinaryPath ?? "(missing)",
                    result.CompatdataPath ?? "(missing)",
                    result.ProtonBinaryPath ?? "(missing)");
            }
        }
        catch (Exception ex)
        {
            // Swallow: discovery is best-effort at startup. Log + continue; the
            // launch-time Discover re-runs and surfaces real failures.
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Startup discovery failed (best-effort; launch will re-try).");
        }
    }

    /// <summary>
    /// Resolves the <see cref="UpdateCheckRunner"/> + calls
    /// <see cref="UpdateCheckRunner.Start"/> so an update check fires on profile
    /// load (startup with the restored active id, then every active-profile
    /// switch). Best-effort: any failure is logged + swallowed so a wiring
    /// problem never blocks app startup (the user can still use the app; the
    /// mod-list update badges just stay blank until restart).
    /// </summary>
    private static void StartUpdateCheck(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            provider.GetRequiredService<UpdateCheckRunner>().Start();
        }
        catch (Exception ex)
        {
            // Swallow: update-check wiring is best-effort. Log + continue; the
            // app works without it (the mod-list update badges just stay blank).
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Failed to start the update-check runner (best-effort).");
        }
    }

    /// <summary>
    /// Resolves the <see cref="AppUpdateCheckRunner"/> + calls
    /// <see cref="AppUpdateCheckRunner.Start"/> so a Curator self-update
    /// availability check fires once on startup. Best-effort: any failure is
    /// logged + swallowed so a wiring problem never blocks app startup (the user
    /// sees nothing; the self-update notice simply never appears).
    /// </summary>
    private static void StartAppUpdateCheck(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            provider.GetRequiredService<AppUpdateCheckRunner>().Start();
        }
        catch (Exception ex)
        {
            // Swallow: self-update-check wiring is best-effort. Log + continue;
            // the app works without it (the self-update notice just never shows).
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Failed to start the app self-update runner (best-effort).");
        }
    }

    /// <summary>
    /// The live running-state poll: a <see cref="DispatcherTimer"/> that pings
    /// <see cref="ISteamService.IsGameRunning"/> every few seconds so the status
    /// strip + launch-availability + dropdown-enable react to the game starting or
    /// stopping while Curator is open. Runs on the UI thread (composition happens
    /// during app startup, also on the UI thread).
    /// </summary>
    private static void StartRunningStatePolling(Action onTick)
    {
        var timer = new DispatcherTimer
        {
            Interval = ProfileSession.PollInterval,
        };
        timer.Tick += (_, _) => onTick();
        timer.Start();
    }

    /// <summary>
    /// The periodic update-check poll: a <see cref="DispatcherTimer"/> that ticks
    /// at <see cref="UpdateCheckRunner.TickInterval"/> (1 minute) so the runner
    /// can fire a check when the user-configured interval (read live from config)
    /// has elapsed. The runner owns the interval math + the toggle gate; this
    /// just drives the tick on the UI thread (mirrors
    /// <see cref="StartRunningStatePolling"/>). Composition happens on the UI
    /// thread during app startup.
    /// </summary>
    private static void StartUpdateCheckPolling(Action onTick)
    {
        var timer = new DispatcherTimer
        {
            Interval = UpdateCheckRunner.TickInterval,
        };
        timer.Tick += (_, _) => onTick();
        timer.Start();
    }

    /// <summary>
    /// Binds + starts the nxm IPC server. <see cref="NxmIpcServer.Bind"/> runs
    /// two separate checks: (1) single-instance via process enumeration, which
    /// throws <see cref="NxmSingleInstanceException"/> if another Curator process
    /// is running (this method rethrows it so the caller,
    /// <c>App.OnFrameworkInitializationCompleted</c>, can shut down before the
    /// main window shows); and (2) the IPC pipe bind, which is its own check
    /// that degrades gracefully on <see cref="IOException"/> (a real pipe
    /// problem, not another instance). On a successful bind the accept loop is
    /// kicked off on a background task (fire-and-forget; process exit reclaims
    /// the pipe). On a degraded bind, the loop is skipped and the app continues
    /// without the IPC server (nxm click-to-download won't work this session;
    /// everything else is unaffected).
    /// </summary>
    /// <remarks>
    /// Called from <see cref="Build"/> after the provider is built. Throwing
    /// (rather than returning a flag) on the single-instance violation keeps
    /// <see cref="Build"/>'s signature unchanged and makes the violation an
    /// explicit, unmissable signal at the call site. The composition root never
    /// catches <see cref="NxmSingleInstanceException"/>, so it propagates to the
    /// App. The pipe-bind degradation, by contrast, is non-fatal: the warning is
    /// logged inside <see cref="NxmIpcServer.Bind"/> and this method simply skips
    /// the accept loop when <see cref="NxmIpcServer.IsBound"/> is false.
    /// </remarks>
    private static void StartNxmServer(IServiceProvider provider, ILogger logger)
    {
        var server = provider.GetRequiredService<NxmIpcServer>();

        // Bind runs Check 1 (process enumeration -> NxmSingleInstanceException on
        // collision, fatal) and Check 2 (pipe ctor -> IOException degrades to a
        // not-bound server with a warning logged, non-fatal).
        server.Bind();

        if (!server.IsBound)
        {
            // Degraded: Bind already logged the detailed warning (with the
            // IOException). Skip the accept loop; the app continues without nxm
            // IPC. Everything else (window, profiles, mods, launch) is unaffected.
            logger.LogWarning(
                "nxm IPC server is not running; nxm click-to-download from Nexus is unavailable this session.");
            return;
        }

        // Kick off the accept loop. The cancellation token is captured for a
        // future graceful-shutdown hook; for v1, process exit reclaims the pipe.
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The nxm IPC server accept loop exited unexpectedly.");
            }
        });

        logger.LogInformation("nxm IPC server accept loop started.");
    }

    /// <summary>
    /// Calls <see cref="INxmHandlerRegistrar.MaintainRegistration"/> once after
    /// <see cref="StartNxmServer"/> has returned, so the fatal process-
    /// enumeration single-instance check has already succeeded. Best-effort: any
    /// failure is logged + swallowed (non-fatal). The call is synchronous: on
    /// Linux it may spawn the registrar's sanitized <c>xdg-mime</c> child and
    /// wait for it, so it can take time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why after StartNxmServer.</b> <see cref="StartNxmServer"/> calls
    /// <see cref="NxmIpcServer.Bind"/>, which runs the single-instance guard
    /// first (throws <see cref="NxmSingleInstanceException"/> on collision,
    /// propagating out of <see cref="Build"/> before this method is reached) and
    /// the pipe bind second (degrades gracefully on <see cref="IOException"/>).
    /// So when this method runs, either the pipe bound or it degraded; both are
    /// non-fatal and maintenance is safe in either case. A single-instance
    /// violation never reaches here.</para>
    /// <para>
    /// <b>Why GetService (optional).</b> On unsupported platforms (not Windows
    /// or Linux) no registrar is registered; maintenance is silently skipped
    /// there. The registrar's own <see cref="INxmHandlerRegistrar.MaintainRegistration"/>
    /// is a no-op when there is nothing to maintain (Windows, standalone
    /// Linux).</para>
    /// </remarks>
    private static void MaintainNxmRegistration(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            // Optional: null on platforms with no registrar (not Windows or
            // Linux). The registrar's own method is a no-op when there is
            // nothing to maintain, so this call is safe unconditionally.
            provider.GetService<INxmHandlerRegistrar>()?.MaintainRegistration();
        }
        catch (Exception ex)
        {
            // Swallow: maintenance is best-effort. Log + continue; the app works
            // without it (the next startup retries).
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "nxm handler registration maintenance failed (best-effort).");
        }
    }
}
