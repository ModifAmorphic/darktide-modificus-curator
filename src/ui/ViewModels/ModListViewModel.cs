using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Integrations;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The add flow's current mode (which action the Add split button's primary
/// click runs). Tracked by the view code-behind + mirrored on the VM (as
/// <see cref="ModListViewModel.AddMode"/>) so the split button's label reflects
/// the selected mode. All four modes are equal: each flyout item sets itself as
/// the default on click and runs its action. NexusMods is the default.
/// </summary>
public enum ModAddMode
{
    /// <summary>Opens the Darktide Nexus Mods games page in the browser (default).</summary>
    NexusMods,

    /// <summary>Import archives (zip, 7z, rar) via the file picker.</summary>
    Archive,

    /// <summary>Import mod folders via the folder picker.</summary>
    Folder,

    /// <summary>Link an external mod folder into the profile via the folder picker (no copy).</summary>
    LinkExternal,
}

/// <summary>
/// Owns the active profile's mod list, the dominant content area of the app
/// shell. Loads the profile's mods (joined with source + version from the mod
/// repository for the badge), and applies every edit through
/// <see cref="IProfileService"/>: enable/disable, reorder (up/down), per-mod
/// policy (Latest / Pinned), remove (confirmed), auto-sort (identity stub),
/// the add flow (file picker + drag-and-drop) forwarded to the injected
/// <see cref="ImportWorkflowViewModel"/> (the inline import card), and the
/// link-external-folder flow (folder picker only, no copy, no card) via
/// <see cref="IModImportService.LinkFolder"/>.
/// </summary>
/// <remarks>
/// <para><b>Active profile is the session's:</b> the list never decides the active
/// id; it reads <see cref="IProfileSession.ActiveProfileId"/> and reloads when it
/// changes. No active profile yields an empty list + <see cref="HasActiveProfile"/>
/// = false. The "select or create a profile" handoff (a HyperlinkButton to the
/// Profiles destination) is owned by the shell, not here: it binds shell-level
/// navigation + <c>ModList.HasActiveProfile</c> from the shell context, keeping
/// navigation out of this VM. This VM owns only the active-profile row + add-mod
/// states (the no-mods hint, drag-and-drop target, Add split button).</para>
/// <para><b>Rows carry state only:</b> each row is a <see cref="ModItemViewModel"/>
/// (container id + name + source badge + enabled + order + policy + policy-edit
/// state). All service calls live here; the view routes row interactions (toggle,
/// move, policy, remove, open external folder) through code-behind handlers
/// calling these commands with the row as the parameter (the established
/// per-row code-behind pattern).</para>
/// <para><b>The join key is <see cref="ModContainer.Id"/></b> (the profile entry's
/// identity): on reload, each entry's container is looked up via
/// <see cref="IModRepository.Get"/> for the display name, source badge, and
/// resolved version. A missing container yields a <see cref="UntrackedSource"/> +
/// a "not found" badge (staging warns at launch). A linked container's external
/// availability is pushed down to its row from
/// <see cref="IModRepository.IsExternalAvailable"/> at reload.</para>
/// <para><b>Edits are allowed while the game runs:</b> the list is the active
/// profile's config, not the running game's. The active profile is already locked
/// against switching by the shell, so the list stays put while the game runs and
/// edits land on the profile the user will launch next. Edits are deferred to the
/// next launch (Curator does not re-stage the mod tree while Darktide runs) and are
/// surfaced via the session's pending-changes flag, which the shell's status strip
/// surfaces as a yellow "changes pending" indicator while the game runs.</para>
/// <para><b>Localized text is live:</b> the header count + empty-state messages
/// re-resolve from <see cref="LocalizationService"/> on a culture change, and each
/// row's badge + policy text refresh too (via <see cref="ModItemViewModel.Refresh"/>).</para>
/// <para><b>Add flow:</b> the Add split button's four flyout items are all modes
/// that set themselves as the default on click (the face label tracks the
/// mode): "Add Nexus Mods" (the default; opens the Darktide Nexus Mods games
/// page in the browser via <see cref="AddNexusModsCommand"/>), "Add Mod
/// (archive)", "Add Mod (folder)", and "Link external folder". The archive +
/// folder modes open their pickers and share an entry point with
/// drag-and-drop; all forward the selected paths to
/// <see cref="ImportWorkflow"/>'s <c>StartBatchCommand</c>, which owns the
/// inline card, the per-item editing form, the batch state machine, and the
/// per-item import orchestration (GetBaseName, collision check, Import,
/// AddMod). The workflow emits a narrow <c>ItemImported</c> event carrying
/// the captured profile id; this VM reloads when it matches the active
/// profile. While the workflow is active (editing, processing, or failure),
/// the Add split button disables and drag-and-drop is rejected.</para>
/// <para><b>Link flow:</b> the Add split button's "Link external folder" flyout
/// item reduces to <see cref="LinkModsCommand"/>, which peeks the base name
/// (validates the mod-folder shape), checks the base-name collision (excluding a
/// re-link of the same path), then records the metadata-only container via
/// <see cref="IModImportService.LinkFolder"/> + adds the profile reference with
/// <see cref="ModVersionPolicy.Latest"/> (inert for linked). No modal; the folder
/// is linked, not copied.</para>
/// </remarks>
public partial class ModListViewModel : ObservableObject
{
    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Curator supports only Darktide, so
    /// there is no config key for it (mirrors <c>UpdateCheckService</c> +
    /// <c>ModAcquisitionService</c>).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

    /// <summary>
    /// The Nexus Mods games page for Darktide (the "Add Nexus Mods" flyout item's
    /// target). Built from <see cref="GameDomain"/> so the domain literal lives
    /// in one place.
    /// </summary>
    private const string NexusModsGamesUrl = "https://www.nexusmods.com/games/" + GameDomain;

    /// <summary>
    /// The client-side cooldown applied when a rate-limited check did not carry
    /// a server-reported reset (e.g. an HTTP 429 with no <c>x-rl-*</c> headers).
    /// Measured from the result's <see cref="UpdateCheckResult.CheckedAt"/> so
    /// the refresh button re-enables on a reasonable schedule even when Nexus
    /// stays silent about when the window refills.
    /// </summary>
    private static readonly TimeSpan RateLimitFallbackCooldown = TimeSpan.FromMinutes(1);

    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly IModOrderResolver _orderResolver;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IModAcquisitionService _acquisition;
    private readonly INexusAuthService _auth;
    private readonly IUpdateStateStore _updateState;
    private readonly UpdateCheckRunner _updateCheckRunner;
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly IAutomaticUpdateService _automaticUpdates;
    private readonly ILogger<ModListViewModel> _logger;
    private readonly Action<Action> _invokeOnUi;
    private readonly Action<Action>? _startCountdownTimer;
    private readonly Action? _stopCountdownTimer;
    /// <summary>
    /// The clock backing the rate-limit-active decision (a functional gate on
    /// the refresh button, unlike the cosmetic-only manual-throttle countdown,
    /// which reads <see cref="DateTimeOffset.UtcNow"/> directly). Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>; tests inject a controllable clock so
    /// the active flag + its elapsing are deterministic. Mirrors the
    /// <c>UpdateCheckRunner</c> + <c>UpdateCheckService</c> clock seams.
    /// </summary>
    private readonly Func<DateTimeOffset> _getNow;
    private readonly Func<Uri, bool> _launchExternal;
    private readonly Func<string, bool> _launchExternalPath;
    private readonly INxmRegistrationState _nxmRegistration;

    /// <summary>
    /// Creates the list VM, subscribes to the session (reload on active-profile
    /// change), the update-check service (badge refresh on
    /// <see cref="IUpdateCheckService.CheckCompleted"/>), the update coordinator
    /// (push the global busy flag down to rows), the automatic-update service
    /// (reload after a batch installs mods), and localization (culture refresh),
    /// loads the current profile's mods, and reads the Nexus premium state once
    /// (fire-and-forget; flips <see cref="IsPremiumUser"/> when it lands).
    /// </summary>
    public ModListViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        IModOrderResolver orderResolver,
        IDialogService dialogs,
        LocalizationService localization,
        IUpdateCheckService updateCheck,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        IUpdateStateStore updateState,
        UpdateCheckRunner updateCheckRunner,
        UpdateCoordinator updateCoordinator,
        IAutomaticUpdateService automaticUpdates,
        ImportWorkflowViewModel importWorkflow,
        DetailedModRowsViewModel detailedRows,
        Action<Action> invokeOnUi,
        ILogger<ModListViewModel> logger,
        INxmRegistrationState nxmRegistration,
        Action<Action>? startCountdownTimer = null,
        Action? stopCountdownTimer = null,
        Func<DateTimeOffset>? getNow = null,
        Func<Uri, bool>? launchExternal = null,
        Func<string, bool>? launchExternalPath = null)
    {
        _profiles = profiles;
        _session = session;
        _repo = repo;
        _importService = importService;
        _orderResolver = orderResolver;
        _dialogs = dialogs;
        _localization = localization;
        _updateCheck = updateCheck;
        _acquisition = acquisition;
        _auth = auth;
        _updateState = updateState;
        _updateCheckRunner = updateCheckRunner;
        _updateCoordinator = updateCoordinator ?? throw new ArgumentNullException(nameof(updateCoordinator));
        _automaticUpdates = automaticUpdates ?? throw new ArgumentNullException(nameof(automaticUpdates));
        ImportWorkflow = importWorkflow ?? throw new ArgumentNullException(nameof(importWorkflow));
        DetailedRows = detailedRows ?? throw new ArgumentNullException(nameof(detailedRows));
        _logger = logger;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _startCountdownTimer = startCountdownTimer;
        _stopCountdownTimer = stopCountdownTimer;
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
        _launchExternal = launchExternal ?? LaunchExternalDefault;
        _launchExternalPath = launchExternalPath ?? LaunchExternalPathDefault;
        _nxmRegistration = nxmRegistration ?? throw new ArgumentNullException(nameof(nxmRegistration));

        _session.PropertyChanged += OnSessionPropertyChanged;
        _localization.PropertyChanged += OnCultureChanged;
        _updateCheck.CheckCompleted += OnUpdateCheckCompleted;
        _updateCoordinator.BusyChanged += OnCoordinatorBusyChanged;
        _automaticUpdates.UpdatesApplied += OnAutomaticUpdatesApplied;
        _automaticUpdates.ModUpdateProgress += OnAutomaticUpdateProgress;
        ImportWorkflow.ItemImported += OnItemImported;

        // The refresh button's tooltip defaults to the normal "check now"
        // string. ReevaluateRefreshGate owns the tooltip once a rate limit or
        // the manual sliding-window throttle engages (each second via the
        // countdown tick). Nothing is active at construction.
        ManualRefreshTooltip = _localization["ModList_CheckNowTooltip"];

        // Read the Nexus premium state once at construction. Fire-and-forget:
        // GetCurrentStateAsync hits the network, so blocking the (UI-thread)
        // constructor on it would stall app startup. The result lands quickly
        // (sub-second typically) and flips IsPremiumUser; until then the Update
        // buttons stay disabled (also gated on an update being flagged, which
        // takes longer). No mid-session refresh by design (re-checking on Integrations
        // dialog close would burn an API call each time; a user signing in
        // mid-session needing a restart for the install behavior to change is acceptable).
        _ = LoadPremiumStateAsync();

        // The empty-state Nexus hint reads the shared last-known nxm
        // registration state; no OS probe happens here or in Reload.
        IsNxmRegistered = _nxmRegistration.IsRegistered;
        _nxmRegistration.Changed += OnNxmRegistrationChanged;

        Reload();
    }

    /// <summary>
    /// The automatic-update service finished a batch with at least one
    /// successful install. Mark the active profile as having staged changes (the
    /// batch changed one or more mod versions) and reload on the UI thread so the
    /// new versions + cleared flags show. The event fires on the UI thread (the
    /// service is invoked from the runner after it returns to the UI context),
    /// but marshal defensively so a test that fires it off-thread stays correct.
    /// </summary>
    private void OnAutomaticUpdatesApplied(object? sender, EventArgs e) =>
        _invokeOnUi(() =>
        {
            _session.HasPendingChanges = true;
            Reload();
        });

    /// <summary>
    /// The automatic-update service reports per-mod progress: a container's
    /// install started (active=true) or finished (active=false). Marshal to the
    /// UI thread, find the row by ContainerId, and set its
    /// <see cref="ModItemViewModel.IsUpdating"/> so the row-level spinner (left
    /// of the Nexus badge) tracks the currently installing mod. An event for a
    /// row no longer present (after a profile switch / reload) is ignored, so a
    /// switch mid-batch never leaves a stale spinner on a now-absent row.
    /// </summary>
    private void OnAutomaticUpdateProgress(object? sender, ModUpdateProgressEventArgs e) =>
        _invokeOnUi(() => ApplyModUpdateProgress(e.ContainerId, e.IsActive));

    /// <summary>
    /// Applies a per-mod automatic-update progress signal to the matching row.
    /// Finds the row by ContainerId; sets its <see cref="ModItemViewModel.IsUpdating"/>
    /// to <paramref name="isActive"/>. Ignores a container id with no matching
    /// row (the row may have been removed by a profile switch / reload between
    /// the event + this UI-thread callback).
    /// </summary>
    private void ApplyModUpdateProgress(Guid containerId, bool isActive)
    {
        var row = Mods.FirstOrDefault(m => m.ContainerId == containerId);
        if (row is null)
        {
            // The row is gone (profile switch / reload). Ignore: no stale
            // spinner is left on a now-absent row.
            return;
        }

        row.IsUpdating = isActive;
    }

    /// <summary>The active profile's mod rows, in load order (lower first).</summary>
    public ObservableCollection<ModItemViewModel> Mods { get; } = new();

    /// <summary>
    /// The inline import-workflow child VM (application-lifetime singleton,
    /// injected before this VM in composition). The view hosts its card below the
    /// toolbar; it owns the batch state machine, the editing form, and the per-item
    /// import orchestration. Exposed read-only so the view binds to
    /// <c>ImportWorkflow.IsActive</c> (gating the Add split button and drag-and-drop)
    /// and the workflow's own commands (<c>StartBatch</c>, <c>ImportCurrent</c>,
    /// <c>CancelBatch</c>, <c>CloseFailure</c>) directly. This VM subscribes to its
    /// narrow <see cref="ImportWorkflowViewModel.ItemImported"/> event to reload
    /// the active list when a successful import lands on the active profile.
    /// </summary>
    public ImportWorkflowViewModel ImportWorkflow { get; }

    /// <summary>
    /// The Compact/Detailed density coordinator child. Owns the persisted density
    /// selection, the metadata-backfill invocation, and the thumbnail hydration
    /// lifecycle. Exposed read-only so the toolbar (later) binds its density
    /// commands + projections to it without widening this VM with those
    /// mechanisms. <see cref="Reload"/> hands the final row snapshot to the child
    /// on every reload.
    /// </summary>
    public DetailedModRowsViewModel DetailedRows { get; }

    /// <summary>
    /// Whether a profile is active. Drives the header + the "no profile" empty
    /// state (owned here, not the shell). Also feeds <see cref="ShowAddModsHint"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddModsHint))]
    private bool _hasActiveProfile;

    /// <summary>Whether the active profile has at least one mod. Drives the
    /// row-list Border's IsVisible (the rows render only when non-empty).</summary>
    [ObservableProperty]
    private bool _hasMods;

    /// <summary>
    /// The number of mods in the active profile. Drives
    /// <see cref="ShowAddModsHint"/> (the hint shows for zero or one mod, so a
    /// DMF-only profile after onboarding still invites adding more).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddModsHint))]
    private int _modCount;

    /// <summary>
    /// Whether the no-mods / DMF-only "add a mod" hint should show: an active
    /// profile with zero or one mod (so a freshly-onboarded DMF-only profile
    /// still invites the user to add their own mods alongside the framework).
    /// A dedicated derived property because the view cannot express the
    /// conjunction in a single Avalonia compiled binding.
    /// </summary>
    public bool ShowAddModsHint => HasActiveProfile && ModCount <= 1;

    /// <summary>
    /// The auto-sort toggle state. Turning it on applies the
    /// <see cref="IModOrderResolver"/> once (the identity stub is a no-op). Held
    /// in-memory only for v1 (not persisted): the real dependency-driven resolver
    /// lands later, and the toggle reflects "apply once" intent.
    /// </summary>
    [ObservableProperty]
    private bool _autoSortEnabled;

    /// <summary>
    /// The Add split button's current mode (which action the primary click runs).
    /// Defaults to <see cref="ModAddMode.NexusMods"/>. The view sets this from the
    /// split button's flyout items + main click (public setter); the
    /// <see cref="AddModeLabel"/> derived string tracks it so the button reads
    /// "Add Nexus Mods" / "Add Mod (archive)" / "Add Mod (folder)" /
    /// "Link external folder" per the current mode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddModeLabel))]
    private ModAddMode _addMode = ModAddMode.NexusMods;

    /// <summary>
    /// Whether the Nexus account is premium. Read once at construction (see the
    /// constructor's premium-read note); no mid-session refresh. Drives the
    /// per-row update action's click behavior (Premium -> in-app install;
    /// regular/unknown -> open the Nexus files page) and is pushed down to each
    /// row so the per-row enabled state + tooltip can recompute without a parent
    /// walk in the binding. False until the read lands (or on a read failure; a
    /// restart re-reads).
    /// </summary>
    [ObservableProperty]
    private bool _isPremiumUser;

    /// <summary>
    /// Whether the last update check was rate-limited. Drives the header
    /// rate-limit notice (the "check incomplete" indicator). Set from
    /// <see cref="IUpdateCheckService.LastResult"/> on reload + on
    /// <see cref="IUpdateCheckService.CheckCompleted"/>. Stays <c>true</c>
    /// until a later non-rate-limited result lands; the coupled
    /// <see cref="IsRateLimitActive"/> flag (and the pill's visibility) is what
    /// flips back when the reset elapses.
    /// </summary>
    [ObservableProperty]
    private bool _isRateLimited;

    /// <summary>
    /// The server-reported reset of the exhausted window from the last
    /// rate-limited result (UTC), or <c>null</code> when the server gave none.
    /// Paired with <see cref="_rateLimitCheckedAt"/> (the result timestamp) so
    /// the active flag can fall back to <see cref="RateLimitFallbackCooldown"/>
    /// when this is null. Cleared alongside <see cref="_isRateLimited"/> when a
    /// later non-rate-limited result lands.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? _rateLimitResetsAt;

    /// <summary>
    /// The <see cref="UpdateCheckResult.CheckedAt"/> of the last rate-limited
    /// result, backing the <see cref="RateLimitFallbackCooldown"/> computation
    /// when <see cref="_rateLimitResetsAt"/> is null. Not observable: it feeds
    /// only the derived active flag, which is re-fired on each re-evaluation.
    /// </summary>
    private DateTimeOffset? _rateLimitCheckedAt;

    /// <summary>
    /// Whether the refresh button is currently blocked by an active rate limit:
    /// <c>true</c> when the last result was rate-limited AND the effective reset
    /// (the server-reported <see cref="_rateLimitResetsAt"/>, or
    /// <see cref="_rateLimitCheckedAt"/> + <see cref="RateLimitFallbackCooldown"/>
    /// when the server was silent) has not yet elapsed. Drives
    /// <see cref="IsRefreshEnabled"/> (the button disables) + the pill's
    /// visibility, so the two stay coherent: the pill shows exactly while the
    /// button is rate-limit-blocked, and both clear together when the reset
    /// elapses. Re-evaluated on each result + each countdown tick by
    /// <see cref="ReevaluateRefreshGate"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]
    private bool _isRateLimitActive;

    /// <summary>
    /// Whether the update coordinator reports an install in flight (manual or
    /// automatic). Set from <see cref="OnCoordinatorBusyChanged"/>; pushed down
    /// to each row so the per-row enabled state reflects the global "one install
    /// at a time" coordination. The manual Update command no longer sets this
    /// directly; acquiring the coordinator is the single source of truth.
    /// </summary>
    [ObservableProperty]
    private bool _anyRowUpdating;

    /// <summary>
    /// Whether the manual "check now" affordance is running a thorough check.
    /// True while <see cref="CheckForUpdatesNowCommand"/> awaits the runner's
    /// thorough check (multiple API calls, a few seconds); drives the header
    /// refresh button's enabled + spinner state. Cleared in the command's
    /// finally block on success or failure (no stuck state).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]
    private bool _isCheckingNow;

    /// <summary>
    /// Whether the manual sliding-window throttle is currently blocking the
    /// refresh button (the runner's free 10/hour budget is spent + the 2-minute
    /// cooldown has not elapsed). Set by <see cref="ReevaluateRefreshGate"/>
    /// after each manual attempt + re-evaluated on each countdown tick. Drives
    /// <see cref="IsRefreshEnabled"/> (the button disables) + the countdown
    /// tooltip (<see cref="ManualRefreshTooltip"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]
    private bool _isManualRefreshThrottled;

    /// <summary>
    /// The refresh button's tooltip. Reflects the active cause by priority: a
    /// server rate limit (the rate-limit tooltip) takes precedence, then the
    /// manual sliding-window countdown, then the normal "Check for updates now"
    /// string. Updated each second by the countdown tick while either cause is
    /// active, and re-resolved on a culture change. Bound to the button's
    /// <c>ToolTip.Tip</c>.
    /// </summary>
    [ObservableProperty]
    private string _manualRefreshTooltip;

    /// <summary>
    /// Whether the manual "check now" refresh button is enabled: NOT while a
    /// thorough check is in flight (<see cref="IsCheckingNow"/>), NOT while the
    /// manual sliding-window throttle is blocking
    /// (<see cref="IsManualRefreshThrottled"/>), and NOT while an active rate
    /// limit has not yet reset (<see cref="IsRateLimitActive"/>). A single
    /// computed property so the view binds the button's IsEnabled to one source;
    /// its dependencies each carry
    /// <c>[NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]</c> so the
    /// binding re-evaluates when any flips.
    /// </summary>
    public bool IsRefreshEnabled => !IsCheckingNow && !IsManualRefreshThrottled && !IsRateLimitActive;

    /// <summary>
    /// The localized split-button label for the current <see cref="AddMode"/>
    /// (mirrors the operator's mock: "Add Nexus Mods" / "Add Mod (archive)" /
    /// "Add Mod (folder)" / "Link external folder"). Re-fires on a culture
    /// change (live-refresh with the rest of the UI).
    /// </summary>
    public string AddModeLabel => AddMode switch
    {
        ModAddMode.NexusMods => _localization["ModList_AddNexusMods"],
        ModAddMode.Archive => _localization["ModList_AddArchive"],
        ModAddMode.Folder => _localization["ModList_AddFolder"],
        ModAddMode.LinkExternal => _localization["ModList_LinkExternalFolder"],
        _ => _localization["ModList_AddNexusMods"],
    };

    /// <summary>
    /// The localized primary hint shown in the no-mods / DMF-only empty state.
    /// Re-fires on a culture change.
    /// </summary>
    public string AddModsHintText => _localization["ModList_EmptyNoMods"];

    /// <summary>
    /// The localized secondary hint shown in the empty state ONLY when Curator
    /// is the registered nxm:// handler (so the user knows the Nexus 'Vortex' /
    /// 'Mod manager download' buttons route mods straight into the app).
    /// Re-fires on a culture change.
    /// </summary>
    public string NxmDownloadHintText => _localization["ModList_NxmDownloadHint"];

    /// <summary>
    /// Whether Curator is the registered OS handler for nxm:// links, mirrored
    /// from the shared <see cref="INxmRegistrationState"/> (last-known).
    /// Seeded at construction + re-read on each publish (the startup seed, the
    /// Nexus-enter probe, and register/release actions). Reload never probes
    /// the OS. False on platforms with no registrar; may be stale if an
    /// external app changed ownership.
    /// </summary>
    [ObservableProperty]
    private bool _isNxmRegistered;

    /// <summary>
    /// The localized rate-limit notice text shown in the header when
    /// <see cref="IsRateLimitActive"/> is true. Re-fires on a culture change.
    /// </summary>
    public string RateLimitedNoticeText => _localization["ModList_RateLimited"];

    /// <summary>
    /// The localized tooltip for the rate-limit pill and (when a rate limit is
    /// the active cause) the refresh button. When the server reported a reset,
    /// names the local time the window refills; otherwise falls back to a
    /// time-free "try again later" message (the server stayed silent, e.g. an
    /// HTTP 429 with no <c>x-rl-*</c> headers, so no specific time is promised).
    /// Re-fires on a culture change + each re-evaluation.
    /// </summary>
    public string RateLimitedTooltip =>
        RateLimitResetsAt is { } reset
            ? _localization.Format(
                "ModList_RateLimitedTooltipWithTime",
                reset.ToLocalTime().ToString("t", _localization.Culture))
            : _localization["ModList_RateLimitedTooltip"];

    /// <summary>
    /// The moment the active rate limit clears: the server-reported
    /// <see cref="RateLimitResetsAt"/>, or (when the server was silent) the
    /// rate-limited result's <see cref="_rateLimitCheckedAt"/> plus
    /// <see cref="RateLimitFallbackCooldown"/>. <c>null</c> when no rate-limited
    /// result has landed (or it has been cleared). A pure function of the
    /// observable fields, so the active flag stays a pure function of the clock.
    /// </summary>
    private DateTimeOffset? EffectiveRateLimitReset =>
        RateLimitResetsAt ?? (_rateLimitCheckedAt is { } checkedAt
            ? checkedAt + RateLimitFallbackCooldown
            : null);

    /// <summary>
    /// The inline import workflow finished a successful per-item import on the
    /// captured profile id. Reload the list only when that profile is the active
    /// one (the user is looking at it); an import that landed on a now-inactive
    /// profile (the profile changed mid-processing) does not misdirect, because
    /// this list always shows the active profile. The workflow owns pending-changes
    /// marking; this handler is a narrow reload trigger.
    /// </summary>
    private void OnItemImported(object? sender, Guid profileId)
    {
        if (_session.ActiveProfileId == profileId)
        {
            Reload();
        }
    }

    /// <summary>
    /// Session-driven reload: the active id changed (dropdown switch, create,
    /// delete-of-active). Rebuilds the list from the new profile. Running-state
    /// changes do not trigger a reload (the list stays put; edits are allowed
    /// while the game runs).
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProfileSession.ActiveProfileId))
        {
            Reload();
        }
    }

    /// <summary>
    /// The UI culture flipped (Preferences dialog). Re-fire the localized derived
    /// strings + refresh each row's badge + policy text.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)
            && e.PropertyName != "Item[]")
        {
            return;
        }

        OnPropertyChanged(nameof(AddModsHintText));
        OnPropertyChanged(nameof(NxmDownloadHintText));
        OnPropertyChanged(nameof(AddModeLabel));
        OnPropertyChanged(nameof(RateLimitedNoticeText));
        // Re-resolve the refresh-gate state so the rate-limit + throttle
        // tooltips pick up the new culture immediately (the countdown tick will
        // also re-resolve on its next fire, but this keeps the UI honest without
        // waiting). OnCultureChanged re-fires the tooltipped affordances.
        ReevaluateRefreshGate();
        foreach (var row in Mods)
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// The update check finished (background task fires the event on its
    /// completing thread). The check service already recorded the authoritative
    /// outcome through the persisted known-update store, so re-hydrate the rows
    /// from that store (profile-scoped) rather than reading the single in-memory
    /// <see cref="IUpdateCheckService.LastResult"/> (which cannot distinguish
    /// profiles). The transient rate-limit notice still reads
    /// <see cref="IUpdateCheckService.LastResult"/> (the notice is session-only;
    /// it does not need to persist and must not erase known flags). Idempotent.
    /// </summary>
    private void OnUpdateCheckCompleted(object? sender, UpdateCheckResult? result)
    {
        // The event fires on the check's completing thread (a threadpool thread
        // via UpdateCheckRunner's Task.Run). Marshal to the UI thread so the
        // hydration's iteration of the UI-bound Mods collection doesn't race
        // with a UI-thread Reload (ObservableCollection's enumerator is not
        // thread-safe vs concurrent mutation).
        _invokeOnUi(() => ApplyCheckLanded(result));
    }

    /// <summary>
    /// Applies a just-landed check: refreshes the rate-limit notice from the
    /// result, refreshes in-place row names when the check renamed any container
    /// (the name-sync piggybacks on the batch query), and re-hydrates the per-row
    /// update flags from the profile-scoped known-update store. Called on
    /// <see cref="IUpdateCheckService.CheckCompleted"/> + at the end of
    /// <see cref="Reload"/> (so a freshly rebuilt list picks up the persisted
    /// state without waiting for the next check).
    /// </summary>
    private void ApplyCheckLanded(UpdateCheckResult? result)
    {
        IsRateLimited = result?.RateLimited == true;
        RateLimitResetsAt = result?.RateLimitResetsAt;
        _rateLimitCheckedAt = result?.CheckedAt;
        // Re-evaluate the coupled refresh gate so the button + pill reflect the
        // just-landed rate-limit state (or its clearing) at once.
        ReevaluateRefreshGate();

        if (Mods.Count == 0)
        {
            return;
        }

        // If the check renamed any containers, refresh each row's displayed name
        // from the repository in place rather than a full Reload (the rest of the
        // row's state is current). Targeted per-row so unrelated rows are not
        // rebuilt.
        if (result?.NamesChanged == true)
        {
            foreach (var row in Mods)
            {
                var currentName = _repo.Get(row.ContainerId)?.Name;
                if (currentName is not null
                    && !string.Equals(currentName, row.Name, StringComparison.Ordinal))
                {
                    row.Name = currentName;
                }
            }
        }

        ApplyKnownUpdateState();
    }

    /// <summary>
    /// Reads the profile-scoped known-update container ids from the persisted
    /// store (which self-heals stale entries against the live profile + repo) and
    /// applies them to the rows by container id. This is the single source of
    /// truth for the per-row <see cref="ModItemViewModel.UpdateAvailable"/> flag,
    /// so a restart inside the interval gate shows prior flags before any API
    /// call, and a result from one profile never bleeds into another. Called from
    /// <see cref="ApplyCheckLanded"/> + <see cref="Reload"/> +
    /// <see cref="AcknowledgeUpdateAndReload"/>. A no-op when no profile is
    /// active.
    /// </summary>
    private void ApplyKnownUpdateState()
    {
        if (_session.ActiveProfileId is not Guid profileId)
        {
            foreach (var row in Mods)
            {
                row.UpdateAvailable = false;
            }
            return;
        }

        var flaggedIds = _updateState.GetKnownUpdateContainerIds(profileId);
        foreach (var row in Mods)
        {
            row.UpdateAvailable = flaggedIds.Contains(row.ContainerId);
        }
    }

    /// <summary>
    /// The global install-coordinator's busy flag changed (a manual row update or
    /// the automatic updater acquired or released it). Mirror it to
    /// <see cref="AnyRowUpdating"/> + push the new value down to every row so the
    /// per-row enabled state recomputes (Premium clicks stay disabled while
    /// another install runs; regular/unknown clicks stay enabled). Fires on a
    /// threadpool thread when the automatic updater acquires/releases, so marshal
    /// to the UI thread.
    /// </summary>
    private void OnCoordinatorBusyChanged(object? sender, EventArgs e) =>
        _invokeOnUi(() => AnyRowUpdating = _updateCoordinator.IsBusy);

    /// <summary>
    /// Pushes the current <see cref="IsPremiumUser"/> + <see cref="AnyRowUpdating"/>
    /// down to every row so each row's enabled state + tooltip recompute without
    /// a parent walk in the binding. Called on reload + whenever either flips.
    /// </summary>
    private void PushGlobalStateToRows()
    {
        foreach (var row in Mods)
        {
            row.IsPremiumUser = IsPremiumUser;
            row.AnyRowUpdating = AnyRowUpdating;
        }
    }

    /// <summary>
    /// Reads the Nexus premium state once (called fire-and-forget from the
    /// constructor). On success flips <see cref="IsPremiumUser"/> and pushes it
    /// down to the rows; on failure logs + leaves it false (a restart re-reads).
    /// </summary>
    private async Task LoadPremiumStateAsync()
    {
        try
        {
            var state = await _auth.GetCurrentStateAsync();
            IsPremiumUser = state?.IsPremium == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Nexus premium state read failed; per-row update actions stay regular-tier until restart.");
        }
        finally
        {
            // Whether the read landed or failed, push the current value down so
            // each row's tooltip + enabled state reflect it.
            PushGlobalStateToRows();
        }
    }

    /// <summary>
    /// Pushes the new premium flag down to every row when it changes (the
    /// construction-time read + a future refresh).
    /// </summary>
    partial void OnIsPremiumUserChanged(bool value) => PushGlobalStateToRows();

    /// <summary>
    /// Pushes the new global-busy flag down to every row when it changes (the
    /// coordinator acquired or released).
    /// </summary>
    partial void OnAnyRowUpdatingChanged(bool value) => PushGlobalStateToRows();

    /// <summary>
    /// The shared nxm registration state published a new last-known value
    /// (already on the UI thread). Re-read it into the empty-state hint flag.
    /// </summary>
    private void OnNxmRegistrationChanged() => IsNxmRegistered = _nxmRegistration.IsRegistered;

    /// <summary>
    /// Rebuilds <see cref="Mods"/> from the active profile. Each row's source +
    /// version are joined from the repository (by container id); a missing
    /// container yields a <see cref="UntrackedSource"/> + a "not found" badge
    /// (staging warns at launch). Rows are sorted by <see cref="ModListEntry.Order"/>.
    /// No active profile clears the list + sets the empty state.
    /// </summary>
    /// <remarks>
    /// Called on construction + on an active-profile change (session-driven),
    /// and also by the shell after the Settings dialog closes, so any
    /// out-of-band change to the repository is reflected in the
    /// <see cref="Mods"/> snapshot.
    /// </remarks>
    public void Reload()
    {
        var activeId = _session.ActiveProfileId;
        Mods.Clear();

        if (activeId is not Guid id)
        {
            HasActiveProfile = false;
            HasMods = false;
            ModCount = 0;
            // Hand an empty snapshot so old work is cancelled.
            _ = DetailedRows.SetRowsAsync(Array.Empty<ModItemViewModel>());
            return;
        }

        HasActiveProfile = true;

        var entries = _profiles.GetModList(id);
        foreach (var entry in entries.OrderBy(e => e.Order, Comparer<int>.Default))
        {
            var container = _repo.Get(entry.ContainerId);
            var found = container is not null;
            var source = container?.Source ?? new UntrackedSource();
            // The displayed version is the resolved one (Latest -> isLatest;
            // Pinned(id) -> the matching version's tag). An orphan pin (an id
            // with no matching version in the container) yields empty rather than
            // surfacing the opaque id; the dropdown exposes the container's
            // versions for re-pinning.
            var version = ResolveDisplayVersion(entry, container);
            var row = new ModItemViewModel(
                _localization,
                entry.ContainerId,
                container?.Name ?? string.Empty,
                source,
                version,
                entry.Enabled,
                entry.Order,
                entry.Policy,
                container?.Versions ?? Array.Empty<ModVersion>(),
                found,
                entry.OrderLocked,
                container?.DisplayMetadata);
            // Linked availability is a transient signal the repo recomputes on
            // rescan; read it once per Reload (no live watcher). Always false for
            // non-linked rows (the repo returns true for managed containers), so
            // only linked rows pay the query.
            if (source is LinkedSource)
            {
                row.IsExternalBroken = !_repo.IsExternalAvailable(entry.ContainerId);
            }
            Mods.Add(row);
        }

        HasMods = Mods.Count > 0;
        ModCount = Mods.Count;

        // Compute per-row move availability over unlocked rows only: an unlocked
        // row's move-up button is enabled when an unlocked row precedes it, and
        // move-down when an unlocked row follows it. Locked rows disable both
        // (their reorder grip is also disabled). Pushed down so the view binds
        // directly without a parent walk.
        ApplyMoveAvailability();

        // The freshly built rows default UpdateAvailable=false + carry no
        // premium / global-busy state. Push the current global state down, then
        // re-apply the persisted known-update state (profile-scoped) so a profile
        // switch (or a post-edit reload, or a restart) reflects the recorded
        // flags without waiting for the next check.
        PushGlobalStateToRows();
        ApplyKnownUpdateState();

        // Hand the final row snapshot to the density coordinator so it can push
        // the current density, clear thumbnails on Compact, or start thumbnail
        // hydration + metadata backfill on Detailed. Fire-and-forget: the
        // coordinator catches/logs all failures internally.
        _ = DetailedRows.SetRowsAsync(Mods.ToArray());
    }

    /// <summary>
    /// Acknowledges a successful version change for <paramref name="containerId"/>
    /// in the active profile (removing its persisted known-update entry
    /// immediately, without an extra API check), then reloads so the new version
    /// shows and the flag clears. Called after a successful nxm
    /// install/reinstall: the prior known-update state (recorded before the
    /// version change) would otherwise re-apply the flag via
    /// <see cref="ApplyKnownUpdateState"/>. The next authoritative check
    /// reconciles naturally (the mod is re-evaluated against the new version).
    /// </summary>
    public void AcknowledgeUpdateAndReload(Guid containerId)
    {
        if (_session.ActiveProfileId is Guid profileId)
        {
            try
            {
                _updateState.AcknowledgeInstall(profileId, containerId);
            }
            catch (Exception ex)
            {
                // Defensive: AcknowledgeInstall should not throw, but a
                // persistence failure must not block the reload.
                _logger.LogWarning(ex,
                    "Acknowledging update for container {Container} failed; the next check reconciles.",
                    containerId);
            }
        }

        Reload();
    }

    /// <summary>
    /// The version string shown in the badge for an entry: the resolved
    /// version's tag when the container + a matching version exist; empty
    /// otherwise (an orphan pin surfaces no readable tag, since the pin is an
    /// opaque id whose version is not present in the container).
    /// </summary>
    private static string ResolveDisplayVersion(ModListEntry entry, ModContainer? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        return container.ResolveVersion(entry.Policy)?.VersionString ?? string.Empty;
    }

    // ---- enable / disable --------------------------------------------------

    /// <summary>
    /// Applies a row's enabled toggle through <see cref="IProfileService.SetModEnabled"/>.
    /// The row's <see cref="ModItemViewModel.Enabled"/> is already two-way bound
    /// (the CheckBox flipped it); this persists it. Defense: no-op with no active
    /// profile.
    /// </summary>
    [RelayCommand]
    private void ToggleEnabled(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        _profiles.SetModEnabled(id, row.ContainerId, row.Enabled);
        _session.HasPendingChanges = true;
        _logger.LogDebug("Toggled {Container} enabled={Enabled}", row.ContainerId, row.Enabled);
    }

    // ---- reorder (up / down / drag) ----------------------------------------

    /// <summary>
    /// Computes + pushes per-row <see cref="ModItemViewModel.CanMoveUp"/> /
    /// <see cref="ModItemViewModel.CanMoveDown"/> over the unlocked rows only.
    /// An unlocked row's move-up is enabled when an unlocked row precedes it, and
    /// move-down when an unlocked row follows it; locked rows disable both.
    /// Called at the end of <see cref="Reload"/> so the buttons reflect the
    /// current order + lock state after every edit.
    /// </summary>
    private void ApplyMoveAvailability()
    {
        var unlockedIndex = 0;
        var unlockedCount = Mods.Count(m => !m.OrderLocked);
        foreach (var row in Mods)
        {
            if (row.OrderLocked)
            {
                row.CanMoveUp = false;
                row.CanMoveDown = false;
                continue;
            }

            row.CanMoveUp = unlockedIndex > 0;
            row.CanMoveDown = unlockedIndex < unlockedCount - 1;
            unlockedIndex++;
        }
    }

    /// <summary>
    /// The unlocked rank of <paramref name="containerId"/> in <see cref="Mods"/>,
    /// or -1 when it is locked / missing. Unlocked rank counts only unlocked rows.
    /// </summary>
    private int UnlockedRankOf(Guid containerId)
    {
        var rank = 0;
        foreach (var row in Mods)
        {
            if (row.OrderLocked)
            {
                continue;
            }

            if (row.ContainerId == containerId)
            {
                return rank;
            }

            rank++;
        }

        return -1;
    }

    /// <summary>
    /// Moves a row up one unlocked rank: the row swaps to the previous UNLOCKED
    /// slot, crossing any locked rows it passes. Locked rows, the top unlocked
    /// row, and a no-active-profile call are strict no-ops. Persists the new
    /// full order through <see cref="IProfileService.SetModOrder"/> exactly once,
    /// marks the session pending on a real order change, and reloads.
    /// </summary>
    [RelayCommand]
    private void MoveUp(ModItemViewModel? row) => MoveTo(row, -1);

    /// <summary>
    /// Moves a row down one unlocked rank (symmetric to <see cref="MoveUp"/>).
    /// No-op for a locked row, the bottom unlocked row, or no active profile.
    /// </summary>
    [RelayCommand]
    private void MoveDown(ModItemViewModel? row) => MoveTo(row, +1);

    /// <summary>
    /// Shared move core: resolves the row's current unlocked rank, adds
    /// <paramref name="delta"/>, and commits the reorder through
    /// <see cref="CommitReorderCore"/>. Boundary rejection (target out of range
    /// or a no-op) happens inside the planner, so a no-move call makes no service
    /// call. No-op for a locked row or no active profile.
    /// </summary>
    private void MoveTo(ModItemViewModel? row, int delta)
    {
        if (row is null || _session.ActiveProfileId is not Guid id || row.OrderLocked)
        {
            return;
        }

        var sourceRank = UnlockedRankOf(row.ContainerId);
        if (sourceRank < 0)
        {
            return;
        }

        CommitReorderCore(id, new ReorderRequest(row.ContainerId, sourceRank + delta));
    }

    /// <summary>
    /// Commits a drag-reorder request: validates against the current rows,
    /// rejects locked / missing / out-of-range / no-op requests without a service
    /// call, constructs the exact legal full order around locked slots, persists
    /// it through <see cref="IProfileService.SetModOrder"/> exactly once, marks
    /// the session pending only on a real order change, and reloads.
    /// </summary>
    [RelayCommand]
    private void CommitReorder(ReorderRequest? request)
    {
        if (request is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        CommitReorderCore(id, request.Value);
    }

    /// <summary>
    /// Builds + persists a reorder request against the current rows. The planner
    /// returns null for any invalid or no-op request, so a no-change call makes
    /// no service call and sets no pending flag.
    /// </summary>
    private void CommitReorderCore(Guid profileId, ReorderRequest request)
    {
        var rows = Mods.Select(r => (r.ContainerId, r.OrderLocked)).ToArray();
        var fullOrder = ModReorderPlanner.BuildFullOrder(rows, request);
        if (fullOrder is null)
        {
            return;
        }

        _profiles.SetModOrder(profileId, fullOrder);
        _session.HasPendingChanges = true;
        Reload();
        _logger.LogDebug(
            "Reordered container {Container} to unlocked rank {Rank}",
            request.SourceContainerId, request.TargetUnlockedRank);
    }

    /// <summary>
    /// Toggles a row's <see cref="ModListEntry.OrderLocked"/> through
    /// <see cref="IProfileService.SetModOrderLocked"/> and reloads. Lock metadata
    /// alone does NOT set <see cref="IProfileSession.HasPendingChanges"/>: it
    /// does not change the staged mod tree or <c>mods.lst</c> (the order +
    /// enabled + policy rows are unchanged; only the lock flag flips). No-op with
    /// no active profile.
    /// </summary>
    [RelayCommand]
    private void ToggleOrderLock(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        _profiles.SetModOrderLocked(id, row.ContainerId, !row.OrderLocked);
        Reload();
        _logger.LogDebug(
            "Set order lock={Locked} on container {Container}", !row.OrderLocked, row.ContainerId);
    }

    // ---- per-mod policy ----------------------------------------------------

    /// <summary>
    /// Switches a row's policy to <see cref="ModVersionPolicy.Latest"/> via
    /// <see cref="IProfileService.SetModPolicy"/>, then reloads.
    /// </summary>
    [RelayCommand]
    private void SetPolicyLatest(ModItemViewModel? row) =>
        ApplyPolicy(row, ModVersionPolicy.Latest);

    /// <summary>
    /// Switches a row's policy to <see cref="PinnedPolicy"/> with the row's
    /// selected dropdown version id, via <see cref="IProfileService.SetModPolicy"/>,
    /// then reloads. The dropdown guarantees the id exists in the container (it
    /// is built from the container's version list), so the call satisfies
    /// <see cref="IProfileService"/>'s orphan-id validation. A <c>null</c>
    /// selection (a version-less container) is a no-op: such a container cannot
    /// be pinned.
    /// </summary>
    [RelayCommand]
    private void SetPolicyPinned(ModItemViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // The dropdown guarantees the id exists in the container. A null
        // selection means the container has no versions to pin to: no-op (the
        // policy ComboBox reset is handled on the next genuine change).
        if (row.SelectedVersion is null)
        {
            return;
        }

        ApplyPolicy(row, new PinnedPolicy(row.SelectedVersion.VersionId));
    }

    private void ApplyPolicy(ModItemViewModel? row, ModVersionPolicy policy)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        _profiles.SetModPolicy(id, row.ContainerId, policy);
        _session.HasPendingChanges = true;
        Reload();
        _logger.LogDebug("Set policy {Policy} on container {Container}", policy, row.ContainerId);
    }

    // ---- remove (confirmed) ------------------------------------------------

    /// <summary>
    /// Removes a row from the profile after a confirmation (the user-facing
    /// "remove from this list" gate). The repository copy survives
    /// (<c>RemoveMod</c> drops only the profile-local reference); the confirm is
    /// about the profile edit, not data loss. No-op with no active profile.
    /// </summary>
    [RelayCommand]
    private async Task Remove(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        var title = _localization["RemoveMod_Title"];
        var message = _localization.Format("RemoveMod_Message", row.Name);
        if (!await _dialogs.ConfirmAsync(title, message))
        {
            return;
        }

        _profiles.RemoveMod(id, row.ContainerId);
        _session.HasPendingChanges = true;
        Reload();
        _logger.LogInformation("Removed container {Container} from profile {Id}", row.ContainerId, id);
    }

    // ---- per-mod update (one at a time, premium-only) ----------------------

    /// <summary>
    /// The manual "check for updates now" trigger (the mod-list header refresh
    /// button). Routes through <see cref="Session.UpdateCheckRunner.CheckNowAsync"/>
    /// so the runner stays the single owner of "fire a check" logic + uses the
    /// thorough path (<see cref="IUpdateCheckService.CheckThoroughAsync"/>) that
    /// also catches mods outside the Month window. Awaits the runner's task so
    /// <see cref="IsCheckingNow"/> can drive the button's spinner + disable for
    /// the duration (a few seconds for the per-mod pass). <see cref="IsCheckingNow"/>
    /// is set before the await + cleared in the finally block on success or
    /// failure (no stuck state). The existing
    /// <see cref="IUpdateCheckService.CheckCompleted"/> subscription re-applies
    /// the result to the rows when it lands. The command is a no-op (via the
    /// runner) when no profile is active; a second click while one is in flight
    /// is a no-op (the <see cref="IsCheckingNow"/> guard). After the await,
    /// <see cref="ReevaluateRefreshGate"/> re-evaluates the runner's
    /// sliding-window throttle so the button disables + the countdown tooltip
    /// engages when the manual path is rate-limited.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesNow()
    {
        // Re-entrancy guard: a second click while a thorough check is running is
        // a no-op (the button's IsEnabled is also bound to IsRefreshEnabled, but
        // the guard makes a programmatic call safe too).
        if (IsCheckingNow)
        {
            return;
        }

        IsCheckingNow = true;
        try
        {
            // No ConfigureAwait(false): the finally (clearing IsCheckingNow)
            // should run on the UI thread so the bound control re-enables
            // synchronously. The runner's Task.Run dispatches the actual check
            // to a thread-pool task; we only await its completion here.
            await _updateCheckRunner.CheckNowAsync();
            // Re-evaluate the refresh gate after every attempt (a fire that
            // spent the free budget engages the countdown; a blocked attempt
            // also lands here as CompletedTask with the throttle active). Stays
            // on the UI thread (no ConfigureAwait above).
            ReevaluateRefreshGate();
        }
        finally
        {
            IsCheckingNow = false;
        }
    }

    // ---- refresh gate (rate-limit + manual-throttle countdown, shared timer) --

    /// <summary>
    /// Re-evaluates the refresh affordance against both disabling causes, the
    /// server rate limit (active until the effective reset elapses) and the
    /// manual sliding-window throttle (the runner's free-budget cooldown), and
    /// applies the result to <see cref="IsRateLimitActive"/>,
    /// <see cref="IsManualRefreshThrottled"/>, <see cref="ManualRefreshTooltip"/>,
    /// and the countdown timer. Called after every <c>CheckForUpdatesNow</c>
    /// attempt, when a check result lands (<see cref="ApplyCheckLanded"/>), on
    /// each 1-second tick (<see cref="OnCountdownTick"/>), and on a culture
    /// change.
    /// </summary>
    /// <remarks>
    /// The two causes share a single countdown timer: it runs while EITHER has
    /// an unelapsed deadline (so the rate-limit pill clears the instant its
    /// reset passes, even mid-throttle) and stops when neither does. The tooltip
    /// takes the rate-limit reason when active (the more informative,
    /// server-driven cause) and otherwise falls back to the throttle countdown
    /// or the normal "check now" string. Production's start delegate is
    /// idempotent (no-op if already running), so invoking it on every re-eval is
    /// safe.
    /// </remarks>
    private void ReevaluateRefreshGate()
    {
        // (1) Rate-limit active: the last result was rate-limited AND the
        //     effective reset (server-reported, or CheckedAt + the fallback
        //     cooldown when the server was silent) has not elapsed.
        var effectiveReset = EffectiveRateLimitReset;
        bool rateLimitActive = IsRateLimited
            && effectiveReset is { } reset
            && _getNow() < reset;
        IsRateLimitActive = rateLimitActive;

        // (2) Manual sliding-window throttle: the runner reports the next
        //     allowed manual fire, or null once the cooldown has elapsed.
        var next = _updateCheckRunner.NextManualRefreshAllowedAt;
        bool throttled = next is not null;
        IsManualRefreshThrottled = throttled;

        // (3) Shared timer: run while either cause has an unelapsed deadline,
        //     stop when neither does.
        if (rateLimitActive || throttled)
        {
            _startCountdownTimer?.Invoke(OnCountdownTick);
        }
        else
        {
            _stopCountdownTimer?.Invoke();
        }

        // (4) Tooltip by priority: rate-limit > throttle > normal.
        ManualRefreshTooltip = rateLimitActive
            ? RateLimitedTooltip
            : throttled && next is { } allowed
                ? BuildThrottleTooltip(allowed)
                : _localization["ModList_CheckNowTooltip"];

        // (5) Re-fire the rate-limit tooltip (its time/later branch depends on
        //     RateLimitResetsAt, which may have just changed).
        OnPropertyChanged(nameof(RateLimitedTooltip));
    }

    /// <summary>
    /// The 1-second countdown tick callback (production wires a
    /// <c>DispatcherTimer</c>; tests invoke the captured callback directly). A
    /// thin wrapper over <see cref="ReevaluateRefreshGate"/> so each tick clears
    /// a now-elapsed rate limit / throttle, updates the tooltip, and stops the
    /// timer once neither cause remains.
    /// </summary>
    private void OnCountdownTick() => ReevaluateRefreshGate();

    /// <summary>
    /// Formats the throttle tooltip from the absolute unlock instant: resolves
    /// the localized throttle template with the remaining time formatted as
    /// <c>m:ss</c>. The remaining time is a COSMETIC computation for the tooltip
    /// only (the throttle enforcement is entirely in the runner, testable via
    /// the injected clock), so it does not need the injected clock seam the
    /// functional rate-limit decision uses and reads <see cref="DateTimeOffset.UtcNow"/>
    /// directly.
    /// </summary>
    private string BuildThrottleTooltip(DateTimeOffset nextAllowedAt)
    {
        var remaining = nextAllowedAt - DateTimeOffset.UtcNow;
        return _localization.Format("ModList_ManualRefreshThrottled", FormatRemaining(remaining));
    }

    /// <summary>
    /// Formats a remaining <see cref="TimeSpan"/> as <c>m:ss</c> (e.g.
    /// <c>1:30</c>, <c>0:05</c>, <c>2:00</c>). Clamps negative values (a tick
    /// landing a hair past the unlock instant) to zero so the tooltip never
    /// shows a negative. Pure + culture-free (manual integer math, no
    /// format-string separators), so it is independently unit-testable.
    /// </summary>
    internal static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }
        var totalSeconds = (int)remaining.TotalSeconds;
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    /// <summary>
    /// The stable per-row update action. Branches on the verified Premium state:
    /// <b>Premium</b> re-downloads the mod's latest MAIN release via
    /// <see cref="IModAcquisitionService.AcquireLatestNexusAsync"/> (the
    /// auth-only / premium path) under the global install coordinator, then
    /// acknowledges the install (clearing the persisted known-update entry
    /// immediately, with no extra API check) + reloads; <b>regular / unknown</b>
    /// opens the mod's Nexus files page in the user's browser via the injectable
    /// external-launcher seam, surfacing a fallback alert on launch failure.
    /// </summary>
    /// <remarks>
    /// <para><b>Defense.</b> No-op when: there is no active profile; the row is
    /// not Nexus+Latest (<see cref="ModItemViewModel.IsNexusLatest"/>); no update
    /// is flagged (<see cref="ModItemViewModel.UpdateAvailable"/>); or the row
    /// has no <see cref="ModItemViewModel.NexusModId"/>. The Premium install path
    /// additionally no-ops when the global coordinator is already busy (one
    /// install at a time, shared with the automatic updater).</para>
    /// <para><b>One install at a time, globally.</b> The Premium path acquires
    /// the shared <see cref="UpdateCoordinator"/>; the automatic updater acquires
    /// the same coordinator per mod, so a manual click and an automatic batch can
    /// never install the same mod concurrently. The coordinator's busy flag drives
    /// <see cref="AnyRowUpdating"/> (pushed to rows), which disables other
    /// Premium rows' actions while an install runs.</para>
    /// <para><b>Transactional extraction.</b> The mod repository's
    /// <c>AddVersion</c> extracts into a sibling temp + atomically swaps on
    /// success, so a mid-update failure leaves the existing version intact (the
    /// user keeps the version they had). On Premium-install failure the command
    /// surfaces a user-facing alert; on success (or failure) the finally block
    /// clears <see cref="ModItemViewModel.IsUpdating"/> + releases the
    /// coordinator so the row's other controls re-enable.</para>
    /// <para><b>No ConfigureAwait(false)</b> on the Premium path: the continuation
    /// must stay on the UI thread so Reload + ShowAlertAsync run on the UI thread
    /// (the UI-layer convention). The acquisition's own I/O runs on the threadpool
    /// internally; awaiting it does not block the UI thread.</para>
    /// </remarks>
    [RelayCommand]
    private async Task Update(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid profileId)
        {
            return;
        }

        // Defense: the button is only visible+enabled under these conditions,
        // but the command is the source of truth (a programmatic caller, a test,
        // or a future keystroke could bypass the view's gating).
        if (!row.IsNexusLatest || !row.UpdateAvailable || row.NexusModId is not int modId)
        {
            return;
        }

        if (IsPremiumUser)
        {
            await UpdatePremiumAsync(profileId, row, modId);
        }
        else
        {
            OpenFilesPage(row);
        }
    }

    /// <summary>
    /// The Premium install branch of the update action: acquires the global
    /// coordinator (no-op if busy), runs the acquisition, acknowledges the
    /// install, and reloads. Surfaces a user-facing alert on failure.
    /// </summary>
    private async Task UpdatePremiumAsync(Guid profileId, ModItemViewModel row, int modId)
    {
        // One install at a time, globally: a second click while another install
        // is in flight is a no-op. The coordinator is shared with the automatic
        // updater, so the two paths can never install the same mod concurrently.
        if (!_updateCoordinator.TryAcquire(out var scope))
        {
            return;
        }

        row.IsUpdating = true;
        try
        {
            // No ConfigureAwait(false): the continuation must stay on the UI
            // thread so AcknowledgeUpdateAndReload (mutates the UI-bound Mods
            // collection) + the failure-path ShowAlertAsync below run on the UI
            // thread (the UI-layer convention).
            await _acquisition.AcquireLatestNexusAsync(GameDomain, modId);

            // Acknowledge the install: remove this container's known-update entry
            // immediately (no extra API check), then reload so the new version
            // shows + the flag clears. The persisted state was the source of the
            // flag, so clearing it is enough; ApplyKnownUpdateState (inside
            // Reload) reads the cleared state.
            AcknowledgeUpdateAndReload(row.ContainerId);
            _session.HasPendingChanges = true;

            _logger.LogInformation("Updated mod {Container} to the latest Nexus release.", row.ContainerId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure: the user (or shutdown) cancelled;
            // no alert. Re-throwing would surface as an unobserved exception on
            // the fire-and-forget AsyncRelayCommand, so swallow instead.
            _logger.LogInformation("Update of mod {Container} was cancelled.", row.ContainerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update of mod {Container} failed.", row.ContainerId);
            await _dialogs.ShowAlertAsync(
                _localization["Update_FailedTitle"],
                _localization.Format("Update_FailedMessage", row.Name) + " " + ex.Message);
        }
        finally
        {
            row.IsUpdating = false;
            scope?.Dispose();
        }
    }

    /// <summary>
    /// The regular / unknown Premium branch of the update action: opens the
    /// mod's Nexus files page in the user's browser via the injectable
    /// external-launcher seam, surfacing a fallback alert on launch failure
    /// (rather than swallowing it). No install coordination is needed (this only
    /// opens a page); the user picks a file on Nexus + the registered nxm
    /// handler acquires it through the standard flow.
    /// </summary>
    private void OpenFilesPage(ModItemViewModel row)
    {
        if (row.UpdatePageUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            if (!_launchExternal(uri))
            {
                // The seam returns false on a launch failure (no default browser,
                // headless, etc.). Surface a fallback alert rather than swallowing
                // it so the user can act (the URL is included for manual copy).
                _logger.LogWarning("Opening the Nexus files page for {Container} failed.", row.ContainerId);
                _ = ShowLaunchFailedAlertAsync(row.Name, url);
            }
        }
        catch (Exception ex)
        {
            // The default launcher's exception filter is narrow; a real wiring
            // bug surfaces here as a fallback alert rather than being swallowed.
            _logger.LogError(ex, "Launching the Nexus files page for {Container} threw.", row.ContainerId);
            _ = ShowLaunchFailedAlertAsync(row.Name, url);
        }
    }

    /// <summary>
    /// Shows the localized launcher-failure alert (fire-and-forget on the UI
    /// thread; the click handler is sync). Includes the files-page URL so the
    /// user can open it manually.
    /// </summary>
    private async Task ShowLaunchFailedAlertAsync(string modName, string url)
    {
        await _dialogs.ShowAlertAsync(
            _localization["ModList_OpenFilesFailedTitle"],
            _localization.Format("ModList_OpenFilesFailedMessage", modName, url));
    }

    /// <summary>
    /// The command behind the "Add Nexus Mods" flyout item + the face button's
    /// NexusMods mode (the Add split button's default): opens the Darktide Nexus
    /// Mods games page in the user's default browser via the
    /// <see cref="_launchExternal"/> seam, surfacing a fallback alert (with the
    /// URL for manual copy) on a launch failure. Lets the user browse + download
    /// mods; nxm:// links route back into Curator when it owns the OS handler.
    /// </summary>
    [RelayCommand]
    private void AddNexusMods()
    {
        var url = NexusModsGamesUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            if (!_launchExternal(uri))
            {
                // The seam returns false on a launch failure (no default browser,
                // headless, etc.). Surface a fallback alert rather than swallowing
                // it so the user can act (the URL is included for manual copy).
                _logger.LogWarning("Opening the Nexus Mods games page failed.");
                _ = ShowNexusModsLaunchFailedAlertAsync(url);
            }
        }
        catch (Exception ex)
        {
            // The default launcher's exception filter is narrow; a real wiring
            // bug surfaces here as a fallback alert rather than being swallowed.
            _logger.LogError(ex, "Launching the Nexus Mods games page threw.");
            _ = ShowNexusModsLaunchFailedAlertAsync(url);
        }
    }

    /// <summary>
    /// Shows the localized launcher-failure alert for the Nexus Mods games page
    /// (fire-and-forget on the UI thread; the click handler is sync). Includes
    /// the URL so the user can open it manually.
    /// </summary>
    private async Task ShowNexusModsLaunchFailedAlertAsync(string url)
    {
        await _dialogs.ShowAlertAsync(
            _localization["ModList_OpenNexusModsFailedTitle"],
            _localization.Format("ModList_OpenNexusModsFailedMessage", url));
    }

    /// <summary>
    /// The default external-launcher: opens <paramref name="uri"/> via the OS
    /// shell-open (<c>Process.Start(UseShellExecute=true)</c>). Mirrors
    /// <c>DmfPromptService</c>'s launcher; the narrow exception filter
    /// (<c>Win32Exception</c>, <c>PlatformNotSupportedException</c>,
    /// <c>FileNotFoundException</c>) keeps a real wiring bug visible rather than
    /// silently swallowed. Returns false on a caught launch failure; tests
    /// inject a controllable seam.
    /// </summary>
    private static bool LaunchExternalDefault(Uri uri)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            };
            using (Process.Start(psi))
            {
            }
            return true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception
                or PlatformNotSupportedException
                or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The default path-launcher: opens the OS file manager at
    /// <paramref name="path"/> via <c>Process.Start(UseShellExecute=true)</c>.
    /// Used by the open-external-folder action on a linked row. Same narrow
    /// exception filter + return contract as <see cref="LaunchExternalDefault"/>;
    /// tests inject a controllable seam.
    /// </summary>
    private static bool LaunchExternalPathDefault(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            using (Process.Start(psi))
            {
            }
            return true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception
                or PlatformNotSupportedException
                or FileNotFoundException)
        {
            return false;
        }
    }

    // ---- auto-sort (identity stub) -----------------------------------------

    /// <summary>
    /// Applies the <see cref="IModOrderResolver"/> to the current list + persists
    /// the resolved order. With the identity stub this is a no-op (order
    /// unchanged); the real dependency-driven resolver drops in later without a
    /// UI change. <see cref="AutoSortEnabled"/> reflects the toggle state.
    /// </summary>
    [RelayCommand]
    private void AutoSort()
    {
        if (_session.ActiveProfileId is not Guid id || Mods.Count == 0)
        {
            return;
        }

        var entries = _profiles.GetModList(id);
        var order = _orderResolver.ResolveOrder(entries);
        _profiles.SetModOrder(id, order);
        _session.HasPendingChanges = true;
        Reload();
        _logger.LogDebug("Auto-sorted via {Resolver}", _orderResolver.GetType().Name);
    }

    // ---- link-external-folder helper -------------------------------------

    /// <summary>
    /// Surfaces a link-external-folder failure alert for a source path + the
    /// underlying exception, using the localized <c>Import_Failed</c> strings.
    /// Logs the exception with its stack + shows the message text to the user.
    /// (The copied local-import flow now surfaces its failures inline via
    /// <see cref="ImportWorkflowViewModel.FailureMessage"/>; this helper is used
    /// only by the linked-folder flow, which remains modal-alert-based.)
    /// </summary>
    private async Task AlertImportFailed(string path, Exception ex)
    {
        _logger.LogError(ex, "Import of {Path} failed", path);
        await _dialogs.ShowAlertAsync(
            _localization["Import_FailedTitle"],
            _localization.Format("Import_FailedMessage", path) + " " + ex.Message);
    }

    // ---- link external folder (picker only, no modal, no copy) --------------

    /// <summary>
    /// Processes a list of external folder paths from the link flow (the "Link
    /// external folder" picker), sequentially. Per path the flow mirrors the
    /// copied-import flow minus the inline workflow (the folder is linked, not
    /// copied): <b>(1)</b> peek the base folder name via
    /// <see cref="IModImportService.GetBaseName"/> (validates the mod-folder
    /// shape, throws on an invalid source); <b>(2)</b> hard-block a base-name
    /// collision against the active profile (refuse, create nothing, alert),
    /// excluding the container a re-link would dedup to (a re-link resolves to
    /// the same container, and <see cref="IProfileService.AddMod"/> is idempotent
    /// on it); <b>(3)</b> <see cref="IModImportService.LinkFolder"/> (record the
    /// metadata-only container, no copy) + <see cref="IProfileService.AddMod"/>
    /// with <see cref="ModVersionPolicy.Latest"/> (inert for linked; the external
    /// folder is the single implicit version). A failed peek, a containment /
    /// shape failure from <see cref="IModImportService.LinkFolder"/>, OR a
    /// collision cancels the whole remaining batch (folders linked earlier in the
    /// batch stay linked).
    /// </summary>
    /// <remarks>
    /// No <c>ConfigureAwait(false)</c> anywhere: dialog + observable mutations
    /// stay on the captured UI context (UI-layer convention).
    /// </remarks>
    [RelayCommand]
    private async Task LinkMods(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid id)
        {
            _logger.LogWarning("Link flow ignored: no active profile");
            return;
        }

        // Tracks whether any path actually landed a linked mod in the profile.
        // A failed-peek or all-colliding batch links nothing, so it must not set
        // the pending-changes flag (no structural change occurred).
        var changed = false;
        foreach (var path in paths)
        {
            // (1) Peek the base folder name. The picked folder IS the base; this
            // validates the mod-folder shape (a matching <base>.mod descriptor)
            // BEFORE any container is created. An invalid source throws here;
            // catch it per path, surface an alert naming the failing source, and
            // abort the remaining batch (the cancel-aborts-batch posture).
            string baseName;
            try
            {
                baseName = _importService.GetBaseName(path);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException
                    or IOException or UnauthorizedAccessException
                    or System.IO.InvalidDataException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            // (2) Base-name collision hard-block (same rule as the inline
            // import workflow's collision check). The
            // container a re-link would dedup to is excluded: a re-link resolves
            // to the same linked container (Linked identity is the normalized
            // external path), and AddMod is idempotent on it, so it must NOT be
            // treated as a collision.
            var linkedSource = new LinkedSource { ExternalPath = path };
            var existing = _importService.FindExistingContainer(linkedSource, string.Empty);
            var collision = _profiles.GetBaseNameCollision(id, baseName, existing?.Id);
            if (collision is not null)
            {
                var conflictingName = _repo.Get(collision.ContainerId)?.Name ?? baseName;
                _logger.LogWarning(
                    "Link blocked at {Path}: base folder '{Base}' collides with existing mod '{Conflicting}' (container {Container}) on profile {Id}",
                    path, baseName, conflictingName, collision.ContainerId, id);
                await _dialogs.ShowAlertAsync(
                    _localization["Import_CollisionTitle"],
                    _localization.Format("Import_CollisionMessage", path, baseName, conflictingName));
                break;
            }

            // (3) Record the linked container (metadata only, no copy) then add
            // the profile reference with LatestPolicy (inert for linked).
            Guid containerId;
            try
            {
                containerId = _importService.LinkFolder(path);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            _profiles.AddMod(id, containerId, ModVersionPolicy.Latest);
            changed = true;
            _logger.LogInformation(
                "Linked {Mod} from {Path} (policy=Latest) onto container {Container}",
                baseName, path, containerId);
        }

        if (changed)
        {
            _session.HasPendingChanges = true;
        }
        Reload();
    }

    // ---- open external folder (linked row badge click) ----------------------

    /// <summary>
    /// Opens the OS file manager at a linked row's external folder via the
    /// injectable path-launcher seam, surfacing a fallback alert on launch
    /// failure. No-op for a non-linked row, a broken row (the folder is missing),
    /// or a row whose source carries no path. The row carries state only; this
    /// command owns the launch + alert, mirroring the regular/unknown
    /// files-page open path.
    /// </summary>
    [RelayCommand]
    private async Task OpenFolder(ModItemViewModel? row)
    {
        if (row is null || row.Source is not LinkedSource || row.IsExternalBroken)
        {
            return;
        }

        var path = row.ExternalFolderPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (!_launchExternalPath(path))
            {
                _logger.LogWarning("Opening the external folder for {Container} failed.", row.ContainerId);
                await ShowOpenFolderFailedAlertAsync(row.Name, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launching the external folder for {Container} threw.", row.ContainerId);
            await ShowOpenFolderFailedAlertAsync(row.Name, path);
        }
    }

    /// <summary>
    /// Shows the localized open-folder-failure alert (the launcher seam returned
    /// false or threw). Includes the path so the user can open it manually.
    /// </summary>
    private async Task ShowOpenFolderFailedAlertAsync(string modName, string path)
    {
        await _dialogs.ShowAlertAsync(
            _localization["ModList_OpenFolderFailedTitle"],
            _localization.Format("ModList_OpenFolderFailedMessage", modName, path));
    }
}
