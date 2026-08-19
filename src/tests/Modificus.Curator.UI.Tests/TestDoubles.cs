using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.RelayClient;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Hand-rolled test doubles for the shell/manage/mod-list VMs' dependencies. No
/// mock library is used anywhere in the repo; these recording fakes match that
/// style and keep the test project dependency-free.
/// </summary>
internal static class TestDoubles
{
    public static FakeProfileService Profiles(params ProfileSummary[] seed) => new(seed);

    /// <summary>
    /// Builds a <see cref="ModRowContext"/> over the supplied (or default)
    /// fakes with a pass-through UI-thread seam: the premium read resolves
    /// synchronously (the fake auth returns completed tasks), the installer's
    /// busy + progress events flow through immediately, and the gaming flag is
    /// readable off the context the moment it is built.
    /// </summary>
    public static ModRowContext RowContext(
        FakeNexusAuthService? auth = null,
        FakeModUpdateInstaller? installer = null,
        IGamingModeState? gamingMode = null)
    {
        return new ModRowContext(
            auth ?? new FakeNexusAuthService(),
            installer ?? new FakeModUpdateInstaller(),
            gamingMode ?? new GamingModeState(false),
            static action => action(),
            NullLogger<ModRowContext>.Instance);
    }

    /// <summary>
    /// Builds a <see cref="DmfPromptService"/> wired to the supplied (or default)
    /// fakes. Defaults share the test's profiles/session so the create trigger
    /// fires through the same fake the test asserts on. The dialog fake defaults
    /// to confirm=true (the Yes/No case 1 + 2 confirm) + a successful acquisition.
    /// </summary>
    /// <param name="launcher">Optional external-launcher override. When
    /// omitted the builder wires a fresh <see cref="FakeExternalLauncher"/>, a
    /// harmless in-memory recorder that NEVER shell-opens, so the case-2
    /// non-premium browser path can never reach the OS. Pass a spy to assert
    /// on the opened URL.</param>
    /// <param name="nxmRegistration">Optional shared registration-state
    /// override. When omitted, a plain fake (unavailable, not registered) is
    /// wired; the DMF wording follows it without probing.</param>
    /// <param name="gamingMode">Optional Gaming Mode state override. When
    /// omitted (the default), a non-gaming session so the browser paths run
    /// as they do on a desktop.</param>
    public static (DmfPromptService Service, ShellModalQueue Queue, IModListRefresh Refresh)
        BuildDmfPromptService(
            FakeProfileService? profiles = null,
            FakeProfileSession? session = null,
            FakeModRepository? repo = null,
            FakeModAcquisitionService? acquisition = null,
            FakeNexusAuthService? auth = null,
            FakeDialogService? dialogs = null,
            LocalizationService? localization = null,
            FakeNxmRegistrationState? nxmRegistration = null,
            IGamingModeState? gamingMode = null,
            FakeExternalLauncher? launcher = null,
            IModListRefresh? modListRefresh = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        nxmRegistration ??= new FakeNxmRegistrationState();
        gamingMode ??= new GamingModeState(false);
        // SAFETY: an omitted launcher defaults to the harmless in-memory
        // recorder (there is no production fallback in the service).
        launcher ??= new FakeExternalLauncher();
        modListRefresh ??= new RefreshRecorder();
        profiles.RepoLookup = repo;
        var queue = new ShellModalQueue();
        return (new DmfPromptService(
            profiles,
            session,
            repo,
            acquisition,
            auth,
            dialogs,
            localization,
            NullLogger<DmfPromptService>.Instance,
            nxmRegistration,
            gamingMode,
            launcher,
            queue,
            modListRefresh), queue, modListRefresh);
    }

    /// <summary>
    /// Builds a <see cref="ModListViewModel"/> wired to the supplied (or default)
    /// fakes. The defaults share one repository between the store + import fake
    /// so the add flow's reload joins the freshly imported source + version
    /// (mirrors the real import service's behavior).
    /// </summary>
    /// <param name="launcher">Optional external-launcher override. When
    /// omitted (the common case) the builder wires a fresh
    /// <see cref="FakeExternalLauncher"/>, a harmless in-memory recorder that
    /// NEVER shell-opens, so a non-Premium update click or any other
    /// external-open path in a test can never reach the OS. Pass a custom
    /// recorder/spy to assert on the opened URL or path.</param>
    /// <param name="nxmRegistration">Optional shared registration-state
    /// override. When omitted (the default), the VM's
    /// <c>IsNxmRegistered</c> reads a plain fake (unavailable, not registered)
    /// and no OS probe can happen. Pass a registrar-wired or value-set
    /// <see cref="FakeNxmRegistrationState"/> to drive the empty-state Nexus
    /// hint's visibility.</param>
    public static ModListViewModel BuildModList(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeModRepository? repo = null,
        FakeModImportService? importService = null,
        FakeDialogService? dialogs = null,
        LocalizationService? localization = null,
        FakeUpdateCheckService? updateCheck = null,
        FakeModUpdateInstaller? installer = null,
        FakeNexusAuthService? auth = null,
        FakeConfigLoader? configLoader = null,
        FakeAppStateStore? appState = null,
        FakeUpdateStateStore? updateState = null,
        FakeAutomaticUpdateService? automaticUpdates = null,
        ImportWorkflowViewModel? importWorkflow = null,
        Action<Action>? invokeOnUi = null,
        Func<DateTimeOffset>? getNow = null,
        Action<Action>? startCountdownTimer = null,
        Action? stopCountdownTimer = null,
        FakeExternalLauncher? launcher = null,
        FakeNxmRegistrationState? nxmRegistration = null,
        IGamingModeState? gamingMode = null,
        UpdateCheckRunner? runner = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        updateCheck ??= new FakeUpdateCheckService();
        updateState ??= new FakeUpdateStateStore(repo);
        installer ??= new FakeModUpdateInstaller { StateStore = updateState };
        auth ??= new FakeNexusAuthService();
        configLoader ??= new FakeConfigLoader();
        appState ??= new FakeAppStateStore();
        automaticUpdates ??= new FakeAutomaticUpdateService();
        profiles.RepoLookup = repo;
        // The inline import-workflow child VM: constructed over the SAME
        // profile/session/repo/import/localization fakes so a test that drives
        // StartBatch sees the imported mod land in the profile the mod-list VM
        // reads (mirrors production DI: one shared workflow singleton injected
        // into the mod-list VM). A test that wants to assert on the workflow
        // directly passes its own pre-constructed instance.
        importWorkflow ??= new ImportWorkflowViewModel(
            profiles,
            session,
            repo,
            importService,
            localization,
            NullLogger<ImportWorkflowViewModel>.Instance);

        // The density coordinator child: one shared instance per BuildModList,
        // constructed with safe no-op fakes so existing tests are unaffected.
        var detailedRows = new DetailedModRowsViewModel(
            configLoader,
            new FakeNexusModMetadataService(),
            repo,
            new FakeModThumbnailService(),
            NullLogger<DetailedModRowsViewModel>.Instance);

        invokeOnUi ??= static action => action();
        // SAFETY: an omitted launcher defaults to the harmless in-memory
        // recorder (never the OS shell; the VM has no production fallback).
        // This is the test-safety guarantee that no UI test can shell-open the
        // operator's browser, even when a path that triggers an external open
        // is exercised.
        launcher ??= new FakeExternalLauncher();
        // The shared nxm registration state: default is a plain fake
        // (unavailable, not registered, no probe possible).
        nxmRegistration ??= new FakeNxmRegistrationState();
        // Gaming Mode default: not gaming (the ordinary desktop session the
        // existing tests assume); gaming-gating tests pass a gaming state.
        gamingMode ??= new GamingModeState(false);

        // The link-external-folder child: constructed over the SAME
        // profile/session/repo/import/dialog fakes (after the launcher +
        // gaming-mode defaults above are settled) so a link-flow test sees its
        // linked container land in the profile the mod-list VM reads (mirrors
        // production DI: one shared child singleton injected into the mod-list
        // VM, which reloads when the child's flow finishes).
        var linkedMods = new LinkedModsViewModel(
            profiles,
            session,
            repo,
            importService,
            dialogs,
            localization,
            launcher,
            gamingMode,
            NullLogger<LinkedModsViewModel>.Instance);

        // The shared row context: the SAME installer/auth/gaming fakes the
        // test drives (premium reads resolve synchronously off the fake auth;
        // the installer's busy + progress events flow through the context).
        var rowContext = new ModRowContext(
            auth,
            installer,
            gamingMode,
            invokeOnUi ?? (static action => action()),
            NullLogger<ModRowContext>.Instance);
        // Wire the state store + a record-profile-id tracker into the fake
        // update-check service so RaiseCheckCompleted / CheckAsync record the
        // result through the store (mirroring the real service's publish-time
        // RecordResult). The record-profile-id follows the session's active
        // profile so a direct RaiseCheckCompleted (no explicit profile arg)
        // scopes to the right profile.
        updateCheck.StateStore = updateState;
        updateCheck.RecordProfileId = session.ActiveProfileId;
        session.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IProfileSession.ActiveProfileId))
            {
                updateCheck.RecordProfileId = session.ActiveProfileId;
            }
        };
        // The runner wires the manual CheckNow path + owns the refresh gate;
        // constructed with the test's fakes + no periodic timer (the manual
        // trigger does not depend on the timer being started). An optional
        // getNow lets the throttle tests drive the sliding window + the gate's
        // rate-limit clock deterministically; the countdown-timer seams are
        // forwarded to the runner's gate. The profiles fake backs the
        // runner's candidate pull. A test that needs to drive the gate
        // directly (the rate-limit tests) passes its own pre-constructed
        // runner.
        runner ??= new UpdateCheckRunner(
            session,
            profiles,
            updateCheck,
            configLoader,
            appState,
            automaticUpdates,
            NullLogger<UpdateCheckRunner>.Instance,
            startTimer: null,
            getNow: getNow,
            startCountdownTimer: startCountdownTimer,
            stopCountdownTimer: stopCountdownTimer);
        return new ModListViewModel(
            profiles,
            session,
            repo,
            dialogs,
            localization,
            updateState,
            runner,
            rowContext,
            importWorkflow,
            detailedRows,
            linkedMods,
            launcher,
            nxmRegistration,
            NullLogger<ModListViewModel>.Instance);
    }

    /// <summary>
    /// The wired parts returned by <see cref="BuildShell"/>: the shell VM plus
    /// every fake + concrete page VM a navigation test needs to drive the
    /// lifecycle (Profiles dirty edits, Integrations auth, Settings rehydrate,
    /// the mod-list reload observer, etc.). Includes the DmfPromptService so a
    /// shell-level test can drive a pending DMF trigger through the same
    /// instance the shell consumes on Mods entry.
    /// </summary>
    public sealed record ShellParts(
        ShellViewModel Shell,
        FakeProfileService Profiles,
        FakeProfileSession Session,
        FakeDialogService Dialogs,
        FakeLaunchService Launch,
        FakeAppUpdateService AppUpdate,
        FakeConfigLoader Config,
        FakeNexusAuthService Auth,
        FakeNxmHandlerRegistrar? NxmRegistrar,
        FakeNxmRegistrationState NxmRegistration,
        ProfilesViewModel ProfilesPage,
        ModListViewModel ModsPage,
        IntegrationsViewModel IntegrationsPage,
        PreferencesViewModel PreferencesPage,
        SettingsViewModel SettingsPage,
        FakeSteamService Steam,
        DmfPromptService Dmf,
        ShellModalQueue ModalQueue);

    /// <summary>
    /// Builds a <see cref="ShellViewModel"/> wired to concrete singleton page
    /// VMs over in-memory fakes, mirroring production composition. The hosted
    /// page VMs are real (not mocks), so navigation lifecycle effects
    /// (Profiles dirty guard, Integrations auth refresh, Settings rehydrate,
    /// mod-list reload) are exercised end-to-end. The shared fakes let a test
    /// seed state + assert on call counts (RefreshAsync calls, registration
    /// refreshes, reload side effects, launch calls).
    /// </summary>
    /// <param name="yieldForLaunchRender">The pre-launch render-yield seam.
    /// When omitted, completes immediately (a real Avalonia dispatcher yield
    /// would hang a unit test). Launch-attempt tests pass a
    /// TaskCompletionSource-backed task to hold the attempt at the yield.</param>
    /// <param name="launchHandoffTimeout">The post-spawn handoff timeout seam.
    /// When omitted, elapses immediately (no real 30-second wait), so a
    /// Launched result resolves its handoff via timeout by default.</param>
    /// <param name="nxmRegistration">Optional shared registration-state
    /// override. When omitted, a fake wired to the (possibly null)
    /// <paramref name="nxmRegistrar"/> is created so registrar-backed values
    /// flow through it.</param>
    public static ShellParts BuildShell(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeDialogService? dialogs = null,
        FakeLaunchService? launch = null,
        FakeAppUpdateService? appUpdate = null,
        FakeConfigLoader? config = null,
        FakeNexusAuthService? auth = null,
        FakeNxmHandlerRegistrar? nxmRegistrar = null,
        FakeNxmRegistrationState? nxmRegistration = null,
        LocalizationService? localization = null,
        FakeModRepository? repo = null,
        FakeSteamService? steam = null,
        Func<Task>? yieldForLaunchRender = null,
        Func<Task>? launchHandoffTimeout = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        dialogs ??= new FakeDialogService();
        launch ??= new FakeLaunchService();
        appUpdate ??= new FakeAppUpdateService();
        config ??= new FakeConfigLoader();
        auth ??= new FakeNexusAuthService();
        localization ??= new LocalizationService();
        repo ??= new FakeModRepository();
        steam ??= new FakeSteamService();
        profiles.RepoLookup = repo;
        yieldForLaunchRender ??= static () => Task.CompletedTask;
        launchHandoffTimeout ??= static () => Task.CompletedTask;
        nxmRegistration ??= new FakeNxmRegistrationState(nxmRegistrar);

        var modsPage = BuildModList(
            profiles, session, repo,
            dialogs: dialogs,
            localization: localization,
            auth: auth,
            configLoader: config,
            appState: new FakeAppStateStore(),
            nxmRegistration: nxmRegistration);
        // The DMF coordinator + the shell share one modal queue (mirroring
        // composition): the coordinator enqueues on ProfileCreated + reloads
        // the mod list itself after the prompt; the shell drains the queue on
        // destination entry. The modsPage the shell hosts is the refresh
        // target, so an accepted DMF add surfaces in the same list the test
        // reads.
        var (dmf, modalQueue, _) = BuildDmfPromptService(
            profiles, session, repo,
            dialogs: dialogs,
            localization: localization,
            auth: auth,
            nxmRegistration: nxmRegistration,
            modListRefresh: modsPage);
        var profilesPage = new ProfilesViewModel(
            profiles, session, dialogs, localization,
            NullLogger<ProfilesViewModel>.Instance);
        var integrationsPage = new IntegrationsViewModel(
            auth, localization, config, dialogs, nxmRegistrar, nxmRegistration,
            new FakeExternalLauncher(),
            NullLogger<IntegrationsViewModel>.Instance);
        var preferencesPage = new PreferencesViewModel(
            new FakePreferencesService(), config, localization,
            isRelayConsoleToggleSupported: true);
        var settingsPage = new SettingsViewModel(
            config,
            steam,
            localization,
            appUpdate, dialogs,
            new GamingModeState(false),
            invokeOnUi: static action => action(),
            NullLogger<SettingsViewModel>.Instance,
            new FakeExternalLauncher());

        var shell = new ShellViewModel(
            session, launch, dialogs, localization,
            profilesPage, modsPage, integrationsPage, preferencesPage, settingsPage,
            appUpdate,
            modalQueue,
            invokeOnUi: static action => action(),
            NullLogger<ShellViewModel>.Instance,
            config, nxmRegistration,
            yieldForLaunchRender,
            launchHandoffTimeout);

        return new ShellParts(
            shell, profiles, session, dialogs, launch, appUpdate, config,
            auth, nxmRegistrar, nxmRegistration, profilesPage, modsPage, integrationsPage,
            preferencesPage, settingsPage, steam, dmf, modalQueue);
    }
}

/// <summary>
/// In-memory <see cref="IExternalLauncher"/>: records every URI + path open
/// and returns a configurable outcome (success by default). The UI-test
/// builders default to this fake, so no test can shell-open the operator's
/// browser or file manager: the VMs have no production fallback, and the
/// default here touches nothing outside memory.
/// </summary>
/// <remarks>
/// A test that wants to assert on an open either reads
/// <see cref="OpenedUris"/> / <see cref="OpenedPaths"/> on its own instance, or
/// points the <see cref="OpenUriResult"/> / <see cref="OpenPathResult"/>
/// handlers at its own recorder. Point a handler at a throwing delegate to
/// exercise a caller's exception path; return <c>false</c> to exercise its
/// launch-failure alert.
/// </remarks>
internal sealed class FakeExternalLauncher : IExternalLauncher
{
    private readonly List<Uri> _openedUris = new();
    private readonly List<string> _openedPaths = new();

    /// <summary>Decides the OpenUri outcome; defaults to success.</summary>
    public Func<Uri, bool> OpenUriResult { get; set; } = _ => true;

    /// <summary>Decides the OpenPath outcome; defaults to success.</summary>
    public Func<string, bool> OpenPathResult { get; set; } = _ => true;

    /// <summary>Every URI this launcher was asked to open, in order.</summary>
    public IReadOnlyList<Uri> OpenedUris => _openedUris;

    /// <summary>Every filesystem path this launcher was asked to open, in order.</summary>
    public IReadOnlyList<string> OpenedPaths => _openedPaths;

    /// <inheritdoc />
    public bool OpenUri(Uri uri)
    {
        _openedUris.Add(uri);
        return OpenUriResult(uri);
    }

    /// <inheritdoc />
    public bool OpenPath(string path)
    {
        _openedPaths.Add(path);
        return OpenPathResult(path);
    }

    /// <summary>
    /// A launcher whose URI opens record into <paramref name="uris"/> and
    /// succeed.
    /// </summary>
    public static FakeExternalLauncher RecordingUris(List<Uri> uris) =>
        new() { OpenUriResult = uri => { uris.Add(uri); return true; } };

    /// <summary>
    /// A launcher whose path opens record into <paramref name="paths"/> and
    /// succeed.
    /// </summary>
    public static FakeExternalLauncher RecordingPaths(List<string> paths) =>
        new() { OpenPathResult = path => { paths.Add(path); return true; } };
}

/// <summary>
/// In-memory <see cref="IProfileService"/> for VM tests: backs the profile CRUD +
/// listing surface AND the per-profile mod-list surface (Track B). Records calls
/// so tests can assert on them. <c>PrepareModRoot</c> throws (staging is out of
/// scope for VM tests).
/// </summary>
internal sealed class FakeProfileService : IProfileService
{
    private readonly List<ProfileSummary> _profiles;
    private readonly Dictionary<Guid, List<ModListEntry>> _modLists = new();

    public FakeProfileService(IEnumerable<ProfileSummary>? seed = null) =>
        _profiles = seed is null ? new() : new(seed);

    /// <inheritdoc />
    /// <remarks>Raised from <see cref="CreateProfile"/>. The DMF prompt
    /// coordinator subscribes; tests that drive the new-profile trigger
    /// simulate a create through <see cref="CreateProfile"/> (the event fires)
    /// + a drain of the shell modal queue the coordinator enqueues onto.</remarks>
    public event EventHandler<ProfileSummary>? ProfileCreated;

    /// <summary>
    /// The (name, description, launchSettings) triples passed to the full
    /// <see cref="CreateProfile(string, string, LaunchSettings)"/> overload, in
    /// call order. Tests assert on the description + launch settings the Profiles
    /// page passed through the atomic create.
    /// </summary>
    public IReadOnlyList<(string Name, string Description, LaunchSettings Settings)> CreateCalls { get; }
        = new List<(string, string, LaunchSettings)>();

    /// <summary>
    /// The (id, name, description, launchSettings) quads passed to
    /// <see cref="UpdateProfile"/>, in call order. Tests assert on the exact
    /// atomic update the Profiles page issued.
    /// </summary>
    public IReadOnlyList<(Guid Id, string Name, string Description, LaunchSettings Settings)> UpdateCalls { get; }
        = new List<(Guid, string, string, LaunchSettings)>();

    /// <summary>
    /// When set, <see cref="UpdateProfile"/> throws this exception AFTER recording
    /// the call but BEFORE mutating any summaries or launch settings, mirroring
    /// the production service's "validate everything before any write" contract.
    /// Default <c>null</c> = no throw. Used by the Profiles-page save-error test
    /// (the catch maps the throw to a localized generic error) + the
    /// "rejected save then Cancel reloads original values" test (which relies on
    /// the no-mutation guarantee).
    /// </summary>
    public Exception? UpdateProfileThrows { get; set; }

    /// <summary>
    /// When set, <see cref="CreateProfile(string, string, LaunchSettings)"/>
    /// throws this exception AFTER recording the call but BEFORE mutating any
    /// summaries or launch settings (no ProfileCreated raised). Default
    /// <c>null</c> = no throw. Used by the Profiles-page new-draft save-error
    /// test: the catch maps the throw to a localized generic error and must NOT
    /// request active / DMF / reload.
    /// </summary>
    public Exception? CreateProfileThrows { get; set; }

    /// <summary>
    /// When set, <see cref="GetProfile"/> throws this exception, simulating an
    /// unreadable/corrupt active profile (the production service surfaces this
    /// as IOException / JsonException / UnauthorizedAccessException from the
    /// disk read). Default <c>null</c> = no throw. Used by the Profiles-page
    /// stale-active-recovery test to verify the page falls back to a genuine
    /// no-active state with no Delete path on the stale id.
    /// </summary>
    public Exception? GetProfileThrows { get; set; }

    public IReadOnlyList<Guid> DeletedIds { get; } = new List<Guid>();

    // ---- per-profile mod-list recording -----------------------------------

    /// <summary>Per-profile mod lists (in stored order); tests seed directly.</summary>
    public Dictionary<Guid, List<ModListEntry>> ModLists => _modLists;

    /// <summary>
    /// Optional repository lookup used to mirror production's source-based DMF
    /// recognition in <see cref="AddMod"/>. When <c>null</c> (a bare fake),
    /// every fresh add appends at the end (the content-based DMF rule needs
    /// the real on-disk resolver regardless).
    /// </summary>
    public IModRepository? RepoLookup { get; set; }

    public IReadOnlyList<(Guid Id, Guid ContainerId, bool Enabled)> SetModEnabledCalls { get; } = new List<(Guid, Guid, bool)>();
    public IReadOnlyList<(Guid Id, Guid ContainerId, bool OrderLocked)> SetModOrderLockedCalls { get; } = new List<(Guid, Guid, bool)>();
    public IReadOnlyList<IReadOnlyList<Guid>> SetModOrderCalls { get; } = new List<IReadOnlyList<Guid>>();
    public IReadOnlyList<(Guid Id, Guid ContainerId, ModVersionPolicy Policy)> SetModPolicyCalls { get; } = new List<(Guid, Guid, ModVersionPolicy)>();
    public IReadOnlyList<(Guid Id, Guid ContainerId, ModVersionPolicy Policy)> AddModCalls { get; } = new List<(Guid, Guid, ModVersionPolicy)>();

    /// <summary>
    /// Optional exception thrown by the next <see cref="AddMod"/> call (after the
    /// call is recorded). Default <c>null</c> = no throw. Used by the nxm-handler
    /// test to simulate AddMod failing after a successful acquisition.
    /// </summary>
    public Exception? AddModThrows { get; set; }

    public IReadOnlyList<(Guid Id, Guid ContainerId)> RemoveModCalls { get; } = new List<(Guid, Guid)>();
    /// <summary>Seeds a profile's mod list (replaces any prior). Test helper.</summary>
    public FakeProfileService WithMods(Guid id, params ModListEntry[] mods)
    {
        _modLists[id] = mods.Select(m => m with { }).ToList();
        return this;
    }

    /// <summary>
    /// Test helper: seeds a profile summary + optional launch settings without
    /// recording a create call (for setting up a scenario's persisted state
    /// before the VM reads it). Returns the seeded summary so the test can
    /// reference its id (e.g. to set as the session's active id).
    /// </summary>
    public ProfileSummary WithProfile(string name, string description = "", LaunchSettings? settings = null)
    {
        var summary = new ProfileSummary(Guid.NewGuid(), name, description);
        _profiles.Add(summary);
        if (settings is not null)
        {
            LaunchSettingsByProfile[summary.Id] = settings;
        }
        return summary;
    }

    private List<ModListEntry> EnsureList(Guid id)
    {
        if (!_modLists.TryGetValue(id, out var list))
        {
            list = new List<ModListEntry>();
            _modLists[id] = list;
        }
        return list;
    }

    public IReadOnlyList<ProfileSummary> ListProfiles() =>
        _profiles.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    public Profile GetProfile(Guid id)
    {
        // Honor GetProfileThrows before any lookup, simulating an unreadable /
        // corrupt active profile (the production service surfaces IOException /
        // JsonException / UnauthorizedAccessException from the disk read). The
        // Profiles VM catches those + falls back to a no-active state.
        if (GetProfileThrows is not null)
        {
            throw GetProfileThrows;
        }

        var summary = _profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException($"No profile {id}");
        // Round-trip the stored launch settings honestly (mirrors the production
        // service, whose GetProfile returns the full aggregate incl.
        // LaunchSettings). Falls back to a default when none was stored for the
        // profile, matching the production null-normalization.
        var settings = LaunchSettingsByProfile.TryGetValue(id, out var s)
            ? s
            : new LaunchSettings();
        return new Profile
        {
            Id = summary.Id,
            Name = summary.Name,
            Description = summary.Description,
            LaunchSettings = settings,
        };
    }

    public Profile CreateProfile(string name, string description, LaunchSettings launchSettings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name required", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(launchSettings);

        // Record the attempted call first so a test can assert the VM tried,
        // then honor CreateProfileThrows BEFORE any mutation, mirroring the
        // production service's "validate everything before any write" contract.
        // No summary is added, no LaunchSettings stored, no ProfileCreated raised
        // on the throw path, so a subsequent GetProfile/ListProfiles sees nothing.
        ((List<(string, string, LaunchSettings)>)CreateCalls).Add((name, description, launchSettings));
        if (CreateProfileThrows is not null)
        {
            throw CreateProfileThrows;
        }

        var created = new ProfileSummary(Guid.NewGuid(), name.Trim(), description.Trim());
        _profiles.Add(created);
        // Store the launch settings so a subsequent GetProfile returns them
        // (the production service round-trips the full aggregate through the
        // disk file). This is the same dictionary GetLaunchSettings reads, so a
        // profile created via the atomic create then read via GetLaunchSettings
        // is consistent.
        LaunchSettingsByProfile[created.Id] = launchSettings;
        // Mirror the production service: raise ProfileCreated AFTER the profile
        // is added to the list so a subscriber that re-lists sees it.
        ProfileCreated?.Invoke(this, created);
        return new Profile
        {
            Id = created.Id,
            Name = created.Name,
            Description = created.Description,
            LaunchSettings = launchSettings,
        };
    }

    public void UpdateProfile(Guid id, string name, string description, LaunchSettings launchSettings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name required", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(launchSettings);

        var idx = _profiles.FindIndex(p => p.Id == id);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No profile {id}");
        }

        // Record the attempted call first so a test can assert the VM tried,
        // then honor UpdateProfileThrows BEFORE any mutation, mirroring the
        // production service's "validate everything before any write" contract.
        // No summary or launch settings are changed on the throw path, so a
        // subsequent GetProfile/ListProfiles sees the original persisted values.
        ((List<(Guid, string, string, LaunchSettings)>)UpdateCalls).Add(
            (id, name, description, launchSettings));
        if (UpdateProfileThrows is not null)
        {
            throw UpdateProfileThrows;
        }

        _profiles[idx] = _profiles[idx] with { Name = name.Trim(), Description = description.Trim() };
        // Round-trip the launch settings through the same dictionary as
        // CreateProfile/GetLaunchSettings, mirroring the production service's
        // atomic single-write semantics (the real service persists all three
        // fields in one profile.json write).
        LaunchSettingsByProfile[id] = launchSettings;
    }

    public void DeleteProfile(Guid id)
    {
        var idx = _profiles.FindIndex(p => p.Id == id);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No profile {id}");
        }

        _profiles.RemoveAt(idx);
        _modLists.Remove(id);
        ((List<Guid>)DeletedIds).Add(id);
    }

    // ---- mod-list surface --------------------------------------------------

    /// <summary>
    /// When set, <see cref="GetModList"/> throws this exception, simulating an
    /// unreadable or deleted profile (the production service surfaces
    /// KeyNotFoundException for an unknown id). Default <c>null</c> = no throw.
    /// Used by the update-check runner tests to verify the candidate-pull
    /// failure posture (log + skip, no check call, no LastResult mutation).
    /// </summary>
    public Exception? GetModListThrows { get; set; }

    public IReadOnlyList<ModListEntry> GetModList(Guid id)
    {
        if (GetModListThrows is not null)
        {
            throw GetModListThrows;
        }
        return EnsureList(id).ToArray();
    }

    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
    {
        ((List<IReadOnlyList<Guid>>)SetModOrderCalls).Add(containerIdsInOrder);

        var list = EnsureList(id);

        // Mirror the production lock projection (ProfileService.SetModOrder) so
        // VM tests are LSP-faithful: locked entries keep their canonical
        // zero-based index (canonical = stable sort by current Order), and the
        // requested ordering projects onto the unlocked slots only. Without this
        // a fake would silently move locks, masking UI bugs.
        var canonical = list.OrderBy(m => m.Order).ToList();

        var reserved = new Dictionary<int, ModListEntry>();
        for (var i = 0; i < canonical.Count; i++)
        {
            if (canonical[i].OrderLocked)
            {
                reserved[i] = canonical[i];
            }
        }

        var desiredIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < containerIdsInOrder.Count; i++)
        {
            var cid = containerIdsInOrder[i];
            if (cid != Guid.Empty && !desiredIndex.ContainsKey(cid))
            {
                desiredIndex[cid] = i;
            }
        }

        var desiredUnlocked = canonical
            .OrderBy(m => desiredIndex.TryGetValue(m.ContainerId, out var idx) ? idx : int.MaxValue)
            .Where(m => !m.OrderLocked)
            .ToList();

        var result = new List<ModListEntry>(canonical.Count);
        var unlockedCursor = 0;
        for (var i = 0; i < canonical.Count; i++)
        {
            ModListEntry entry = reserved.TryGetValue(i, out var lockedEntry)
                ? lockedEntry
                : desiredUnlocked[unlockedCursor++];
            result.Add(entry with { Order = i });
        }

        list.Clear();
        list.AddRange(result);
    }

    public void SetModEnabled(Guid id, Guid containerId, bool enabled)
    {
        ((List<(Guid, Guid, bool)>)SetModEnabledCalls).Add((id, containerId, enabled));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list[idx] = list[idx] with { Enabled = enabled };
    }

    public void SetModOrderLocked(Guid id, Guid containerId, bool orderLocked)
    {
        ((List<(Guid, Guid, bool)>)SetModOrderLockedCalls).Add((id, containerId, orderLocked));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list[idx] = list[idx] with { OrderLocked = orderLocked };
    }

    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ((List<(Guid, Guid, ModVersionPolicy)>)AddModCalls).Add((id, containerId, policy));

        if (AddModThrows is not null)
        {
            throw AddModThrows;
        }

        var list = EnsureList(id);
        // Strict idempotent re-add (order/enabled/policy/lock untouched),
        // evaluated before any compaction so a re-add never disturbs survivors.
        if (list.Any(m => m.ContainerId == containerId))
        {
            return;
        }

        // Mirror production: stable Order sort, then insert. A fresh DMF add
        // goes to rank 0 + locked (shifting survivors down one rank); any other
        // add appends at the end unlocked. The fake recognizes DMF by source
        // only (Nexus mod 8); the content-based rule needs the real on-disk
        // resolver and is exercised against the real service in
        // Profiles.Tests (same posture as GetBaseNameCollision).
        var dmf = RepoLookup?.Get(containerId)?.Source is NexusSource { ModId: 8 };
        var entries = list.OrderBy(m => m.Order).ToList();
        entries.Insert(dmf ? 0 : entries.Count, new ModListEntry
        {
            ContainerId = containerId,
            Enabled = true,
            OrderLocked = dmf,
            Policy = policy,
        });
        list.Clear();
        list.AddRange(entries.Select((m, i) => m with { Order = i }));
    }

    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ((List<(Guid, Guid, ModVersionPolicy)>)SetModPolicyCalls).Add((id, containerId, policy));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list[idx] = list[idx] with { Policy = policy };
    }

    public void RemoveMod(Guid id, Guid containerId)
    {
        ((List<(Guid, Guid)>)RemoveModCalls).Add((id, containerId));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }

        // Mirror production: drop the entry (locked or unlocked), then compact
        // survivor Order dense by stable Order sort so a surviving lock's new
        // dense index is the new baseline.
        var survivors = list
            .Where(m => m.ContainerId != containerId)
            .OrderBy(m => m.Order)
            .Select((m, i) => m with { Order = i })
            .ToList();
        list.Clear();
        list.AddRange(survivors);
    }

    /// <summary>The (profileId, baseName, excludeContainerId) triples passed to
    /// <see cref="GetBaseNameCollision"/>, in call order. Tests assert on
    /// <c>ExcludeContainerId</c> to verify the add flow carried the re-add
    /// container id through.</summary>
    public IReadOnlyList<(Guid ProfileId, string BaseName, Guid? ExcludeContainerId)> GetBaseNameCollisionCalls { get; }
        = new List<(Guid, string, Guid?)>();

    /// <summary>
    /// The <see cref="ModListEntry"/> returned by the next
    /// <see cref="GetBaseNameCollision"/> call (default <c>null</c> = no
    /// collision). The fake does no real base-name resolution (that is exercised
    /// against the real service in <c>Profiles.Tests</c>); a VM test sets this to
    /// simulate a collision.
    /// </summary>
    public ModListEntry? GetBaseNameCollisionResult { get; set; }

    /// <summary>
    /// Records the call (for the exclude-container assertion) + returns
    /// <see cref="GetBaseNameCollisionResult"/>. The real resolution lives in
    /// <c>ProfileService</c> + is tested there.
    /// </summary>
    public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
    {
        ((List<(Guid, string, Guid?)>)GetBaseNameCollisionCalls).Add((id, baseName, excludeContainerId));
        return GetBaseNameCollisionResult;
    }

    /// <summary>Per-profile launch settings (read + written directly by tests).
    /// Default empty, mirroring a fresh / no-settings profile. The atomic
    /// <see cref="UpdateProfile"/> write + <see cref="CreateProfile"/> both
    /// populate this; <see cref="GetLaunchSettings"/> reads it.</summary>
    public Dictionary<Guid, LaunchSettings> LaunchSettingsByProfile { get; } = new();

    /// <summary>
    /// Returns the recorded launch settings for the profile (empty when none
    /// recorded), mirroring the production service's non-null default.
    /// </summary>
    public LaunchSettings GetLaunchSettings(Guid id) =>
        LaunchSettingsByProfile.TryGetValue(id, out var s) ? s : new LaunchSettings();

    public string PrepareModRoot(Guid id) => throw new NotImplementedException();

    /// <summary>
    /// The profiles root, unused by the shell/VM surface (the game-dir consent
    /// flow goes through the injected host fake). Present to satisfy the
    /// interface; a path that matches nothing.
    /// </summary>
    public string ProfilesRoot { get; } = "/nonexistent-curator-profiles";
}

/// <summary>
/// In-memory app-state fake covering the role interfaces the UI tests consume
/// (the concrete store implements all six; the two the UI never touches, the
/// metadata-backfill gate + the main-window geometry, stay in the General
/// tests' coverage). Records writes for assertion.
/// </summary>
internal sealed class FakeAppStateStore :
    IOnboardingState,
    IProfileActivationState,
    IUpdateCheckScheduleState,
    IKnownUpdateState
{
    /// <summary>
    /// The persisted onboarding flag (read + written directly by tests). Default
    /// <c>false</c>, mirroring a fresh / first-run real store.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    public int SetCount { get; private set; }
    public Guid? ActiveProfileId { get; set; } = null;

    /// <summary>
    /// The last property written via the <see cref="IUpdateCheckScheduleState.LastUpdateCheckUtc"/>
    /// setter (the public <see cref="LastUpdateCheckUtc"/> is the raw value; the
    /// explicit-interface setter records the write). Mirrors
    /// <see cref="SetCount"/> for the active-id path so tests can assert the
    /// runner persisted a timestamp.
    /// </summary>
    public int LastUpdateCheckSetCount { get; private set; }

    /// <summary>The raw last-check timestamp value (read + written directly by
    /// tests; the explicit-interface setter bumps <see cref="LastUpdateCheckSetCount"/>).</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; } = null;

    /// <summary>
    /// The manual throttle's sliding-window timestamps (read + written directly
    /// by tests; the explicit-interface setter bumps
    /// <see cref="ManualRefreshSetCount"/>). Default <c>null</c> (no throttle
    /// history recorded), mirroring a fresh / first-run real store.
    /// </summary>
    public IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; } = null;

    /// <summary>
    /// The number of times the <see cref="IUpdateCheckScheduleState.ManualRefreshTimestamps"/>
    /// setter was invoked, so tests can assert the runner persisted the window on
    /// a manual fire.
    /// </summary>
    public int ManualRefreshSetCount { get; private set; }

    Guid? IProfileActivationState.ActiveProfileId
    {
        get => ActiveProfileId;
        set
        {
            ActiveProfileId = value;
            SetCount++;
        }
    }

    DateTimeOffset? IUpdateCheckScheduleState.LastUpdateCheckUtc
    {
        get => LastUpdateCheckUtc;
        set
        {
            LastUpdateCheckUtc = value;
            LastUpdateCheckSetCount++;
        }
    }

    IReadOnlyList<DateTimeOffset>? IUpdateCheckScheduleState.ManualRefreshTimestamps
    {
        get => ManualRefreshTimestamps;
        set
        {
            ManualRefreshTimestamps = value;
            ManualRefreshSetCount++;
        }
    }

    /// <summary>
    /// The persisted known-update snapshots keyed by profile id (read + written
    /// directly by tests). Default <c>null</c> (no recorded state), mirroring a
    /// fresh / first-run real store.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; }
}

/// <summary>
/// In-memory <see cref="IUpdateStateStore"/> for the UI tests. Models the
/// replacement + acknowledge + hydrate semantics over a per-profile set of
/// flagged container ids so the mod-list VM tests can drive the persisted
/// known-update state. The real store's self-healing filter is covered by the
/// Integrations-layer tests; this fake does the simplest equivalent (drop
/// entries absent from the caller's candidates or whose container is gone).
/// </summary>
internal sealed class FakeUpdateStateStore : IUpdateStateStore
{
    private readonly Dictionary<Guid, HashSet<Guid>> _flagged = new();
    private readonly FakeModRepository? _repository;

    public FakeUpdateStateStore(FakeModRepository? repository = null)
    {
        _repository = repository;
    }

    /// <summary>The per-profile recorded calls (each entry: profileId + the
    /// result). Tests assert on the outcome + the updates the store saw.</summary>
    public IReadOnlyList<(Guid ProfileId, UpdateCheckResult Result)> RecordCalls { get; } = new List<(Guid, UpdateCheckResult)>();

    /// <summary>The per-container acknowledge calls (profileId, containerId).</summary>
    public IReadOnlyList<(Guid ProfileId, Guid ContainerId)> AcknowledgeCalls { get; } = new List<(Guid, Guid)>();

    public void RecordResult(Guid profileId, UpdateCheckResult result)
    {
        ((List<(Guid, UpdateCheckResult)>)RecordCalls).Add((profileId, result));
        if (result.Outcome == CheckOutcome.Success)
        {
            _flagged[profileId] = result.Updates.Select(u => u.ContainerId).ToHashSet();
        }
        else if (result.Outcome == CheckOutcome.NoNexusMods)
        {
            _flagged[profileId] = new HashSet<Guid>();
        }
        // NoAuth / RateLimited / Failed: preserve (no write).
    }

    public void AcknowledgeInstall(Guid profileId, Guid containerId)
    {
        ((List<(Guid, Guid)>)AcknowledgeCalls).Add((profileId, containerId));
        if (_flagged.TryGetValue(profileId, out var set))
        {
            set.Remove(containerId);
        }
    }

    public IReadOnlyCollection<Guid> GetKnownUpdateContainerIds(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates)
    {
        if (!_flagged.TryGetValue(profileId, out var set))
        {
            return Array.Empty<Guid>();
        }

        // Light self-heal: drop entries no longer among the caller's
        // candidates or whose container is gone, mirroring the real store's
        // filter closely enough for the VM tests that exercise it.
        var members = candidates.Select(c => c.ContainerId).ToHashSet();
        set.RemoveWhere(id => !members.Contains(id));
        if (_repository is not null)
        {
            set.RemoveWhere(id => _repository.Get(id) is null);
        }
        return set;
    }

    /// <summary>Test helper: seed a profile's flagged ids directly.</summary>
    public void SeedFlagged(Guid profileId, params Guid[] containerIds) =>
        _flagged[profileId] = containerIds.ToHashSet();
}

/// <summary>
/// No-op <see cref="IAutomaticUpdateService"/> for the UI tests. Records
/// <see cref="RunAfterCheckAsync"/> calls so the runner tests can assert the
/// service was chained after a check; raises <see cref="UpdatesApplied"/> only
/// when a test calls <see cref="RaiseUpdatesApplied"/>. Never installs anything
/// (per-row progress comes from the <see cref="FakeModUpdateInstaller"/>).
/// </summary>
internal sealed class FakeAutomaticUpdateService : IAutomaticUpdateService
{
    public IReadOnlyList<(UpdateCheckResult Result, Guid ProfileId)> Calls { get; } = new List<(UpdateCheckResult, Guid)>();

    public event EventHandler? UpdatesApplied;

    public Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default)
    {
        ((List<(UpdateCheckResult, Guid)>)Calls).Add((result, profileId));
        return Task.CompletedTask;
    }

    public void RaiseUpdatesApplied() => UpdatesApplied?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Configurable <see cref="IModUpdateInstaller"/> for the UI tests. Records
/// every install call (both the Try + awaiting shapes) with its arguments;
/// answers with the configured outcome (Installed by default, acknowledging
/// through the wired state store like the real installer), a settable busy
/// refusal for the Try shape, or a thrown exception (cancellation posture).
/// Raises progress active/inactive around each simulated attempt + exposes
/// <see cref="RaiseBusyChanged"/> for the busy push-down tests.
/// </summary>
internal sealed class FakeModUpdateInstaller : IModUpdateInstaller
{
    /// <summary>The install calls made through either method, in order
    /// (profileId, containerId, modId, expectedVersion, method).</summary>
    public IReadOnlyList<(Guid ProfileId, Guid ContainerId, int ModId, string ExpectedVersion, string Method)> Calls { get; }
        = new List<(Guid, Guid, int, string, string)>();

    /// <summary>The outcome returned by the next non-busy install call.
    /// Default <see cref="ModInstallStatus.Installed"/>. Per-call overrides:
    /// <see cref="OutcomeQueue"/>.</summary>
    public ModInstallOutcome NextOutcome { get; set; } = new(ModInstallStatus.Installed);

    /// <summary>Outcomes returned one per install call, in order (dequeued
    /// before falling back to <see cref="NextOutcome"/>), so a test can script
    /// a batch where specific entries fail + others succeed.</summary>
    public Queue<ModInstallOutcome> OutcomeQueue { get; } = new();

    /// <summary>When true, the next <see cref="TryInstallLatestAsync"/> call
    /// answers <see cref="ModInstallStatus.Busy"/> without recording progress
    /// or acknowledging (the manual no-op).</summary>
    public bool NextTryIsBusy { get; set; }

    /// <summary>When set, thrown from the next install attempt (after progress
    /// active=true), so a test can drive the caller-side cancellation or
    /// exception posture.</summary>
    public Exception? ThrowNext { get; set; }

    /// <summary>
    /// Optional state store acknowledged on an Installed outcome, mirroring the
    /// real installer's acknowledge-on-success so the VM-level flag-clearing
    /// assertions observe the persisted state the way production does.
    /// </summary>
    public IUpdateStateStore? StateStore { get; set; }

    /// <summary>The value reported by <see cref="IsBusy"/>; settable so a test
    /// can simulate an install in flight without holding a real gate.</summary>
    public bool IsBusy { get; set; }

    public event EventHandler? BusyChanged;
    public event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <summary>Raises <see cref="BusyChanged"/> (an installer's busy flag
    /// flipped somewhere else).</summary>
    public void RaiseBusyChanged() => BusyChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raises <see cref="ModUpdateProgress"/> for <paramref name="containerId"/>
    /// with the given <paramref name="isActive"/> state, simulating the
    /// installer's per-attempt progress signal.
    /// </summary>
    public void RaiseModUpdateProgress(Guid containerId, bool isActive) =>
        ModUpdateProgress?.Invoke(this, new ModUpdateProgressEventArgs(containerId, isActive));

    public async Task<ModInstallOutcome> TryInstallLatestAsync(
        Guid profileId, Guid containerId, int modId, string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
    {
        ((List<(Guid, Guid, int, string, string)>)Calls).Add(
            (profileId, containerId, modId, expectedVersion, nameof(TryInstallLatestAsync)));
        if (NextTryIsBusy)
        {
            return new ModInstallOutcome(ModInstallStatus.Busy);
        }
        return await RunAsync(profileId, containerId);
    }

    public async Task<ModInstallOutcome> InstallLatestAsync(
        Guid profileId, Guid containerId, int modId, string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
    {
        ((List<(Guid, Guid, int, string, string)>)Calls).Add(
            (profileId, containerId, modId, expectedVersion, nameof(InstallLatestAsync)));
        return await RunAsync(profileId, containerId);
    }

    private async Task<ModInstallOutcome> RunAsync(Guid profileId, Guid containerId)
    {
        RaiseModUpdateProgress(containerId, isActive: true);
        try
        {
            if (ThrowNext is not null)
            {
                await Task.FromException(ThrowNext);
            }
            var outcome = OutcomeQueue.Count > 0 ? OutcomeQueue.Dequeue() : NextOutcome;
            if (outcome.Status == ModInstallStatus.Installed)
            {
                StateStore?.AcknowledgeInstall(profileId, containerId);
            }
            return outcome;
        }
        finally
        {
            RaiseModUpdateProgress(containerId, isActive: false);
        }
    }
}

/// <summary>
/// Configurable dialog fake. <see cref="ConfirmResult"/> drives
/// <see cref="ConfirmAsync"/>. The escape-hatch and alert calls are recorded
/// for assertion; the escape-hatch also exposes its drive flag
/// (<see cref="EscapeHatchResult"/>) so a test can simulate a submit vs. a
/// cancel.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }

    /// <summary>
    /// The result returned by the next <see cref="ShowWelcomeAsync"/> call.
    /// Default <see cref="WelcomeChoice.Continue"/> (ESC / close equivalent).
    /// </summary>
    public WelcomeChoice WelcomeResult { get; set; } = WelcomeChoice.Continue;

    /// <summary>The number of <see cref="ShowWelcomeAsync"/> calls.</summary>
    public int WelcomeCalls { get; private set; }

    /// <summary>
    /// The result returned by the next escape-hatch call: <c>true</c> = the
    /// user submitted, <c>false</c> = cancelled. Default <c>false</c>.
    /// </summary>
    public bool EscapeHatchResult { get; set; }

    /// <summary>
    /// Optional task the next <see cref="ShowDiscoveryEscapeHatchAsync"/> call
    /// awaits before recording its result, so a test can hold the dialog open
    /// and observe in-flight state (e.g. the launch-attempt state staying set
    /// while a failure dialog is showing). Consumed (reset to <c>null</c>) by
    /// that call. Default <c>null</c> = returns immediately.
    /// </summary>
    public Task? NextEscapeHatchGate { get; set; }

    /// <summary>The missing-field lists the shell asked the escape-hatch to show,
    /// in call order. Tests assert on this to verify which fields the launch
    /// reported missing.</summary>
    public IReadOnlyList<IReadOnlyList<string>> EscapeHatchCalls { get; } = new List<IReadOnlyList<string>>();

    /// <summary>The (title, message) pairs passed to <see cref="ShowAlertAsync"/>,
    /// in call order.</summary>
    public IReadOnlyList<(string Title, string Message)> AlertCalls { get; } = new List<(string, string)>();

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmCalls++;
        LastConfirmMessage = message;
        return Task.FromResult(ConfirmResult);
    }

    public Task<WelcomeChoice> ShowWelcomeAsync()
    {
        WelcomeCalls++;
        return Task.FromResult(WelcomeResult);
    }

    public async Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields)
    {
        ((List<IReadOnlyList<string>>)EscapeHatchCalls).Add(missingFields);
        if (NextEscapeHatchGate is { } gate)
        {
            NextEscapeHatchGate = null;
            await gate;
        }
        return EscapeHatchResult;
    }

    public Task ShowAlertAsync(string title, string message)
    {
        ((List<(string, string)>)AlertCalls).Add((title, message));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The result returned by the next <see cref="ShowUnsavedChangesAsync"/>
    /// call. Default <see cref="UnsavedChangesChoice.Cancel"/> (the enum default,
    /// so ESC / close behave like the explicit Cancel button, matching the
    /// production dialog). Independent of <see cref="ConfirmResult"/>: the
    /// three-choice contract is a separate dialog and must not be overloaded
    /// onto the binary confirm result.
    /// </summary>
    public UnsavedChangesChoice UnsavedResult { get; set; } = UnsavedChangesChoice.Cancel;

    /// <summary>
    /// The number of <see cref="ShowUnsavedChangesAsync"/> calls + the last
    /// (title, message, canSave) triple passed in, so tests can assert on the
    /// prompt's framing + that Save was disabled when expected.
    /// </summary>
    public int UnsavedCalls { get; private set; }
    public string? LastUnsavedMessage { get; private set; }
    public bool LastUnsavedCanSave { get; private set; }

    public Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(string title, string message, bool canSave)
    {
        UnsavedCalls++;
        LastUnsavedMessage = message;
        LastUnsavedCanSave = canSave;
        return Task.FromResult(UnsavedResult);
    }

    /// <summary>
    /// The work passed to <see cref="ShowProgressAsync{T}"/>, in call order.
    /// Tests assert on this to verify the DMF download path was driven through
    /// the spinner. Each entry is invoked (awaited) so the work's result /
    /// exception surfaces to the caller as in production.
    /// </summary>
    public IReadOnlyList<(string Title, string Message, Delegate Work)> ProgressCalls { get; }
        = new List<(string, string, Delegate)>();

    public async Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work)
    {
        ((List<(string, string, Delegate)>)ProgressCalls).Add((title, message, work));
        // Drive the work so the caller sees its result / exception as in
        // production. No real spinner in tests; just await the work.
        return await work();
    }
}

/// <summary><see cref="ISteamService"/> with a configurable running flag +
/// discovery result, plus call counters for <see cref="Discover"/> +
/// <see cref="Rediscover"/> (the discovery escape-hatch + Settings VM tests
/// assert on these). <see cref="Discovery"/> is returned by both methods; tests
/// that need the two to diverge set <see cref="RediscoverResult"/> (falls back
/// to <see cref="Discovery"/>).</summary>
internal sealed class FakeSteamService : ISteamService
{
    // A default complete result so tests that exercise Discover/Rediscover
    // without configuring one get a sensible non-throwing answer (mirrors the
    // relay-client test double's default). Tests that need a specific outcome
    // or a side-effect (writing config) set Discovery/RediscoverResult/On*.
    private static readonly DiscoveryResult DefaultComplete = new(
        "/fake/steam", "/fake/darktide.exe", "/fake/compatdata", "/fake/proton",
        "GE-Proton-test", DiscoveryStatus.Complete, Array.Empty<string>());

    public bool Running { get; set; }

    /// <summary>The result returned by <see cref="Discover"/>. Defaults to a
    /// complete result so a bare Discover call does not throw.</summary>
    public DiscoveryResult? Discovery { get; set; } = DefaultComplete;

    /// <summary>The result returned by <see cref="Rediscover"/>; falls back to
    /// <see cref="Discovery"/> when null.</summary>
    public DiscoveryResult? RediscoverResult { get; set; }

    public int DiscoverCalls { get; private set; }
    public int RediscoverCalls { get; private set; }

    /// <summary>An optional side-effect invoked after <see cref="Discover"/>
    /// runs, so a test can simulate the service persisting the snapshot into
    /// config (mirrors the real service's write through the config
    /// loader).</summary>
    public Action? OnDiscover { get; set; }

    /// <summary>An optional side-effect invoked after <see cref="Rediscover"/>
    /// runs.</summary>
    public Action? OnRediscover { get; set; }

    public bool IsGameRunning() => Running;

    public DiscoveryResult Discover()
    {
        DiscoverCalls++;
        OnDiscover?.Invoke();
        return Discovery ?? DefaultComplete;
    }

    public DiscoveryResult Rediscover()
    {
        RediscoverCalls++;
        OnRediscover?.Invoke();
        return RediscoverResult ?? Discovery ?? DefaultComplete;
    }
}

/// <summary>
/// In-memory <see cref="IProfileSession"/> for shell / dialog tests. Mirrors the
/// real session's gate (<see cref="RequestActive"/> no-ops when running), delete
/// gate (<see cref="CanDeleteProfile"/> locks the active id while running), and
/// recovery (<see cref="ReconcileActive"/> clears the active id when it no longer
/// exists). Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> so the shell
/// + dialog react to live <see cref="IsRunning"/> changes the way the real polling
/// timer drives.
/// </summary>
internal sealed class FakeProfileSession : ObservableObject, IProfileSession
{
    private readonly Func<IReadOnlyList<ProfileSummary>>? _listProfiles;
    private Guid? _activeProfileId;
    private bool _isRunning;

    public FakeProfileSession(Func<IReadOnlyList<ProfileSummary>>? listProfiles = null)
    {
        _listProfiles = listProfiles;
    }

    public Guid? ActiveProfileId
    {
        get => _activeProfileId;
        set => SetProperty(ref _activeProfileId, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        set => SetProperty(ref _hasPendingChanges, value);
    }
    private bool _hasPendingChanges;

    public int RequestActiveCalls { get; private set; }
    public Guid? LastRequestedId { get; private set; }

    public void RequestActive(Guid id)
    {
        RequestActiveCalls++;
        LastRequestedId = id;
        if (IsRunning)
        {
            return;
        }

        ActiveProfileId = id;
    }

    public int CanDeleteProfileCalls { get; private set; }

    /// <summary>Mirrors the real session: the active id is locked while running.</summary>
    public bool CanDeleteProfile(Guid id)
    {
        CanDeleteProfileCalls++;
        return !(id == ActiveProfileId && IsRunning);
    }

    public int ReconcileCalls { get; private set; }

    public void ReconcileActive()
    {
        ReconcileCalls++;
        if (_listProfiles is null || _activeProfileId is not Guid id)
        {
            return;
        }

        var existing = _listProfiles();
        if (existing.Any(p => p.Id == id))
        {
            return;
        }

        ActiveProfileId = null;
    }

    /// <summary>Number of times <see cref="Refresh"/> was called.</summary>
    public int RefreshCalls { get; private set; }

    /// <summary>
    /// Optional callback invoked on each <see cref="Refresh"/>; tests use it to
    /// drive a deterministic running-state change (e.g. flip <see cref="IsRunning"/>
    /// to <c>true</c> to simulate the game having just started).
    /// </summary>
    public Action? OnRefresh { get; set; }

    /// <summary>
    /// Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for
    /// <see cref="IsRunning"/> without changing the value, simulating a false
    /// polling notification (the real polling timer re-checks the detector and
    /// the session only raises a change when the value actually flips, but the
    /// launch-attempt wait must tolerate any notification shape).
    /// </summary>
    public void RaiseIsRunningPropertyChanged() => OnPropertyChanged(nameof(IsRunning));

    /// <summary>
    /// Records the call + runs the optional <see cref="OnRefresh"/> callback so a
    /// test can simulate the running-state change a real Refresh would observe.
    /// </summary>
    public void Refresh()
    {
        RefreshCalls++;
        OnRefresh?.Invoke();
    }
}

/// <summary>
/// Configurable <see cref="IRelayLaunchService"/> for shell-VM launch tests.
/// <see cref="NextResult"/> is returned for every Launch call (default:
/// Launched). <see cref="LaunchCalls"/> records the ids the shell asked to
/// launch. <see cref="LaunchThrows"/> (when set) makes the next call throw
/// after recording, for the launch-attempt exception path.
/// </summary>
internal sealed class FakeLaunchService : IRelayLaunchService
{
    public LaunchResult NextResult { get; set; } =
        new(LaunchStatus.Launched, null, Array.Empty<string>());

    public IReadOnlyList<Guid> LaunchCalls { get; } = new List<Guid>();

    /// <summary>
    /// When set, <see cref="Launch"/> throws this exception after recording the
    /// call. Default <c>null</c> = no throw.
    /// </summary>
    public Exception? LaunchThrows { get; set; }

    public LaunchResult Launch(Guid profileId)
    {
        ((List<Guid>)LaunchCalls).Add(profileId);
        if (LaunchThrows is not null)
        {
            throw LaunchThrows;
        }
        return NextResult;
    }
}

/// <summary>
/// In-memory <see cref="IModRepository"/> for VM tests: backs the lookup surface
/// the mod-list VM joins source + version from, plus the path-derivation helper
/// used by staging tests. Tests seed containers directly; mutations update the
/// in-memory store.
/// </summary>
internal class FakeModRepository : IModRepository
{
    private readonly Dictionary<Guid, ModContainer> _byId = new();
    private readonly Dictionary<string, Guid> _untrackedByName = new(StringComparer.Ordinal);
    private readonly string _fakeRoot = Path.Combine(Path.GetTempPath(), "curator-fakerepo-" + Guid.NewGuid());

    public IReadOnlyList<ModContainer> List() => _byId.Values.ToArray();

    public ModContainer? Get(Guid containerId) =>
        _byId.TryGetValue(containerId, out var c) ? c : null;

    public ModContainer? FindBySource(ModSource source)
    {
        if (source is UntrackedSource)
        {
            return null;
        }
        return source switch
        {
            NexusSource n => _byId.Values.FirstOrDefault(c =>
                c.Source is NexusSource ns && ns.ModId == n.ModId),
            // Mirror production: linked identity is the normalized ExternalPath.
            LinkedSource l => _byId.Values.FirstOrDefault(c =>
                c.Source is LinkedSource ls && SamePath(ls.ExternalPath, l.ExternalPath)),
            _ => null,
        };
    }

    private static bool SamePath(string a, string b)
    {
        // Mirrors production (ModRepository.SamePath): full-path normalization
        // with trailing directory separators trimmed on both sides, so a path
        // stored with a trailing slash dedups against its slash-less form.
        var na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(
            na, nb,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public ModContainer? FindUntrackedByName(string name) =>
        _untrackedByName.TryGetValue(name, out var id) ? Get(id) : null;

    public ModContainer CreateContainer(ModSource source, string name)
    {
        var container = new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = source,
            Name = name,
            Versions = Array.Empty<ModVersion>(),
        };
        _byId[container.Id] = container;
        if (source is UntrackedSource)
        {
            _untrackedByName[name] = container.Id;
        }
        return container;
    }

    public ModContainer AddVersion(
        Guid containerId, string versionString, Action<string> populateFolder,
        DateTimeOffset? remoteUploadedAt = null, ModDisplayMetadata? displayMetadata = null)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            throw new KeyNotFoundException($"No container {containerId}");
        }

        var existing = container.Versions.FirstOrDefault(v => v.VersionString == versionString);
        List<ModVersion> versions;
        if (existing is not null)
        {
            // Mirror the production repo: dedup refreshes RemoteUploadedAt.
            var refreshed = existing with { RemoteUploadedAt = remoteUploadedAt };
            versions = container.Versions.Select(v => ReferenceEquals(v, existing) ? refreshed : v).ToList();
        }
        else
        {
            var entry = new ModVersion
            {
                Folder = Guid.NewGuid().ToString("N"),
                VersionString = versionString,
                IsLatest = true,
                ImportedAt = DateTimeOffset.UtcNow,
                RemoteUploadedAt = remoteUploadedAt,
            };
            versions = container.Versions
                .Select(v => v with { IsLatest = false })
                .Append(entry)
                .ToList();
        }
        // Mirror production: a non-null displayMetadata replaces the container
        // value in the same update; null preserves any prior value.
        var updated = displayMetadata is null
            ? container with { Versions = versions }
            : container with { Versions = versions, DisplayMetadata = displayMetadata };
        _byId[containerId] = updated;
        return updated;
    }

    public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!_byId.TryGetValue(containerId, out var container))
        {
            return false;
        }
        // Mirror production: missing-only. Any existing non-null metadata
        // returns false with no rewrite.
        if (container.DisplayMetadata is not null)
        {
            return false;
        }
        var updated = container with { DisplayMetadata = metadata };
        _byId[containerId] = updated;
        return true;
    }

    public void RemoveVersion(Guid containerId, string versionFolder)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            return;
        }
        var updated = container with
        {
            Versions = container.Versions.Where(v => v.Folder != versionFolder).ToArray(),
        };
        _byId[containerId] = updated;
    }

    public ModContainer? RenameContainer(Guid containerId, string newName)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            return null;
        }
        if (string.Equals(container.Name, newName, StringComparison.Ordinal))
        {
            return container;
        }
        // Mirror production: keep the untracked-name index consistent for
        // untracked containers; non-untracked identity is on the source record.
        if (container.Source is UntrackedSource)
        {
            _untrackedByName.Remove(container.Name);
            _untrackedByName[newName] = container.Id;
        }
        var updated = container with { Name = newName };
        _byId[containerId] = updated;
        return updated;
    }

    public string GetVersionFolderPath(Guid containerId, string versionFolder) =>
        Path.Combine(_fakeRoot, containerId.ToString(), versionFolder);

    public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
    {
        // Minimal fake: drop unreferenced versions + empty containers, mirroring
        // the real repository's behavior (including the linked-container keep:
        // a container id in the referenced set survives even with zero versions).
        var referencedContainerIds = referenced.Select(p => p.ContainerId).ToHashSet();
        foreach (var container in _byId.Values.ToArray())
        {
            var keep = container.Versions
                .Where(v => referenced.Contains((container.Id, v.Folder)))
                .ToArray();
            if (keep.Length == 0 && !referencedContainerIds.Contains(container.Id))
            {
                _byId.Remove(container.Id);
            }
            else
            {
                _byId[container.Id] = container with { Versions = keep };
            }
        }
    }

    // Default-safe: managed + unknown report available (matches production).
    // Linked availability is driven by ExternalUnavailableIds so a VM test can
    // simulate a broken linked container (production seeds the signal when the
    // container is recorded; the VM reads it once per reload here).
    public HashSet<Guid> ExternalUnavailableIds { get; } = new();

    public bool IsExternalAvailable(Guid containerId) =>
        !ExternalUnavailableIds.Contains(containerId);

    /// <summary>Test helper: seed a container with a single latest version.</summary>
    public ModContainer Seed(ModSource source, string name, string versionString = "1.0")
    {
        var container = CreateContainer(source, name);
        return AddVersion(container.Id, versionString, _ => { });
    }
}

/// <summary>
/// Recording <see cref="IModImportService"/> for VM tests. Captures each Import
/// call (source path, mod name, parsed source, version) so tests can assert the
/// add flow recorded the right metadata. Optionally upserts a wired
/// <see cref="IModRepository"/> so the add flow's reload joins the freshly
/// imported source + version (mirrors the real import service's behavior). A
/// per-call exception queue lets a test simulate an import failure (an invalid
/// source) to exercise the add flow's catch + alert + abort path.
/// </summary>
internal sealed class FakeModImportService : IModImportService
{
    private readonly IModRepository? _repo;

    public FakeModImportService(IModRepository? repo = null) => _repo = repo;

    public IReadOnlyList<(string SourcePath, string ModName, ModSource Source, string Version)> Imports { get; }
        = new List<(string, string, ModSource, string)>();

    /// <summary>
    /// Optional per-call queue: each Import call dequeues one exception and
    /// throws it (after recording the call), simulating an invalid source. A
    /// <c>null</c> slot means "succeed for this call". When empty / unset, Import
    /// proceeds normally. Mirrors <see cref="FakeDialogService.ImportResultQueue"/>.
    /// </summary>
    public Queue<Exception?>? ImportExceptionQueue { get; set; }

    /// <summary>
    /// Optional gate that each <see cref="Import"/> call awaits (blocking the
    /// calling thread, which is expected to be a thread-pool thread inside a
    /// Task.Run) before recording and proceeding. The inline-workflow VM tests
    /// use this to observe the processing state mid-import deterministically
    /// (no sleeps) and to drive the active-profile-change-during-processing
    /// edge. Default null = no blocking (the existing add-flow tests are
    /// unaffected).
    /// </summary>
    public TaskCompletionSource<bool>? ImportGate { get; set; }

    public (Guid ContainerId, string VersionId) Import(
        string sourcePath, string modName, ModSource source, string version,
        DateTimeOffset? remoteUploadedAt = null, ModDisplayMetadata? displayMetadata = null)
    {
        // Gate first, before recording, so a test can observe IsProcessing
        // while the worker is still blocked inside Import. Safe: Import runs on
        // a thread-pool thread (the VM's Task.Run), never the UI/test thread.
        if (ImportGate is not null)
        {
            ImportGate.Task.GetAwaiter().GetResult();
        }

        ((List<(string, string, ModSource, string)>)Imports).Add((sourcePath, modName, source, version));

        if (ImportExceptionQueue is { Count: > 0 })
        {
            var ex = ImportExceptionQueue.Dequeue();
            if (ex is not null)
            {
                throw ex;
            }
        }

        if (_repo is null)
        {
            // No wired repository: return a synthetic container id + version id
            // so the add flow has something to feed AddMod. Each call gets fresh
            // ids so distinct imports land as distinct entries.
            return (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        }

        // Mirror the real import service: resolve-or-create the container, then
        // add the version. This keeps the VM's reload join working in tests +
        // yields the version's opaque folder id (the real service's new return).
        ModContainer container;
        if (source is UntrackedSource)
        {
            container = _repo.FindUntrackedByName(modName) ?? _repo.CreateContainer(source, modName);
        }
        else
        {
            container = _repo.FindBySource(source) ?? _repo.CreateContainer(source, modName);
        }
        var updated = _repo.AddVersion(container.Id, version, _ => { }, remoteUploadedAt, displayMetadata);
        var versionId = updated.Versions.First(v => v.VersionString == version).Folder;
        return (container.Id, versionId);
    }

    /// <summary>The source paths passed to <see cref="GetBaseName"/>, in order.</summary>
    public IReadOnlyList<string> GetBaseNameCalls { get; } = new List<string>();

    /// <summary>
    /// Optional gate that each <see cref="GetBaseName"/> call awaits (blocking
    /// the calling thread, which is a thread-pool thread inside the VM's
    /// Task.Run) before recording and proceeding. The inline-workflow VM tests
    /// use this to create a deterministic window between SetState(Processing)
    /// and the collision check, so the active-profile-change-during-processing
    /// edge can be driven for the collision path. Default null = no blocking.
    /// </summary>
    public TaskCompletionSource<bool>? GetBaseNameGate { get; set; }

    /// <summary>
    /// Optional override for <see cref="GetBaseName"/>: receives the source path
    /// and returns the base name (or throws, to simulate an invalid source).
    /// When unset, the base name is derived from the path (folder name or
    /// archive stem, any extension), never throwing.
    /// </summary>
    public Func<string, string>? GetBaseNameFunc { get; set; }

    /// <summary>
    /// Peeks the base folder name (mirrors <see cref="IModImportService.GetBaseName"/>).
    /// The default derivation never throws; a test that needs an invalid-source
    /// failure sets <see cref="GetBaseNameFunc"/> to throw.
    /// </summary>
    public string GetBaseName(string sourcePath)
    {
        // Gate first, before recording, so a test can hold the worker inside
        // GetBaseName (the first Task.Run call) and drive a profile change.
        if (GetBaseNameGate is not null)
        {
            GetBaseNameGate.Task.GetAwaiter().GetResult();
        }

        ((List<string>)GetBaseNameCalls).Add(sourcePath);
        if (GetBaseNameFunc is not null)
        {
            return GetBaseNameFunc(sourcePath);
        }
        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return name;
    }

    /// <summary>The (source, modName) pairs passed to
    /// <see cref="FindExistingContainer"/>, in order.</summary>
    public IReadOnlyList<(ModSource Source, string ModName)> FindExistingContainerCalls { get; }
        = new List<(ModSource, string)>();

    /// <summary>
    /// Mirrors <see cref="IModImportService.FindExistingContainer"/>: resolves the
    /// container an import would dedup to, against the wired repo, without
    /// creating anything. Returns <c>null</c> when no repo is wired or no
    /// existing container matches.
    /// </summary>
    public ModContainer? FindExistingContainer(ModSource source, string modName)
    {
        ((List<(ModSource, string)>)FindExistingContainerCalls).Add((source, modName));
        if (_repo is null)
        {
            return null;
        }
        return source is UntrackedSource
            ? _repo.FindUntrackedByName(modName)
            : _repo.FindBySource(source);
    }

    /// <summary>The source paths passed to <see cref="LinkFolder"/>, in order.</summary>
    public IReadOnlyList<string> LinkFolderCalls { get; } = new List<string>();

    /// <summary>
    /// Optional per-call queue: each <see cref="LinkFolder"/> call dequeues one
    /// exception and throws it (after recording the call), simulating an invalid
    /// source (bad shape, containment rejection, unreadable folder). A
    /// <c>null</c> slot means "succeed for this call". When empty / unset,
    /// <see cref="LinkFolder"/> proceeds normally. Mirrors
    /// <see cref="ImportExceptionQueue"/>.
    /// </summary>
    public Queue<Exception?>? LinkFolderExceptionQueue { get; set; }

    /// <summary>
    /// Mirrors <see cref="IModImportService.LinkFolder"/>: records the external
    /// path, optionally throws from <see cref="LinkFolderExceptionQueue"/>, then
    /// resolves-or-creates the linked container on the wired repo (if any). When
    /// no repo is wired, returns a synthetic container id so the link flow has
    /// something to feed AddMod.
    /// </summary>
    public Guid LinkFolder(string externalPath)
    {
        ((List<string>)LinkFolderCalls).Add(externalPath);

        if (LinkFolderExceptionQueue is { Count: > 0 })
        {
            var ex = LinkFolderExceptionQueue.Dequeue();
            if (ex is not null)
            {
                throw ex;
            }
        }

        var normalized = Path.GetFullPath(externalPath);
        var source = new LinkedSource { ExternalPath = normalized };
        if (_repo is not null)
        {
            var existing = _repo.FindBySource(source);
            if (existing is not null)
            {
                return existing.Id;
            }
            var baseName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return _repo.CreateContainer(source, baseName).Id;
        }
        return Guid.NewGuid();
    }
}

/// <summary>
/// Recording <see cref="IPreferencesService"/> for tests. Captures the last
/// applied (theme, fontScale, language, showRelayConsole) quartet + the number
/// of apply calls so tests can assert the Preferences VM routes changes through
/// the authority.
/// </summary>
internal sealed class FakePreferencesService : IPreferencesService
{
    public int ApplyCalls { get; private set; }
    public ThemeMode LastTheme { get; private set; } = ThemeMode.System;
    public double LastFontScale { get; private set; } = 1.0;
    public string LastLanguage { get; private set; } = "en";
    public bool LastShowRelayConsole { get; private set; }

    public void ApplyAndPersist(ThemeMode theme, double fontScale, string language, bool showRelayConsole)
    {
        ApplyCalls++;
        LastTheme = theme;
        LastFontScale = fontScale;
        LastLanguage = language;
        LastShowRelayConsole = showRelayConsole;
    }
}

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Save"/> captures
/// the last-written config AND mirrors the real loader's round-trip by
/// promoting it to the live <see cref="Config"/> (so a subsequent
/// <see cref="Load"/> returns what was saved, like the real on-disk file).
/// Returns a configurable config from <see cref="Load"/> (defaults to a fresh
/// <see cref="CuratorConfig.CreateDefault"/>).
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load() => Config;

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        // Promote to the live Config so a subsequent Load returns the saved
        // state (mirrors the real loader's round-trip through the disk file).
        Config = config;
    }
}

/// <summary>
/// Configurable <see cref="IUpdateCheckService"/> shared by the runner tests
/// (call recording) + the mod-list VM tests (settable LastResult +
/// <see cref="RaiseCheckCompleted"/>). <see cref="CheckAsync"/> records the
/// profile id (Month-only path), optionally throws, sets <see cref="LastResult"/>,
/// + raises <see cref="CheckCompleted"/> (mirrors the real service's atomic
/// publish). <see cref="CheckThoroughAsync"/> mirrors that for the thorough
/// path. Tests that drive the badge refresh directly set
/// <see cref="LastResult"/> + call <see cref="RaiseCheckCompleted"/> without
/// invoking either method.
/// </summary>
internal sealed class FakeUpdateCheckService : IUpdateCheckService
{
    private readonly ConcurrentQueue<Guid> _calls = new();
    private readonly ConcurrentQueue<Guid> _thoroughCalls = new();
    private readonly ConcurrentQueue<IReadOnlyList<ModListCandidate>> _candidateBatches = new();

    /// <summary>
    /// Optional state store wired so <see cref="RaiseCheckCompleted"/> +
    /// <see cref="CheckAsync"/> + <see cref="CheckThoroughAsync"/> mirror the
    /// real service's RecordResult side-effect (the persisted known-update
    /// state is the source of the per-row flags). BuildModList wires this; a
    /// standalone construction leaves it null (the runner-only tests do not
    /// need it).
    /// </summary>
    public IUpdateStateStore? StateStore { get; set; }

    /// <summary>The profile id the next RecordResult should scope to (set by
    /// the runner path; the direct-RaiseCheckCompleted path defers to the
    /// active session via the VM). Tests that drive RaiseCheckCompleted set
    /// this so the recorded state is scoped correctly.</summary>
    public Guid? RecordProfileId { get; set; }

    /// <summary>The number of <see cref="CheckAsync"/> (Month-only) calls
    /// recorded so far. Thread-safe; safe to poll from the test thread while
    /// the runner fires on a thread-pool task.</summary>
    public int CallCount => _calls.Count;

    /// <summary>The profile ids passed to <see cref="CheckAsync"/>, in call
    /// order. A snapshot (<see cref="ConcurrentQueue{T}.ToArray"/>); safe to
    /// read after <see cref="Calls"/>/<see cref="CallCount"/> reach the expected
    /// count.</summary>
    public IReadOnlyList<Guid> Calls => _calls.ToArray();

    /// <summary>The number of <see cref="CheckThoroughAsync"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int ThoroughCallCount => _thoroughCalls.Count;

    /// <summary>The profile ids passed to <see cref="CheckThoroughAsync"/>, in
    /// call order.</summary>
    public IReadOnlyList<Guid> ThoroughCalls => _thoroughCalls.ToArray();

    /// <summary>
    /// The candidate lists passed to <see cref="CheckAsync"/> /
    /// <see cref="CheckThoroughAsync"/> (both shapes record into the same
    /// queue), in call order, so tests can assert the runner's entry-to-
    /// candidate mapping.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ModListCandidate>> CandidateCalls => _candidateBatches.ToArray();

    /// <summary>
    /// When set, thrown synchronously from every <see cref="CheckAsync"/> +
    /// <see cref="CheckThoroughAsync"/> call, after the call is recorded. Lets
    /// the exception-safety test assert the call was made AND that the runner
    /// swallowed the throw.
    /// </summary>
    public Exception? ThrowOnCheck { get; set; }

    /// <summary>
    /// The last check result, or <c>null</c> before the first check. Public
    /// setter so the mod-list VM tests can stage a result without invoking
    /// <see cref="CheckAsync"/>; <see cref="CheckAsync"/> +
    /// <see cref="CheckThoroughAsync"/> also set this on a real call (mirrors
    /// the real service).
    /// </summary>
    public UpdateCheckResult? LastResult { get; set; }

    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    public Task<UpdateCheckResult> CheckAsync(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
    {
        _calls.Enqueue(profileId);
        _candidateBatches.Enqueue(candidates);

        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastResult ??= new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: false,
            Outcome: CheckOutcome.Success);

        Record(profileId, LastResult);
        // Mirror the real service's contract: CheckCompleted is raised exactly
        // once per call. Also keeps the event field used (CS0067).
        CheckCompleted?.Invoke(this, LastResult);

        return Task.FromResult(LastResult);
    }

    public Task<UpdateCheckResult> CheckThoroughAsync(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates, CancellationToken ct = default)
    {
        _thoroughCalls.Enqueue(profileId);
        _candidateBatches.Enqueue(candidates);

        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastResult = new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: true,
            Outcome: CheckOutcome.Success);
        Record(profileId, LastResult);
        CheckCompleted?.Invoke(this, LastResult);
        return Task.FromResult(LastResult);
    }

    /// <summary>
    /// Sets <see cref="LastResult"/> + raises <see cref="CheckCompleted"/> so a
    /// test can simulate a check landing without invoking
    /// <see cref="CheckAsync"/>/<see cref="CheckThoroughAsync"/>. Also records
    /// the result through the wired state store (when <see cref="RecordProfileId"/>
    /// is set) so the mod-list VM's profile-scoped hydration reflects it,
    /// mirroring the real service's publish-time RecordResult.
    /// </summary>
    public void RaiseCheckCompleted(UpdateCheckResult? result, Guid? profileId = null)
    {
        LastResult = result;
        var scope = profileId ?? RecordProfileId;
        if (scope is { } pid)
        {
            Record(pid, result);
        }
        CheckCompleted?.Invoke(this, result);
    }

    private void Record(Guid profileId, UpdateCheckResult? result)
    {
        try
        {
            StateStore?.RecordResult(profileId, result ?? new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, false, false,
                Outcome: CheckOutcome.Success));
        }
        catch
        {
            // Defensive: a recording failure must not break the test's event raise.
        }
    }
}

/// <summary>
/// Recording <see cref="IModAcquisitionService"/> for the mod-list VM tests.
/// Captures each <see cref="AcquireLatestNexusAsync"/> call + optionally throws
/// to simulate a failed update. The base <see cref="AcquireFromNexusAsync"/> is
/// wired to the same recorder (tests assert on the unified call list).
/// </summary>
internal sealed class FakeModAcquisitionService : IModAcquisitionService
{
    public List<(string GameDomain, int ModId)> LatestNexusCalls { get; } = new();
    public (Guid ContainerId, string VersionId) NextResult { get; set; } =
        (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
    public Exception? ThrowNext { get; set; }

    public Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<long>? progress = null, CancellationToken ct = default) =>
        throw new NotImplementedException("AcquireFromNexusAsync is not exercised by the mod-list VM tests");

    public Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        LatestNexusCalls.Add((gameDomain, modId));
        if (ThrowNext is not null)
        {
            return Task.FromException<(Guid, string)>(ThrowNext);
        }
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// Configurable <see cref="INexusAuthService"/> for the mod-list VM + shell
/// navigation tests. The mod-list VM reads <see cref="GetCurrentStateAsync"/>
/// once at construction for the premium flag; this fake returns the configured
/// <see cref="State"/> (default a premium user; set null / non-premium to test
/// the gating). The OAuth login is optionally controllable via
/// <see cref="CancelOAuthOnToken"/> so a navigation test can prove leaving
/// Integrations cancels an in-flight login (Deactivate). <see cref="AuthStateChanged"/>
/// is wired for the DMF prompt coordinator tests.
/// </summary>
internal sealed class FakeNexusAuthService : INexusAuthService
{
    /// <summary>The state returned by the next GetCurrentStateAsync call.
    /// Default a premium OAuth user so the Update button is visible by default;
    /// tests that exercise non-premium gating set this to a non-premium
    /// state.</summary>
    public NexusAuthState? State { get; set; } = new(NexusAuthMethod.OAuth, "tester", IsPremium: true);

    /// <summary>The number of <see cref="GetCurrentStateAsync"/> calls, so tests
    /// can assert the automatic-update service verifies Premium only when
    /// gated, or that entering Integrations ran its auth refresh.</summary>
    public int GetCurrentStateCallCount { get; private set; }

    /// <summary>When true, <see cref="GetCurrentStateAsync"/> throws instead of
    /// returning the state (the caller's failure path: a caller that reads the
    /// premium state once must swallow + keep its default).</summary>
    public bool ThrowOnGetCurrentState { get; set; }

    /// <summary>
    /// When true, <see cref="LoginWithOAuthAsync"/> returns a task that completes
    /// as canceled when the supplied <see cref="CancellationToken"/> fires, so a
    /// test can prove a VM cancels an in-flight login on Deactivate. Records the
    /// task so the test can await it and assert IsCanceled.
    /// </summary>
    public bool CancelOAuthOnToken { get; set; }

    /// <summary>The task returned by the last <see cref="LoginWithOAuthAsync"/>
    /// call when <see cref="CancelOAuthOnToken"/> is set, so a test can await it
    /// and assert cancellation.</summary>
    public Task<NexusAuthResult>? LastOAuthTask { get; private set; }

    /// <summary>The CancellationToken passed into the last
    /// <see cref="LoginWithOAuthAsync"/> call.</summary>
    public CancellationToken LastOAuthCancellationToken { get; private set; }

    /// <summary>The number of <see cref="LoginWithOAuthAsync"/> calls.</summary>
    public int OAuthLoginCalls { get; private set; }

    /// <inheritdoc />
    public event EventHandler? AuthStateChanged;

    /// <summary>
    /// Raises <see cref="AuthStateChanged"/> with this sender. Simulates the
    /// signal the production service raises from its login / sign-out methods
    /// (the DMF prompt no longer subscribes; the shell's Integrations flow
    /// refreshes the nxm handler status on leave instead).
    /// </summary>
    public void RaiseAuthStateChanged() => AuthStateChanged?.Invoke(this, EventArgs.Empty);

    public Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default)
    {
        GetCurrentStateCallCount++;
        if (ThrowOnGetCurrentState)
        {
            throw new InvalidOperationException("offline");
        }
        return Task.FromResult(State);
    }

    public Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default)
    {
        OAuthLoginCalls++;
        LastOAuthCancellationToken = ct;
        if (CancelOAuthOnToken)
        {
            // Return a task that only completes when the VM cancels the token it
            // handed us (Deactivate flips it). Records the task so the test can
            // await it and assert IsCanceled.
            var tcs = new TaskCompletionSource<NexusAuthResult>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            LastOAuthTask = tcs.Task;
            return tcs.Task;
        }
        return Task.FromResult(NexusAuthResult.Success("OAuthUser", isPremium: false));
    }

    public Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task SignOutAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}

/// <summary>
/// Recording <see cref="INxmHandlerRegistrar"/> for the Integrations + shell
/// tests. The real registrar probes the OS; this one returns a settable
/// <see cref="Registered"/> flag and records Register/Unregister calls. Can be
/// configured to throw on Register to exercise the failure path. Its
/// <see cref="Unregister"/> mirrors the registrar self-guard contract: the call
/// is always recorded, but <see cref="Registered"/> only flips to false when it
/// was true (another owner's registration is never released).
/// </summary>
internal sealed class FakeNxmHandlerRegistrar : INxmHandlerRegistrar
{
    /// <summary>The value returned by <see cref="IsRegistered"/>.</summary>
    public bool Registered { get; set; }

    /// <summary>When set, thrown from <see cref="Register"/> (after the call is
    /// recorded) so tests can exercise the register-failure path.</summary>
    public Exception? ThrowOnRegister { get; set; }

    /// <summary>When set, thrown from <see cref="Unregister"/> (after the call is
    /// recorded) so tests can exercise the unregister-failure path.</summary>
    public Exception? ThrowOnUnregister { get; set; }

    /// <summary>When set, thrown from <see cref="MaintainRegistration"/> (after
    /// the call is recorded) so tests can exercise the maintenance-failure
    /// path.</summary>
    public Exception? ThrowOnMaintain { get; set; }

    public int IsRegisteredCalls { get; private set; }
    public int RegisterCalls { get; private set; }
    public int UnregisterCalls { get; private set; }
    public int MaintainCalls { get; private set; }

    public bool IsRegistered()
    {
        IsRegisteredCalls++;
        return Registered;
    }

    public void Register()
    {
        RegisterCalls++;
        if (ThrowOnRegister is not null)
        {
            throw ThrowOnRegister;
        }
        Registered = true;
    }

    public void Unregister()
    {
        UnregisterCalls++;
        if (ThrowOnUnregister is not null)
        {
            throw ThrowOnUnregister;
        }
        // Self-guard contract: only Curator's own registration is released.
        if (Registered)
        {
            Registered = false;
        }
    }

    public void MaintainRegistration()
    {
        MaintainCalls++;
        if (ThrowOnMaintain is not null)
        {
            throw ThrowOnMaintain;
        }
    }
}

/// <summary>
/// Recording <see cref="INxmRegistrationState"/> for the VM tests: settable
/// values, counts <see cref="RefreshFromOs"/> calls, and raises
/// <see cref="Changed"/> when refreshed (or on demand via
/// <see cref="RaiseChanged"/>). When constructed with a
/// <see cref="FakeNxmHandlerRegistrar"/>, a refresh reads the registrar's
/// probe (mirroring the production state's read) so register/release flows
/// propagate exactly as they do in the app.
/// </summary>
internal sealed class FakeNxmRegistrationState : INxmRegistrationState
{
    private readonly INxmHandlerRegistrar? _registrar;

    public FakeNxmRegistrationState(INxmHandlerRegistrar? registrar = null)
    {
        _registrar = registrar;
        IsAvailable = registrar is not null;
    }

    /// <summary>
    /// Whether a registrar is available. Pre-set from the constructor wiring
    /// (false with no registrar); settable for explicit scenarios.
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>The last-known value; overwritten by a refresh when a
    /// registrar is wired, manual otherwise.</summary>
    public bool IsRegistered { get; set; }

    /// <summary>The number of <see cref="RefreshFromOs"/> calls so far.</summary>
    public int RefreshFromOsCalls { get; private set; }

    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/> without recording a refresh (an
    /// out-of-band publish).</summary>
    public void RaiseChanged() => Changed?.Invoke();

    public void RefreshFromOs()
    {
        RefreshFromOsCalls++;
        if (_registrar is not null)
        {
            IsRegistered = _registrar.IsRegistered();
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Configurable <see cref="IAppUpdateService"/> for the app self-update runner
/// tests. <see cref="CheckAsync"/> records the call, optionally throws (after
/// recording), and returns a settable result while raising
/// <see cref="UpdateStateChanged"/> (mirrors the real service's atomic publish).
/// The download + apply members record their calls for assertion but are not
/// driven by the runner (the runner only fires the check). Thread-safe recording
/// so the runner's thread-pool dispatch can be polled from the test thread.
/// </summary>
internal sealed class FakeAppUpdateService : IAppUpdateService
{
    private readonly ConcurrentQueue<int> _checkCalls = new();
    private readonly ConcurrentQueue<int> _downloadCalls = new();
    private readonly ConcurrentQueue<int> _applyCalls = new();

    /// <summary>The number of <see cref="CheckForUpdatesAsync"/> calls recorded
    /// so far. Thread-safe; safe to poll from the test thread while the runner
    /// fires on a thread-pool task.</summary>
    public int CheckCallCount => _checkCalls.Count;

    /// <summary>The number of <see cref="DownloadUpdatesAsync"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int DownloadCallCount => _downloadCalls.Count;

    /// <summary>The number of <see cref="ApplyUpdatesAndRestart"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int ApplyCallCount => _applyCalls.Count;

    /// <summary>
    /// The value returned by the next <see cref="CheckForUpdatesAsync"/> call
    /// (default <c>null</c> = no update). When non-null, it is also published on
    /// <see cref="LastCheckResult"/> and announced via
    /// <see cref="UpdateStateChanged"/>.
    /// </summary>
    public AppUpdateInfo? NextCheckResult { get; set; }

    /// <summary>
    /// When set, thrown synchronously from every
    /// <see cref="CheckForUpdatesAsync"/> call, after the call is recorded. Lets
    /// the exception-safety test assert the call was made AND that the runner
    /// swallowed the throw.
    /// </summary>
    public Exception? ThrowOnCheck { get; set; }

    /// <summary>
    /// When set, thrown from every <see cref="DownloadUpdatesAsync"/> call
    /// (after recording) so the shell/settings VM tests can exercise the
    /// download-failure alert path. Default <c>null</c> = success.
    /// </summary>
    public Exception? ThrowOnDownload { get; set; }

    /// <summary>
    /// The supported / installed flag exposed by the fake. Defaults to
    /// <c>true</c> so the runner's check is not short-circuited; tests that
    /// exercise the unsupported path set this to <c>false</c>.
    /// </summary>
    public bool IsUpdateSupported { get; set; } = true;

    public string? CurrentVersion { get; set; } = "1.0.0";

    /// <summary>
    /// The last check result exposed by the fake. Public setter so the
    /// shell/settings VM tests can stage a result without invoking a check
    /// (mirrors <see cref="FakeUpdateCheckService.LastResult"/>).
    /// </summary>
    public AppUpdateInfo? LastCheckResult { get; set; }

    public AppUpdateInfo? UpdatePendingRestart { get; private set; }

    public event EventHandler? UpdateStateChanged;

    /// <summary>
    /// Raises <see cref="UpdateStateChanged"/> (mirrors how the real service
    /// publishes from its background check + how
    /// <see cref="FakeUpdateCheckService.RaiseCheckCompleted"/> works). Used by
    /// the shell/settings VM tests to simulate a check landing without invoking
    /// <see cref="CheckForUpdatesAsync"/>.
    /// </summary>
    public void RaiseUpdateStateChanged() => UpdateStateChanged?.Invoke(this, EventArgs.Empty);

    public Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _checkCalls.Enqueue(1);
        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastCheckResult = NextCheckResult;
        UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(NextCheckResult);
    }

    public Task DownloadUpdatesAsync(CancellationToken ct = default)
    {
        _downloadCalls.Enqueue(1);

        // When set, thrown before the download is recorded as successful so the
        // caller's download flow surfaces the failure (the shell/settings VM
        // tests exercise the alert path). Defaults to null (success).
        if (ThrowOnDownload is not null)
        {
            return Task.FromException(ThrowOnDownload);
        }

        UpdatePendingRestart = LastCheckResult;
        UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void ApplyUpdatesAndRestart() => _applyCalls.Enqueue(1);
}

/// <summary>
/// A no-op <see cref="INexusModMetadataService"/> for existing tests that do not
/// exercise the backfill. Returns an empty result with zero attempts. The
/// coordinator tests in <c>DetailedModRowsViewModelTests</c> use a richer fake
/// local to that test class.
/// </summary>
internal sealed class FakeNexusModMetadataService : INexusModMetadataService
{
    public Task<NexusModMetadataResult> BackfillMissingAsync(
        IReadOnlyList<Guid> priorityContainerIds, CancellationToken ct = default)
        => Task.FromResult(NexusModMetadataResult.Empty);
}

/// <summary>
/// A no-op <see cref="IModThumbnailService"/> for existing tests that do not
/// exercise thumbnail loading. Always returns <c>null</c> (placeholder). The
/// coordinator tests in <c>DetailedModRowsViewModelTests</c> use a richer fake
/// local to that test class.
/// </summary>
internal sealed class FakeModThumbnailService : IModThumbnailService
{
    public Task<Avalonia.Media.IImage?> GetThumbnailAsync(string? thumbnailUrl, CancellationToken ct = default)
        => Task.FromResult<Avalonia.Media.IImage?>(null);
}
