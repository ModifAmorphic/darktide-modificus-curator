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
using Modificus.Curator.Launcher;
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
        services.AddLauncher();
        // The nxm scheme-handler plumbing: IPC server (single-instance via
        // process enumeration, pipe bind degrades gracefully on failure), router
        // + no-op handler defaults, and the platform OS registrar. The IPC
        // server is bound + started after the provider is built (see
        // StartNxmServer).
        services.AddNxm();

        // Replace the no-op INxmModDownloadHandler (registered inside AddNxm)
        // with the real acquisition handler. MS DI resolves the LAST registration
        // for an interface, so this AddSingleton supersedes the no-op. Registered
        // with a factory that resolves its dependencies lazily at first use (the
        // factory delegate is deferred until the handler is first resolved by the
        // IPC router, by which point all dependencies including IProfileSession,
        // IDialogService, and MainWindow are registered). It coordinates the
        // acquisition service (Integrations) with the active-profile session,
        // profile service, and the UI-thread alert dialog. Registered with a
        // factory so the UI-thread marshaling seam
        // (Dispatcher.UIThread.InvokeAsync) is wired explicitly.
        services.AddSingleton<INxmModDownloadHandler>(sp => new NxmModDownloadHandler(
            invokeOnUi: action => Dispatcher.UIThread.InvokeAsync(action),
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<NxmModDownloadHandler>>(),
            refreshModList: containerId => sp.GetRequiredService<ModListViewModel>().AcknowledgeUpdateAndReload(containerId)));

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
            sp.GetRequiredService<IAppStateStore>(),
            StartRunningStatePolling));
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        // MainWindow is a singleton resolved as desktop.MainWindow + the modal
        // dialog owner. Built through an explicit factory that supplies
        // IAppStateStore via the internal production constructor before the
        // window is returned/shown; the public parameterless constructor stays
        // available for Avalonia's XAML runtime/designer loader (AVLN3001
        // clean), and production construction never uses a service locator.
        services.AddSingleton<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<IAppStateStore>()));
        // The active profile's mod-list VM: a singleton (one list, the dominant
        // content area). Resolves IModImportService (via AddMods) +
        // IModOrderResolver (via AddProfiles), both already registered above.
        // The UI-thread marshal seam for ModListViewModel's CheckCompleted handler
        // (the event fires on a threadpool thread; the handler iterates the
        // UI-bound Mods collection). Production wires Dispatcher.UIThread.Post.
        services.AddSingleton<Action<Action>>(_ => action => Dispatcher.UIThread.Post(action));
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
        // The global install coordinator: shared between the manual per-row update
        // action and the automatic Premium updater so only one install runs at a
        // time. Singleton: holds the single-slot semaphore for the app lifetime.
        services.AddSingleton<UpdateCoordinator>();
        // The opt-in Premium automatic mod-update installer. Chained from the
        // update-check runner after each check; independent of ModListViewModel
        // (to avoid the ModListViewModel -> UpdateCheckRunner dependency becoming
        // circular) but raises UpdatesApplied so the list VM reloads after a
        // batch.
        services.AddSingleton<IAutomaticUpdateService, AutomaticUpdateService>();

        // The mod-thumbnail disk/in-memory cache + download orchestrator. A UI-
        // layer singleton (decoded images are kept alive for the app lifetime so
        // multiple rows + reloads share them). Resolves IHttpClientFactory
        // (registered by AddIntegrations via AddHttpClient) for a plain factory-
        // created HttpClient per download; production decode uses
        // Bitmap.DecodeToWidth at ModThumbnailService.DecodeWidth px (sized for
        // the 112-DIP detailed-row thumbnail on scaled displays). Registered
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
        services.AddSingleton(sp => new ImportWorkflowViewModel(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModImportService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<ImportWorkflowViewModel>>()));
        // The manual-refresh countdown timer seams (the throttle's live m:ss
        // tooltip). Production manages a single 1-second DispatcherTimer, created
        // lazily on first start, with Tick wired once; Start/Stop control whether
        // it runs (mirrors StartUpdateCheckPolling's established timer pattern).
        // The start delegate is idempotent: a second start while the timer is
        // running is a no-op (DispatcherTimer.Start is safe to re-call, and the
        // Tick handler is wired exactly once). Composition happens on the UI
        // thread during app startup, so the DispatcherTimer affinity is correct.
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
            return new ModListViewModel(
                sp.GetRequiredService<IProfileService>(),
                sp.GetRequiredService<IProfileSession>(),
                sp.GetRequiredService<IModRepository>(),
                sp.GetRequiredService<IModImportService>(),
                sp.GetRequiredService<IModOrderResolver>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<LocalizationService>(),
                sp.GetRequiredService<IUpdateCheckService>(),
                sp.GetRequiredService<IModAcquisitionService>(),
                sp.GetRequiredService<INexusAuthService>(),
                sp.GetRequiredService<IUpdateStateStore>(),
                sp.GetRequiredService<UpdateCheckRunner>(),
                sp.GetRequiredService<UpdateCoordinator>(),
                sp.GetRequiredService<IAutomaticUpdateService>(),
                sp.GetRequiredService<ImportWorkflowViewModel>(),
                sp.GetRequiredService<DetailedModRowsViewModel>(),
                sp.GetRequiredService<Action<Action>>(),
                sp.GetRequiredService<ILogger<ModListViewModel>>(),
                // The shared last-known nxm registration state feeds the
                // empty-state Nexus hint; the mod list never probes the OS.
                sp.GetRequiredService<INxmRegistrationState>(),
                startCountdownTimer,
                stopCountdownTimer);
        });

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
        // subscribed coordinator, which records it as pending; the shell
        // consumes the pending trigger on the next real navigation into Mods
        // (ProcessPendingAsync after CurrentDestination = Mods), then reloads
        // the mod list when a trigger was consumed so an accepted existing /
        // Premium DMF add is visible.
        services.AddSingleton<ProfilesViewModel>(sp => new ProfilesViewModel(
            sp.GetRequiredService<IProfileService>(),
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
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<SettingsViewModel>>()));

        // The DMF (Darktide Mod Framework) install-prompt coordinator.
        // Subscribes to the synchronous IProfileService.ProfileCreated event at
        // construction. When ProfilesViewModel.Save later calls CreateProfile
        // (firing ProfileCreated), the already-subscribed coordinator records it
        // as pending; the shell consumes the trigger on the next real navigation
        // into Mods (ProcessPendingAsync after CurrentDestination = Mods), so
        // the DMF prompt runs as the topmost modal with Mods already selected
        // underneath + the post-prompt mod-list reload surfaces an accepted
        // existing/Premium DMF add. Singleton: owns the event subscription for
        // the app lifetime. Takes the shared nxm registration state so the
        // download confirm can tailor its message to the last-known handler
        // ownership (manager-download vs. manual-import guidance; no probe).
        // Registered BEFORE ShellViewModel so ShellViewModel's factory can
        // resolve it eagerly.
        services.AddSingleton(sp => new DmfPromptService(
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IModRepository>(),
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<INexusAuthService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ILogger<DmfPromptService>>(),
            sp.GetRequiredService<INxmRegistrationState>()));

        // ShellViewModel owns navigation + the deferred DMF trigger (consumed on
        // a real Mods entry). The concrete DmfPromptService is injected (not a
        // delegate or navigation interface) so the shell can call
        // ProcessPendingAsync at its chosen point without coupling the
        // coordinator to navigation sequencing. The shell's nxm status strip
        // reads the shared registration state (seeded by the one startup probe
        // inside its constructor).
        services.AddSingleton(sp => new ShellViewModel(
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IRelayLaunchService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<ProfilesViewModel>(),
            sp.GetRequiredService<ModListViewModel>(),
            sp.GetRequiredService<IntegrationsViewModel>(),
            sp.GetRequiredService<PreferencesViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<IAppUpdateService>(),
            sp.GetRequiredService<DmfPromptService>(),
            sp.GetRequiredService<Action<Action>>(),
            sp.GetRequiredService<ILogger<ShellViewModel>>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<INxmRegistrationState>()));
        services.AddSingleton<IDialogService>(sp =>
            new DialogService(
                sp.GetRequiredService<MainWindow>(),
                sp.GetRequiredService<LocalizationService>(),
                sp.GetRequiredService<IConfigLoader>(),
                sp.GetRequiredService<ISteamService>()));

        // The UI-layer glue that fires an update check
        // (IUpdateCheckService, registered above via AddIntegrations) on the
        // automatic triggers: startup (when a profile is restored),
        // active-profile switch, and a periodic timer. All three share one
        // shared interval gate (read live from config) so a rapid
        // open/close loop or rapid profile switching cannot burn API calls;
        // the gate's last-check timestamp is persisted via IAppStateStore so
        // it survives a close/reopen. The toggle gates only the periodic
        // timer. Subscribes to IProfileSession.PropertyChanged for switches
        // + fires the opening check for the restored active id. Started after
        // the provider is built (see StartUpdateCheck); best-effort, never
        // blocks startup. Singleton: owns the session subscription for the
        // app lifetime. The periodic timer is wired to a DispatcherTimer (the
        // established ProfileSession pattern); the runner takes the timer-start
        // delegate as a seam so it stays unit-testable.
        services.AddSingleton(sp => new UpdateCheckRunner(
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IUpdateCheckService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<IAppStateStore>(),
            sp.GetRequiredService<IAutomaticUpdateService>(),
            sp.GetRequiredService<ILogger<UpdateCheckRunner>>(),
            StartUpdateCheckPolling));

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
        // to Nexus (resolved lazily through ShellViewModel's
        // navigation entry point, so the leave-Integrations nxm/mod-list refresh
        // applies after the Welcome-driven visit too). Singleton: owns the
        // in-process "already shown" guard. Started from App after the main
        // window opens; best-effort, never blocks startup.
        services.AddSingleton(sp => new OnboardingService(
            sp.GetRequiredService<IAppStateStore>(),
            sp.GetRequiredService<IDialogService>(),
            () => sp.GetRequiredService<ShellViewModel>().NavigateToIntegrationsAsync(),
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

        // Start the update-check runner so a check fires on profile load
        // (startup with the restored id + active-profile switches).
        // Best-effort: a failure is logged + swallowed so a wiring problem never
        // blocks startup (the mod-list update badges just stay blank until restart).
        StartUpdateCheck(provider, loggerFactory);

        // Start the app self-update runner so an availability check fires once
        // on startup. Best-effort: a failure is logged + swallowed so a wiring
        // problem never blocks startup (the user sees nothing; the self-update
        // notice simply never appears).
        StartAppUpdateCheck(provider, loggerFactory);

        return provider;
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
