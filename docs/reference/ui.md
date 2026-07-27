# UI (`Modificus.Curator.UI`): reference

> The Avalonia 12 front end of Modificus Curator. Owns the shell, profile
> management, the mod list, every dialog (Settings, Preferences,
> Integrations, Manage profiles, import, discovery escape-hatch, progress, launch
> settings), global preferences (theme, font scale, language), the i18n infrastructure,
> the DMF install-prompt coordinator, the update-check runner, and the app
> self-update service. The UI never touches the filesystem or the network
> directly; every data operation flows through a backend library service.

The UI is an executable (`OutputType=WinExe`), not a library: it is the
composition root and the only project that constructs Avalonia windows. It
exposes a small set of interfaces and types so its logic stays unit-testable
and so backend libraries do not depend on it.

## The profile session

### `IProfileSession`

The single authority for "which profile is active, can it change, and is the
game running." Both the shell dropdown switch and the Manage-profiles
dialog's create-sets-active route through the same gate.

```csharp
public interface IProfileSession : INotifyPropertyChanged
{
    Guid? ActiveProfileId { get; }
    bool IsRunning { get; }
    void RequestActive(Guid id);
    bool CanDeleteProfile(Guid id);
    void ReconcileActive();
    void Refresh();
}
```

- `ActiveProfileId`: the current active profile id, or null when none is
  active. Persisted on every change. Raises `PropertyChanged` on assignment
  (so the shell and the mod list reload on a switch).
- `IsRunning`: whether Darktide is currently running. Live, refreshed by a
  polling timer (~3 s, a cheap process scan). The status strip,
  launch-availability, and the switch-block gate all read this. Raises
  `PropertyChanged` on assignment.
- `RequestActive(id)`: the sole active-change gate. Applied and persisted
  only when the game is not running; otherwise a no-op (the active stays
  put). Both the dropdown switch and the dialog's create-sets-active call
  this. Rename and delete-of-active do not (rename leaves the id stable;
  delete uses `ReconcileActive`).
- `CanDeleteProfile(id)`: whether the profile `id` may be deleted right now.
  False when `id` is the active id and the game is running; true otherwise.
  The Manage-profiles dialog binds each row's trash button to this so the
  active row's trash disables while the game runs.
- `ReconcileActive()`: recovery after CRUD that may have removed the active
  profile: if the current active id no longer exists in
  `IProfileService.ListProfiles`, clears the active id (null) and persists.
  A no-op when the active id is still present, or when no active is set
  (first run / nothing chosen). Never auto-selects a remaining profile.
- `Refresh()`: re-checks `IsRunning` against the running-state source right
  now, rather than waiting for the next polling-timer tick. Used by callers
  that just caused a state change (the shell after a successful launch) so
  the indicator and launch-availability react immediately.

### `ProfileSession`

The production implementation. `ObservableObject` (CommunityToolkit.Mvvm) so
`[ObservableProperty]` raises `PropertyChanged` for `ActiveProfileId` and
`IsRunning`. Owns:

- The active id, restored from `IAppStateStore` at startup (straight into
  the backing field; no write-back, no subscribers yet). A stale id (deleted
  while Curator was closed) resolves to no selection in the shell and is
  cleaned up lazily on the next delete-of-active reconcile rather than
  rewritten at startup. Persisted on every change via `OnActiveProfileIdChanged`.
- The can-change gate (`RequestActive`).
- The live running-state (a polling timer that calls `Refresh`).

```csharp
public sealed partial class ProfileSession : ObservableObject, IProfileSession
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    public ProfileSession(
        ISteamService steam,
        IProfileService profiles,
        IAppStateStore appState,
        Action<Action>? startTimer = null);
}
```

The polling timer is injected as a `startTimer` delegate so unit tests
construct the session without a UI dispatcher and call `Refresh` directly for
deterministic running-state changes. Production wires a `DispatcherTimer`
(`CuratorComposition.StartRunningStatePolling`). The session's own logic (gate,
persistence, fallback) has no time dependency and no Avalonia dependency.

## Dialog service

### `IDialogService`

The application's UI-dialog abstraction. Keeps view models free of direct
Avalonia `Window` construction so their logic stays unit-testable: a view
model depends on this seam, and tests inject a recording fake instead of a
real window. The production `DialogService` owns every real `Window` and
`ShowDialog` wiring.

```csharp
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowManageProfilesAsync();
    Task ShowLaunchSettingsAsync(Guid profileId);
    Task ShowPreferencesAsync();
    Task<ImportModResult?> ShowImportModAsync(ImportModRequest request);
    Task ShowSettingsAsync();
    Task ShowIntegrationsAsync();
    Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields);
    Task ShowAlertAsync(string title, string message);
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
```

- `ConfirmAsync(title, message)`: a modal confirmation. Returns true when
  the user confirms, false otherwise (cancel / dismiss). Gates destructive
  actions (profile delete, mod remove, the DMF download prompt).
- `ShowManageProfilesAsync()`: the Manage-profiles modal (create / rename /
  delete). Active changes are applied live through `IProfileSession` during
  the dialog's session, so on completion the session already reflects
  whatever the gate allowed. The caller refreshes its profile-list snapshot
  on completion. Each row carries a launch-settings action (a drawn tune
  icon) that opens `ShowLaunchSettingsAsync` for that row's profile.
- `ShowLaunchSettingsAsync(profileId)`: the per-profile launch-settings modal
  (environment variables + Darktide command-line arguments + the skip-splash and
  Lua-logging toggles), opened over the Manage-profiles dialog. Loads the
  profile's existing settings via `GetLaunchSettings`, lets the user add/remove
  env-var + game-arg rows with inline localized validation (delegated to the
  shared `LaunchSettingsValidator` from the Profiles library -- the same source
  of truth `SetLaunchSettings` uses -- so the per-field messages track the
  service rules exactly), and persists on Save through `SetLaunchSettings`
  (closing only on success). Cancel / ESC / close make no change. Editing is
  unlocked while Darktide runs (a `profile.json` write that does not touch the
  running process); changes apply on the next launch. The two toggles map to
  Relay bare flags: `SkipSplash` emits `--skip-splash` (skips Darktide's intro
  splash state) and `EnableLuaLogs` emits `--lua-logs` (tees Lua `print` output
  into the log file).
- `ShowPreferencesAsync()`: the Preferences modal (theme / font scale /
  language). Each change applies immediately through `IPreferencesService`
  (which also persists), so on completion the running app and the persisted
  config already reflect the user's choices.
- `ShowImportModAsync(request)`: the per-mod import modal (source chooser,
  conditional Version and URL), pre-filled from `request`. Returns the
  confirmed `ImportModResult` (URL parsed to canonical source) when the user
  confirms, or null when they cancel / dismiss. The mod-list add flow calls
  this once per imported path (sequentially); a null cancels the remaining
  batch.
- `ShowSettingsAsync()`: the Settings modal (discovery paths + storage
  shortcuts). Each discovery setting applies and persists immediately; the
  Storage section has two buttons that launch the OS file manager at the
  Curator data root (AppPaths.AppDataDir) and the profiles root.
- `ShowIntegrationsAsync()`: the Integrations modal (Nexus auth: OAuth
  login, API-key validate, sign-out). Nexus-only. Each auth action applies and
  persists immediately through `NexusAuthService`.
- `ShowDiscoveryEscapeHatchAsync(missingFields)`: the discovery escape-hatch
  modal, focused on the missing discovery fields the launch reported. Inputs
  are shown only for the fields in `missingFields`. Returns true when the
  user submitted (the entered paths are now persisted), false when they
  cancelled (no writes). No auto-retry: the caller does not re-launch on a
  true return; the user clicks Launch again.
- `ShowAlertAsync(title, message)`: a simple modal alert (a single OK
  button, no cancel). Used to surface a launch `Error`, a download failure,
  or the DMF informational case where there is nothing for the user to
  decide, only acknowledge.
- `ShowProgressAsync<T>(title, message, work)`: a buttonless, non-closeable
  modal spinner over the supplied async work. The user cannot dismiss the
  spinner: the work runs to completion and the caller surfaces its result.
  The work's exception (if any) propagates to the caller; the spinner is
  closed in either case. Used for the DMF in-app download.

### Import-modal payload types

```csharp
public sealed record ImportModRequest
{
    public string ModName { get; set; }    // pre-filled; two-way bound so the
                                           // user's rename-at-import flows back
    public string SourcePath { get; set; } // absolute path to a folder OR archive

    public ImportModRequest(string modName, string sourcePath);
}

public sealed record ImportModResult(
    ModSource Source,           // parsed canonical source (URL resolved to identity)
    string Version,             // raw release tag; string.Empty for untracked
    ModVersionPolicy Policy);   // Latest (default) or Pinned (carries an empty
                                // VersionId here; the add flow substitutes the
                                // id returned by IModImportService.Import)
```

`ModSource`, `ModVersionPolicy`, and `LatestPolicy` / `PinnedPolicy` live in
the [mods](mods.md) library; the UI consumes them.

## Preferences service

### `IPreferencesService`

The single authority for applying user-facing preferences (theme, font scale,
language) to the running app and persisting them to `CuratorConfig`.

```csharp
public interface IPreferencesService
{
    void ApplyAndPersist(ThemeMode theme, double fontScale, string language);
}
```

`ApplyAndPersist` applies the theme via `Application.RequestedThemeVariant`,
the font scale via application-level `AppFontSize` + `AppStatusFontSize`
resources (cascading to all controls through inheritance and `DynamicResource`),
and the language via `LocalizationService.SetCulture`. It then persists all
three to the config file via a read-modify-save through `IConfigLoader`. Safe
to call at startup (the values may match the loaded config, which is a
no-op apply).

`ThemeMode` and `PreferencesConfig` live in the [config](config.md) library.

### `PreferencesService`

```csharp
public sealed class PreferencesService : IPreferencesService
{
    public const double BaseFontSize = 14.0;        // AppFontSize base, px
    public const double BaseStatusFontSize = 12.0;  // AppStatusFontSize base, px
}
```

The font scale is applied as `BaseFontSize * scale` (and
`BaseStatusFontSize * scale`). The `AppFontSize` resource is read by the
Window style in `App.axaml` (`Window.FontSize` binds to it via
`DynamicResource`), so all open windows and their inheriting children
re-resolve when the resource changes; `MainWindow`'s status `TextBlock` binds
to `AppStatusFontSize`. Both use the same scale so the status strip grows
with the body. A non-finite or non-positive scale falls back to 1.0.

## Localization

### `LocalizationService`

The single authority for resolving localized strings at runtime. A singleton
(registered in DI) that holds the current UI culture, exposes a string
indexer used by every XAML binding, and raises `PropertyChanged` so bindings
refresh live when the culture changes.

```csharp
public sealed class LocalizationService : INotifyPropertyChanged
{
    public LocalizationService();   // over "Modificus.Curator.UI.Resources.Strings"

    CultureInfo Culture { get; set; }      // assigning raises PropertyChanged for
                                           // "Item[]" + "Culture"
    void SetCulture(string name);          // empty / unknown -> invariant

    string this[string key] { get; }       // missing key -> the key itself
    string Format(string key, params object[] args);  // string.Format(culture, ...)
}
```

- `Culture`: the current UI culture. Assigning a different culture raises
  `PropertyChanged` for `"Item[]"` (the indexer wildcard, so every
  `{Binding [Key], Source={StaticResource Loc}}` re-evaluates and the whole
  UI refreshes) and for `Culture` itself. Unknown or null names keep the
  current culture (graceful: a missing translation file does not crash the
  UI).
- `SetCulture(name)`: sets the culture by name (e.g. `"en"`, `"fr"`). An
  empty or unknown name resolves to `CultureInfo.InvariantCulture` (the
  neutral resx). A name that parses but matches the current culture is a
  no-op.
- `this[key]`: resolves `key` for the current culture via the
  `ResourceManager`. A missing key returns the key itself (visible, never
  throws).
- `Format(key, args)`: resolves `key` for the current culture and applies
  `string.Format(IFormatProvider, string, object[])` with the supplied args
  (using the current culture for any number or date formatting). Used for
  parameterized messages (e.g. the delete confirmation: `"Delete profile
  {0}?…"`).

The service holds its own culture and resolves strings with it directly. It
does not mutate the thread's `CurrentUICulture` (avoiding surprising global
side effects); only the UI text follows the chosen language. The neutral
`Strings.resx` lives at `src/ui/Resources/Strings.resx` under
the default namespace `Modificus.Curator.UI`.

`App.OnFrameworkInitializationCompleted` swaps the XAML resource placeholder
for the real DI singleton (`Resources["Loc"] = localization`), so every
view's `{Binding [Key], Source={StaticResource Loc}}` resolves through the
live service. The XAML uses `{ReflectionBinding [Key], Source=...}` rather
than a compiled binding because the indexer-based dynamic-language path is
not expressible as a compiled binding.

## The first-run Welcome onboarding

### `OnboardingService`

The first-run Welcome coordinator. Shows the Welcome modal once, the first time
the app starts with `IAppStateStore.OnboardingCompleted` still `false`, persists
completion, and opens the shell's Integrations flow on a "Set up Nexus" choice.
After the first run, the call is a no-op for the lifetime of the process.

```csharp
public sealed class OnboardingService
{
    public OnboardingService(
        IAppStateStore appState,
        IDialogService dialogs,
        Func<Task> openIntegrations,   // resolves to ShellViewModel.OpenIntegrationsAsync
        ILogger<OnboardingService> logger);

    public Task ShowWelcomeIfFirstRunAsync();
}
```

- `ShowWelcomeIfFirstRunAsync()`: one-shot. Reads the persisted
  `OnboardingCompleted` flag (plus an in-process guard) and no-ops when already
  complete; otherwise shows the Welcome dialog, persists completion BEFORE any
  further UI (so closing the subsequent Integrations dialog can never cause
  Welcome to repeat), and on a `WelcomeChoice.SetUpNexus` choice opens the
  shell's full Integrations flow via the injected `openIntegrations` delegate.
- `openIntegrations`: resolved lazily through `ShellViewModel.OpenIntegrationsAsync`
  at composition, so the nxm handler status refresh applies after the
  Welcome-driven Integrations dialog too. Kept as a delegate so the coordinator
  stays unit-testable.
- `WelcomeChoice`: the typed result returned through
  `IDialogService.ShowWelcomeAsync`. `Continue` (the default; also ESC, the
  title-bar close button, and a window close) persists completion and leaves the
  user at the main window; `SetUpNexus` persists completion then opens
  Integrations.

The App wires the call after the main window is actually opened (Avalonia modal
dialogs require a shown owner): a one-shot `Opened` handler resolves the
coordinator and fires the call; a failure is logged and swallowed so it never
crashes startup.

## The DMF prompt coordinator

### `DmfPromptService`

The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator.
Records the trigger from `IProfileService.ProfileCreated` (which fires from
inside the Manage-profiles dialog) and processes it when the shell calls
`ProcessPendingAsync` after the triggering dialog closes, so the DMF prompt is
the topmost modal at that point (no dialog-on-dialog).

```csharp
public sealed class DmfPromptService
{
    public const int DmfModId = 8;     // Nexus mod id of Darktide Mod Framework

    public DmfPromptService(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<DmfPromptService> logger,
        Func<Uri, bool>? launchExternal = null);

    public Task ProcessPendingAsync();
}
```

- `DmfModId`: the Nexus mod id of Darktide Mod Framework (8). DMF is
  required for most Darktide mods; the prompt offers to install it when
  missing.
- `ProcessPendingAsync()`: processes any pending new-profile trigger. Called by
  the shell after the Manage-profiles dialog closes. Safe to call when nothing
  is pending (a no-op). The trigger is consumed (cleared) before it is
  processed so an exception in the prompt does not leave it stuck pending for
  the next call; the prompt is wrapped in a try/catch that logs and swallows
  non-cancellation exceptions.

### Trigger + cases

One trigger fires when DMF is not in the active profile:

1. **New profile becomes active** (a fresh ask per profile; no persisted
   flag). A profile created while Darktide runs does not become active (the
   session gates it), so no prompt fires in that case.

Two cases on a trigger:

1. DMF in the repo but not in the profile: a Yes/No confirm. On Yes,
   `IProfileService.AddMod` adds it instantly.
2. DMF not in the repo: a Yes/No confirm (the message tailors to whether Curator
   owns the `nxm://` handler: the manager-download path when it does,
   manual-import guidance when it does not). On Yes, premium users get the
   in-app API download under a modal spinner (via
   `IDialogService.ShowProgressAsync` plus
   `IModAcquisitionService.AcquireLatestNexusAsync`); everyone else (no auth,
   regular, or unknown premium state) gets the DMF Nexus files page
   (`https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files`) opened in
   the default browser via the OS shell-open (`UseShellExecute = true`),
   regardless of `nxm://` setup. When Curator owns the handler the user clicks
   Download on the page and the handler picks up the URL; otherwise the user
   downloads the archive and imports it via the normal add flow. On a
   browser-open failure, a fallback alert carries the files-page URL.

Decline is respected: nothing opens, no Integrations prompt. The DMF flow never
opens the Integrations dialog; the one-time Nexus setup offer lives in the
first-run Welcome flow.

`launchExternal` is injectable so tests exercise the browser-open failure
path without launching a real browser. The default uses `Process.Start` with
`UseShellExecute = true`; the exception filter is narrow
(`Win32Exception`, `PlatformNotSupportedException`,
`FileNotFoundException`) so a real wiring bug is not silently swallowed.

## The update check runner

### `UpdateCheckRunner`

The UI-layer glue between `IProfileSession` (the active-profile authority)
and `IUpdateCheckService` (the Integrations update check). The check itself
is backend-only; this runner owns when the UI fires it. After each check
completes, the runner captures the exact result (not a potentially raced
`LastResult`) and chains the `IAutomaticUpdateService` (the opt-in Premium
automatic installer) on the captured UI context, so a manual CheckNow keeps
its spinner active through the installations. The check flags mods via three
tiers (the server's `viewerUpdateAvailable`, a mod-level version compare, and a
latest-file-version confirmation that clears tier-2 false positives against the
actual latest file); see
[the update-detection tiers](rate-limiting-strategy.md#update-detection-tiers).

```csharp
public sealed class UpdateCheckRunner
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    public UpdateCheckRunner(
        IProfileSession session,
        IUpdateCheckService updateCheck,
        IConfigLoader configLoader,
        IAppStateStore appState,
        IAutomaticUpdateService autoUpdate,
        ILogger<UpdateCheckRunner> logger,
        Action<Action>? startTimer = null,
        Func<DateTimeOffset>? getNow = null);

    public DateTimeOffset? NextManualRefreshAllowedAt { get; }

    public void Start();
    public Task CheckNowAsync();
}
```

- `TickInterval`: the periodic timer's fixed tick granularity (1 minute).
  The user-configured interval
  (`CuratorConfig.Nexus.AutoUpdateCheckIntervalMinutes`) is honored to this
  granularity: the runner fires when that much time has elapsed since the
  last check, checked on each tick.
- `Start()`: seeds the last-check timestamp
  (`IAppStateStore.LastUpdateCheckUtc`) and the manual throttle's sliding window
  (`IAppStateStore.ManualRefreshTimestamps`) from the persisted store,
  subscribes to the session's active-profile changes, starts the periodic tick,
  and fires an opening check only when a profile was already restored at startup
  AND the configured interval has elapsed. Called once from the composition root
  after the provider is built (best-effort: failures are logged and swallowed,
  never blocking startup).
- `CheckNowAsync()`: the manual "check now" trigger (the mod-list header
  refresh button). Fires an immediate thorough check for the active profile
  (`IUpdateCheckService.CheckThoroughAsync`, the per-mod pass that also
  catches mods outside the Month window). Awaitable so the caller (the list
  VM's `CheckForUpdatesNow` command) can drive an `IsCheckingNow` affordance
  while it runs. No-op (returns `Task.CompletedTask`) when no profile is
  active. Bypasses the interval gate but carries its own sliding-window
  throttle (10 free refreshes per rolling hour, then one per 2 minutes); a
  throttled attempt is a silent no-op (no API call, no timestamp stamp). The
  list VM reads `NextManualRefreshAllowedAt` for the countdown tooltip and the
  disabled button. Resets the shared periodic clock.

The four triggers:

| Trigger | Check shape | Awaited? | Gated? |
| --- | --- | --- | --- |
| Startup (restored active id) | Month-only `CheckAsync` | no (fire-and-forget) | yes (interval) |
| Active-profile switch | Month-only `CheckAsync` | no (fire-and-forget) | yes (interval) |
| Periodic timer | Month-only `CheckAsync` | no (fire-and-forget) | yes (toggle + interval) |
| Manual "check now" | Thorough `CheckThoroughAsync` | yes (caller drives spinner) | yes (sliding window) |

Every automatic trigger (startup, switch, and periodic) is interval-gated: a
check fires only when the configured interval has elapsed since the last check
of any kind. The `AutoUpdateCheckEnabled` toggle gates only the periodic timer;
startup and switch fire regardless of the toggle (when the interval has
elapsed), and `CheckNowAsync` always fires (it is user-initiated and bypasses
the interval gate). The toggle and interval are read live on each tick so a
runtime change in the Integrations dialog takes effect without a restart.

The last-check timestamp is persisted to `app-state.json`
(`IAppStateStore.LastUpdateCheckUtc`) and seeded at `Start()`, so the interval
gate survives a close/reopen: a check that fired moments ago in a prior session
suppresses this session's opening check, and a rapid open/close loop does not
fire a call per launch. Every fire (automatic or manual) re-stamps the
timestamp, so a manual or profile-load check also resets the periodic clock (no
double-fire right after a switch).

The manual "check now" path layers its own sliding-window throttle on top of
(independent of) the interval gate: the first 10 manual refreshes in a rolling
1-hour window fire freely, then the path throttles to one per 2 minutes until
timestamps age out of the window and free mode resumes. A blocked attempt is a
silent no-op (no API call, no timestamp stamp). The list VM reads
`NextManualRefreshAllowedAt` on every manual attempt and on each 1-second
countdown tick to drive the disabled button and the `m:ss` countdown tooltip
("Rate limiting protection enabled. Manual refresh will be available again in
{time}."). The window persists across restarts via `app-state.json`
(`IAppStateStore.ManualRefreshTimestamps`), seeded at `Start()` and written back
on every successful fire, so closing and reopening the app does not reset the
free-refresh budget. See
[the rate-limiting strategy](rate-limiting-strategy.md) for the thresholds.

The runner never blocks on a check beyond the await the manual trigger opts
into, never surfaces its result (the mod list reads
`IUpdateCheckService.LastResult` and subscribes to `CheckCompleted`), and
never lets an unobserved exception escape the threadpool task. When a result
carries `NamesChanged` (the check renamed at least one Nexus container to its
current Nexus name, piggybacking on the batch query at no extra API cost), the
mod list refreshes each affected row's displayed name from the repository in
place, without a full reload. A
fire-and-forget `Task` whose only awaited operation throws must not surface
that as an unobserved exception; `OperationCanceledException` is swallowed
silently, anything else is logged.

The timer and the clock are injected (`startTimer`, `getNow`) so tests drive
time deterministically. Production wires a `DispatcherTimer` and
`DateTimeOffset.UtcNow`. The runner lives in the UI assembly (mirrors
`NxmModDownloadHandler`): it observes a UI-layer singleton
(`IProfileSession`) and drives an Integrations service, so it belongs on the
consumer side of that boundary.

## The app self-update service

### `IAppUpdateService`

Curator's own self-update in Velopack-packaged builds (the Windows installer
and Linux AppImage). The
shape mirrors `IUpdateCheckService`: a best-effort availability check that
never throws to the caller for non-cancellation failures, plus a
state-holding `LastCheckResult` / `UpdatePendingRestart` surface published
under a lock together with the `UpdateStateChanged` event. The download and
apply steps are user-initiated and DO surface their failures (a checksum
mismatch or a locked-file error is something the user needs to see), so they
propagate from those two methods. See
[app auto-update architecture](../architecture/app-auto-update.md) for the
packaged-build scope, the update source, and the lifecycle interaction.

```csharp
public sealed record AppUpdateInfo(string TargetVersion, string? Notes);

public interface IAppUpdateService
{
    bool IsUpdateSupported { get; }
    string? CurrentVersion { get; }
    AppUpdateInfo? LastCheckResult { get; }
    AppUpdateInfo? UpdatePendingRestart { get; }
    event EventHandler? UpdateStateChanged;

    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);
    Task DownloadUpdatesAsync(CancellationToken ct = default);
    void ApplyUpdatesAndRestart();
}
```

- `AppUpdateInfo`: a plain data record exposing no Velopack types, so the UI
  consumes it without a hard dependency on the update engine. `TargetVersion`
  is the available update's version string; `Notes` is the target version's
  release notes, or `null` (currently empty until `vpk pack` is given
  `--releaseNotes`).
- `IsUpdateSupported`: `true` only when the running app is a Velopack install
  and the update manager initialized. The UI gates the entire update surface
  (the shell notice, the Settings controls, apply) on this, so a non-Velopack
  build (standalone Linux, portable Windows, or a dev run) shows nothing.
- `CurrentVersion`: the installed app version as a string
  (`UpdateManager.CurrentVersion.ToString()`), or `null` when unsupported. The
  UI shows it alongside `AppUpdateInfo.TargetVersion`.
- `LastCheckResult`: the most recent check result, or `null` before the first
  check, when no update was found, when self-update is unsupported, or when a
  check failed (a failure leaves the prior value untouched). Written under the
  state lock together with the `UpdateStateChanged` invocation; read lock-free.
- `UpdatePendingRestart`: the update that has been downloaded and is waiting
  for the next restart, or `null` until a download succeeds. Set by
  `DownloadUpdatesAsync`; consumed by `ApplyUpdatesAndRestart`.
- `UpdateStateChanged`: raised on the completing thread when `LastCheckResult`
  or `UpdatePendingRestart` changes. Never raised on a swallowed check
  failure. Handlers marshal to the UI thread via the shared `Action<Action>`
  seam.
- `CheckForUpdatesAsync`: returns the available update, or `null` when
  unsupported, no update is available, or the check failed. Never throws for
  non-cancellation failures; `OperationCanceledException` propagates.
- `DownloadUpdatesAsync`: downloads the update the last check resolved,
  staging it for apply. Propagates its failures (the download is
  user-initiated). `InvalidOperationException` when no check resolved an
  update (a wiring mistake, since the UI gates the download).
- `ApplyUpdatesAndRestart`: exits the process, applies the staged update, and
  relaunches under the new version. A no-op when no update has been
  downloaded.

### Conditional implementation

Two implementations live behind the one interface, selected at compile time by
the `CURATOR_VELOPACK` symbol (defined when `CuratorUseVelopack=true` is set at
publish time for the Windows installer or Linux AppImage):

- **`VelopackAppUpdateService`** (`#if CURATOR_VELOPACK`): the real impl. Wraps
  a Velopack `UpdateManager` whose source is config-driven: the constructor
  reads `CuratorConfig.AppUpdates.SourceOverride` once via the injected
  `IConfigLoader` (the same pattern every other service uses; `UpdateManager` is
  built once with its source, so the value is not held beyond the constructor).
  `null`/whitespace (the default) builds the production anonymous `GithubSource`
  (`Velopack.Sources` namespace; repo
  `https://github.com/ModifAmorphic/darktide-modificus-curator`,
  `accessToken: null`, `prerelease: true`); a set value (a local directory path
  or a URL) builds the manager from `UpdateManager`'s `urlOrPath` overload
  instead, the local-testing / self-hosted-feed path with no code change.
  Construction catches `Velopack.Exceptions.NotInstalledException` (the expected
  throw for a non-Velopack run) and leaves the manager `null`, so
  `IsUpdateSupported` is `false`.
- **`NoopAppUpdateService`**: the default, registered everywhere else. Every
  member returns the neutral value; `UpdateStateChanged` is never raised;
  `DownloadUpdatesAsync` throws `NotSupportedException` rather than silently
  no-op-ing (reaching the download path in an unsupported build is a wiring
  mistake).

### `AppUpdateCheckRunner`

The UI-layer glue that fires one Curator self-update availability check on
startup, fire-and-forget, against `IAppUpdateService`. Unlike
`UpdateCheckRunner`, app updates are profile-independent: this class has no
profile dependency and no periodic timer. The manual check (the Settings
"Check for Updates" button) calls `CheckForUpdatesAsync` directly, so it always
works regardless of the `CheckOnStartup` toggle.

```csharp
public sealed class AppUpdateCheckRunner
{
    public AppUpdateCheckRunner(
        IAppUpdateService appUpdate,
        IConfigLoader configLoader,
        ILogger<AppUpdateCheckRunner> logger);

    public void Start();
}
```

- `Start()`: reads `CuratorConfig.AppUpdates.CheckOnStartup` live. When it is
  on (the default), fires one check on a thread-pool task and discards the
  returned `Task`. When it is off, logs an informational line and returns
  without firing the check (the manual check path is unaffected). Called once
  from the composition root after the provider is built (best-effort: failures
  are logged and swallowed, never blocking startup). The result lands through
  `IAppUpdateService.UpdateStateChanged`; the runner itself surfaces nothing.

The toggle gates ONLY the automatic startup check. When it is off, no startup
check runs and the status-strip update notice is suppressed entirely: the
notice's visibility (`ShellViewModel.ShowAppUpdateNotice`) is itself gated on
`CheckOnStartup`, so even a manual check that populates `LastCheckResult`
cannot surface it (the manual Settings check is the only remaining path and is
self-contained, with its own inline result plus a Download-and-Restart button).
The shell re-reads the toggle when Settings closes so the notice tracks a
runtime toggle without a restart. The toggle is surfaced in the Settings
Updates section (read-modify-save, no caching).

The runner never blocks on the check, never surfaces its result, and never
lets an unobserved exception escape the threadpool task.
`CheckForUpdatesAsync` is documented to swallow its own non-cancellation
failures; the runner wraps the call in its own try/catch as belt-and-suspenders
(`OperationCanceledException` swallowed silently, anything else logged).
`ConfigureAwait(false)` is used only inside its `Task.Run` block, the narrow
documented exception to the UI-layer rule for explicit background-task code.

## The update coordinator + automatic-update service

### `UpdateCoordinator`

Coordinates mod-update installs so only one runs at a time globally, shared
between the manual per-row update action (`ModListViewModel`'s Update command)
and the automatic Premium updater (`IAutomaticUpdateService`). Keeps a manual
click and an automatic batch from installing the same mod concurrently without
relying on per-VM flags.

```csharp
public sealed class UpdateCoordinator
{
    public bool IsBusy { get; }
    public event EventHandler? BusyChanged;

    public bool TryAcquire(out IDisposable? scope);   // non-blocking (manual path)
    public Task<IDisposable> AcquireAsync(CancellationToken ct = default); // awaiting (auto path)
}
```

- `IsBusy`: flips on acquire + release and raises `BusyChanged` (on the
  acquiring/releasing thread). `ModListViewModel` subscribes, marshals to the
  UI thread, and pushes the flag down to each row so the per-row enabled state
  reflects "one install at a time" without each row polling.
- `TryAcquire`: non-blocking. The manual path uses it; a second click while an
  install runs is a clean no-op.
- `AcquireAsync`: awaiting. The automatic batch uses it per mod; the runner
  serializes the batch, so this is uncontended in practice, but the coordinator
  is the single mutual-exclusion point across both paths.

### `IAutomaticUpdateService`

The opt-in Premium automatic mod-update installer. Chained directly from
`UpdateCheckRunner` after a check completes (the runner captures the exact
result, not a potentially raced `LastResult`), it sequentially installs flagged
updates for the active profile's Nexus Latest mods when the user has enabled it
AND a fresh Premium verification passes. Independent of
`ModListViewModel` (to avoid the existing ModListViewModel -> UpdateCheckRunner
dependency becoming circular) and shares the `UpdateCoordinator` with the manual
update action.

```csharp
public interface IAutomaticUpdateService
{
    event EventHandler? UpdatesApplied;
    event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;
    Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default);
}

public sealed record ModUpdateProgressEventArgs(Guid ContainerId, bool IsActive);
```

- `RunAfterCheckAsync`: gates on the result's outcome being authoritative
  `Success` with updates, `NexusConfig.AutomaticUpdatesEnabled` being on, the
  active profile still matching, and a fresh `GetCurrentStateAsync` returning
  `IsPremium == true` (the Premium request fires ONLY when the gates pass, so
  an empty result or a disabled setting costs no extra API call). Then installs
  sequentially, one at a time under the coordinator. Per-mod revalidation gates
  each entry (membership / policy / source / version still match); a profile
  switch stops the whole batch; per-mod failures are isolated. A successful
  install acknowledges/clears its known-update entry immediately. A batch with
  failures surfaces one aggregated localized alert; a fully successful batch is
  silent beyond the per-mod progress indication. `UpdatesApplied` is raised when
  at least one install succeeded so `ModListViewModel` can reload the list (new
  versions + cleared flags) without the service depending on it.
- `UpdatesApplied`: raised (on the caller's thread) when at least one install in
  the last batch succeeded. `ModListViewModel` subscribes and reloads.
- `ModUpdateProgress`: raised per mod (on the caller's thread) with
  `IsActive == true` immediately before the acquisition attempt and
  `IsActive == false` from the per-mod finally block (success, failure, or
  cancellation). Deterministic start/stop ordering per sequential item.
  `ModListViewModel` subscribes, marshals to the UI thread, finds the row by
  `ContainerId`, and sets its `IsUpdating` so the row-level spinner (left of the
  Nexus badge) tracks the currently installing mod. An event for a row no longer
  present (after a profile switch / reload) is ignored, so a mid-batch switch
  never leaves a stale spinner.

This is independent of `NexusConfig.AutoUpdateCheckEnabled`: periodic checking
being off never disables automatic installation (startup + switch + manual
checks still drive it), and changing the periodic-check toggle never clears a
configured `true` here.

## Behaviors

Plain attached properties (no `Avalonia.Xaml.Interactivity` dependency) under
`Modificus.Curator.UI.Behaviors`. Each is opt-in: set its `IsEnabled`
attached property on the target control in XAML.

### `EscapeClosesBehavior`

The standard desktop "ESC dismisses the topmost modal" convention. When
`IsEnabled="True"` on a `Window`, pressing ESC calls `Window.Close()` (the same
path the shared `DialogTitleBar` close button takes), so a dialog's
result/cancel contracts are unchanged, ESC is equivalent to clicking the
title-bar X, and the key is marked handled so nothing else runs after the
close. Other keys are ignored.

```csharp
public static class EscapeClosesBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty;
    public static bool GetIsEnabled(Window element);
    public static void SetIsEnabled(Window element, bool value);
    internal static bool ShouldClose(Key key);   // pure: Key.Escape -> true
}
```

Applied to the eight closeable modal dialogs: `ConfirmDialog`,
`ImportModDialog`, `DiscoveryEscapeHatchDialog`, `IntegrationsWindow`,
`LaunchSettingsWindow`, `ManageProfilesWindow`, `PreferencesWindow`,
`SettingsWindow`. `ProgressDialog`
(non-closeable by design, `DialogTitleBar.ShowClose="False"`) and the main
window do not opt in, so ESC never dismisses a spinner or exits the app. ESC
bubbles from focused children (TextBox, ComboBox) to the window; the
Manage-profiles inline-edit boxes (`EditBox_KeyDown` / `AddBox_KeyDown`) mark
ESC handled to cancel the in-flight edit first, so a second ESC is what closes
the dialog while editing.

The key decision is factored into the `internal static ShouldClose` pure helper
so it is unit-testable without rendering a window; the KeyDown-to-Close wiring
is rendered UI and covered by code inspection, not a rendered-control test.

### `FocusOnVisible`

When `IsEnabled="True"` on a `TextBox`, focuses it and selects all its text the
moment it becomes visible. Used by the Manage-profiles editable list so the
inline rename and "+ New profile" entry boxes grab focus on appearance.

## Avalonia app + explicit X11 desktop identity

`Program.BuildAvaloniaApp` configures the Avalonia `AppBuilder` with the
standard `UsePlatformDetect` + `LogToTrace` setup, plus an explicit X11 desktop
identity via `AppBuilder.With`:

```csharp
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(DesktopIdentityOptions.Build())
            .LogToTrace(LogEventLevel.Warning);
}

internal static class DesktopIdentityOptions
{
    internal const string WmClass = "ModifAmorphic.ModificusCurator";
    internal static X11PlatformOptions Build() => new() { WmClass = WmClass };
}
```

`DesktopIdentityOptions.WmClass` is the single C# runtime constant for Curator's
explicit X11 `WM_CLASS`. It is deliberately coupled to (must stay equal to) the
Velopack pack id (`ModifAmorphic.ModificusCurator`), the `StartupWMClass` the
release pipeline bakes into the generated AppImage desktop file, and the
`StartupWMClass` `scripts/install.sh` writes into the user desktop
entry; the AppImage packaging smoke (`curator-build.yml`) and the installer test
harness (`scripts/tests/test-install.sh`) assert that coupling from the
packaging side, and this constant is the C# side. Avalonia 12's default
`WmClass` is the entry-assembly name; setting it explicitly means a task manager
groups the Curator window under Curator (and not, in particular, under Darktide
when Curator launched Darktide from its AppImage). `AppBuilder.With<T>` binds the
options before platform initialization; the platform reads `WmClass` only when an
X11 window is created.

The factory is factored separately so a unit test can read the configured value
without starting X11 or requiring `DISPLAY`. Production binds the factory's
result via `AppBuilder.With`, the normal app identity rather than a runtime
heuristic.

## DI registration

The composition root is `src/ui/CuratorComposition.cs` (a static
`Build()` that returns the application `IServiceProvider`). It runs: config
load, logger build, every backend `Add<Library>()` extension, then the UI
surface, then the startup prune, startup discovery, the nxm IPC server bind,
the OS scheme-handler registration, and the update-check runner start. The
UI registers its own surface after the backend libraries:

```csharp
// Singletons: one shell, one list, one dialog service, one session.
services.AddSingleton<IProfileSession>(sp => new ProfileSession(
    sp.GetRequiredService<ISteamService>(),
    sp.GetRequiredService<IProfileService>(),
    sp.GetRequiredService<IAppStateStore>(),
    StartRunningStatePolling));                 // DispatcherTimer, 3s
services.AddSingleton<LocalizationService>();
services.AddSingleton<IPreferencesService, PreferencesService>();
services.AddSingleton<MainWindow>();
services.AddSingleton<Action<Action>>(_ => action => Dispatcher.UIThread.Post(action));
services.AddSingleton<UpdateCoordinator>();                 // one-install-at-a-time gate
services.AddSingleton<IAutomaticUpdateService, AutomaticUpdateService>(); // Premium auto-installer
services.AddSingleton<ModListViewModel>();
services.AddSingleton(sp => new ShellViewModel(/* … incl. IAppUpdateService, Action<Action> */,
                                              sp.GetService<INxmHandlerRegistrar>()));
services.AddSingleton<IDialogService>(sp => new DialogService(/* … incl. IAppUpdateService, Action<Action>,
                                                               sp.GetService<INxmHandlerRegistrar>() */));
services.AddSingleton(sp => new UpdateCheckRunner(/* … incl. IAutomaticUpdateService, StartUpdateCheckPolling */));
#if CURATOR_VELOPACK
services.AddSingleton<IAppUpdateService>(sp => new VelopackAppUpdateService(
    sp.GetRequiredService<IConfigLoader>(),
    sp.GetRequiredService<ILogger<VelopackAppUpdateService>>()));
#else
services.AddSingleton<IAppUpdateService, NoopAppUpdateService>();
#endif
services.AddSingleton(sp => new AppUpdateCheckRunner(/* IAppUpdateService, IConfigLoader, logger */));
services.AddSingleton(sp => new DmfPromptService(/* … no IConfigLoader */, sp.GetService<INxmHandlerRegistrar>()));
services.AddSingleton(sp => new OnboardingService(
    sp.GetRequiredService<IAppStateStore>(),
    sp.GetRequiredService<IDialogService>(),
    () => sp.GetRequiredService<ShellViewModel>().OpenIntegrationsAsync(),
    sp.GetRequiredService<ILogger<OnboardingService>>()));
```

Key wiring notes:

- `IProfileSession` is registered with a factory that injects the polling
  timer (`StartRunningStatePolling` constructs a `DispatcherTimer` at
  `ProfileSession.PollInterval`). The session is shared by the shell, the
  Manage-profiles dialog, the update-check runner, and the DMF prompt
  coordinator.
- `ShellViewModel`, `DialogService`, and `DmfPromptService` resolve the
  `INxmHandlerRegistrar` via `GetService` (null on platforms without a
  registrar) so the shell status strip, the Integrations "Nexus download
  links" section, and the DMF download-confirm message can query/toggle the OS
  `nxm://` handler without forcing activation to fail on unsupported
  platforms. The composition root no longer auto-registers the handler.
- `OnboardingService` resolves `ShellViewModel.OpenIntegrationsAsync` lazily
  through its `openIntegrations` delegate, so the first-run Welcome "Set up
  Nexus" choice reuses the shell's full Integrations flow (including the nxm
  handler status refresh after the dialog closes).
- `MainWindow` is a singleton: the desktop lifetime installs the resolved
  instance as `desktop.MainWindow`, and `DialogService` resolves the same
  instance as the owner for modal dialogs.
- `Action<Action>` is registered as a factory that posts to
  `Dispatcher.UIThread`. `ModListViewModel` injects it as its `invokeOnUi`
  seam so the `CheckCompleted` handler (which fires on a threadpool thread)
  marshals its `Mods` collection iteration to the UI thread. `ShellViewModel`
  and `SettingsViewModel` use the same seam for their
  `IAppUpdateService.UpdateStateChanged` handlers.
- `IAppUpdateService` is registered conditionally on `CURATOR_VELOPACK`: the
  packaged Windows installer and Linux AppImage get
  `VelopackAppUpdateService`; every other build (standalone Linux, portable
  Windows, or a dev run without `CuratorUseVelopack=true`) gets
  `NoopAppUpdateService`. Consumers talk to `IAppUpdateService` unconditionally
  and gate their affordances on `IsUpdateSupported`.
- After `StartNxmServer` establishes single-instance ownership, the composition
  root calls `INxmHandlerRegistrar.MaintainRegistration()` best-effort. This
  refreshes an already-owned Linux AppImage handler copy and symlink, but never
  registers or takes ownership. A fatal single-instance exception bypasses the
  maintenance call; a degraded pipe bind does not.
- `INxmModDownloadHandler` is registered AFTER `AddNxm()` with a factory
  that resolves its dependencies lazily at first use (the handler is first
  resolved by the IPC router, by which point all dependencies are
  registered). MS DI resolves the last registration for an interface, so
  this supersedes the no-op default registered inside `AddNxm()`. See
  [nxm reference](nxm.md) + [mod acquisition](../architecture/mod-acquisition.md).
- `UpdateCheckRunner.Start()` and `AppUpdateCheckRunner.Start()` are called
  after the provider is built (best-effort; a wiring failure is logged and
  swallowed, never blocks startup).

`App.OnFrameworkInitializationCompleted` runs `CuratorComposition.Build()`,
applies the user's preferences before any window shows (so the first paint
already reflects them), swaps the XAML resource placeholder for the real
`LocalizationService` singleton, installs the resolved `MainWindow` as the
desktop lifetime's main window, and sets its `DataContext` to the resolved
`ShellViewModel`. A `NxmSingleInstanceException` from `Build()` (single
instance violation) propagates out; `App` catches it and calls
`Environment.Exit(1)` before any window shows.

## Dependencies

- **Curator libraries:** `config` (`CuratorConfig`, `PreferencesConfig`,
  `ThemeMode`, `NexusConfig`, `DiscoveryConfig`), `general` (`IConfigLoader`,
  `IAppStateStore`, `LoggingBootstrap`), `profiles` (`IProfileService`,
  `ProfileSummary`, `ModListEntry`, `IModOrderResolver`), `mods`
  (`IModRepository`, `IModImportService`, `ModContainer`, `ModVersion`,
  `ModVersionPolicy`, `ModSource`, `NexusSource`, `UntrackedSource`,
  `LinkedSource`),
  `integrations` (`INexusAuthService`,
  `IModAcquisitionService`, `IUpdateCheckService`, `UpdateCheckResult`,
  `ModUpdateInfo`), `steam` (`ISteamService`), `relay-client`
  (`IRelayLaunchService`, `LaunchResult`, `LaunchStatus`), `nxm`
  (`INxmModDownloadHandler`, `NxmSingleInstanceException`, `NxmIpcServer`,
  `INxmHandlerRegistrar`), `launcher` (the stub).
- **NuGet:** `Avalonia` 12.1.0 + `Avalonia.Desktop` 12.1.0 +
  `Avalonia.Themes.Fluent` 12.1.0 (the UI framework), plus an explicit
  `Avalonia.X11` 12.1.0 compile-time reference so `Program.cs` can construct
  `X11PlatformOptions` (the WmClass binding) directly; `Avalonia.Desktop`
  already supplies the X11 runtime backend transitively, but excludes
  `Avalonia.X11` from compile-time refs, so the options type is not otherwise
  visible to code. Also `CommunityToolkit.Mvvm`
  8.4.2 (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`),
  `Microsoft.Extensions.DependencyInjection` 10.0.9,
  `Microsoft.Extensions.Logging` 10.0.9. `Velopack` 1.2.0 is conditionally
  referenced (Windows installer and Linux AppImage packaging, gated on
  `CuratorUseVelopack=true`),
  which defines `CURATOR_VELOPACK` and brings in the app self-update engine.
- **BCL otherwise:** `System.Resources.ResourceManager` (the i18n lookup),
  `System.Globalization.CultureInfo`, `Avalonia.Threading.DispatcherTimer`
  (the polling timers), `Avalonia.Styling.ThemeVariant` (the theme) all
  in-box on net10.0.

The UI references every backend library because it is the composition root.
No backend library references the UI (the dependency direction is one-way).

## Testing

`Modificus.Curator.UI.Tests` covers:

- **`ShellViewModelTests`**: profile CRUD and switch, active-profile
  persist, switch-blocked-while-running, the launch result branches
  (Launched / DiscoveryIncomplete / StagingFailed / Error), the `_syncing`
  guard against spurious dropdown events, the post-dialog DMF prompt path, and
  the nxm handler status (startup read + refresh after Integrations closes +
  unavailable when no registrar).
- **`ShellViewModelAppUpdateTests`**: the status-strip notice (show/hide on
  `IsUpdateSupported` + `LastCheckResult` + dismissal, session-only dismiss,
  the `UpdateStateChanged` marshal), and the notice-click flow (confirm gate,
  download under the progress dialog, apply on success, alert on failure,
  cancel dismisses the notice for the session).
- **`ProfileSessionTests`**: the gate (RequestActive applies only when not
  running), persistence, `CanDeleteProfile`, `ReconcileActive` (delete of
  active clears to null; never auto-selects), `Refresh`.
- **`ManageProfilesViewModelTests`**: the create / rename / delete dialog
  view model (via an injectable `IDialogService` seam) + the per-row
  launch-settings action (opens for the selected row, not the active profile;
  unlocked while Darktide runs).
- **`LaunchSettingsViewModelTests`**: the launch-settings modal VM (existing
  settings load, add/remove rows, inline localized validation -- empty / `=` /
  NUL name, NUL value, case-insensitive duplicate, reserved name, all delegated
  to the shared `LaunchSettingsValidator` from the Profiles library -- the
  `EnableLuaLogs` Logging toggle + the `SkipSplash` skip-splash toggle (load +
  persist), Save persists once via `SetLaunchSettings` and closes only on
  success, Cancel no change).
- **`ModListViewModelTests`**: enable / disable, reorder, per-mod policy,
  remove (with confirm), auto-sort (identity stub), the add flow (peek,
  collision hard-block, import, add-mod), the linked-folder flow
  (`LinkMods`: peek, collision-refusal, re-link refresh, `LatestPolicy` add;
  `OpenFolder`: launches the file manager at the normalized external path,
  failure alert, no-op for non-linked/broken rows; the linked badge two-state
  available/broken, disabled policy edit, empty update-action cell,
  `IsExternalBroken` on Reload), `CheckCompleted` per-row state,
  `UpdateCommand` success / failure / one-at-a-time / premium gating,
  `CheckForUpdatesNow`, `IsRateLimited`, and the `NamesChanged` in-place row
  name refresh (refreshed when the flag is set, untouched when it is not).
- **`PreferencesViewModelTests`** + **`PreferencesServiceTests`**: the
  Preferences dialog view model and the service that applies theme / font
  scale / language and persists.
- **`LocalizationServiceTests`**: the indexer, `Format`, `SetCulture`
  (unknown name -> invariant), the `Item[]` event that refreshes every
  indexer binding.
- **`SettingsViewModelTests`** + **`SettingsViewModelAppUpdateTests`**: the
  Settings dialog (discovery overrides + the open-folder Storage buttons),
  plus the Updates section (current version, manual check + inline status, the
  `UpdateStateChanged` marshal, Download and Restart, the unsupported-build
  disabled controls, and the startup-check toggle persist + pre-fill).
- **`ImportModViewModelTests`**: the per-mod import modal (URL parsing,
  version field, name edit).
- **`DiscoveryEscapeHatchViewModelTests`**: the focused escape-hatch form
  (only the missing fields shown).
- **`IntegrationsViewModelTests`**: the Integrations dialog (OAuth login,
  API-key validate, sign-out), auth controls staying usable while Darktide
  runs, and the "Nexus download links" section (status display, register
  confirm / success / failure, unregister only when Curator owns the
  handler, unavailable when no registrar).
- **`UpdateCheckRunnerTests`**: the four triggers (startup restore,
  active-switch, periodic timer with the live toggle + interval, manual
  CheckNowAsync), the periodic-clock reset, the unobserved-exception safety,
  the thorough vs Month-only check selection.
- **`AppUpdateCheckRunnerTests`**: the single startup fire
  (fire-and-forget, never blocks, result lands through `UpdateStateChanged`),
  the `CheckOnStartup` config gate (no fire when disabled, fires when enabled),
  and the belt-and-suspenders unobserved-exception safety.
- **`NoopAppUpdateServiceTests`**: the no-op impl's neutral values
  (`IsUpdateSupported` false, null state, completed-null check, never-raised
  event) and the `NotSupportedException` from `DownloadUpdatesAsync` (the
  wiring-mistake guard).
- **`DmfPromptServiceTests`**: the two DMF cases (add existing / download +
  add or browser-open), the new-profile trigger, the decline path (nothing
  opens), the dialog-on-dialog avoidance (the prompt fires from the shell
  after the triggering dialog closes), the premium in-app download, the
  non-premium / unknown / no-auth browser-open path (opens regardless of the
  nxm registrar state), and the browser-launch failure fallback alert.
- **`OnboardingServiceTests`**: the first-run Welcome coordinator (already
  complete no-op, Continue persists + skips Integrations, Set up Nexus
  persists before opening Integrations once, the close == Continue
  equivalence, the in-process one-shot guard, and Integrations failure
  isolation).
- **`NxmModDownloadHandlerTests`**: the Darktide-only gate (rejects other
  games before auth / profile / acquisition), the auth + active-profile
  gates, the acquire / register / refresh flow, the error wiring (alert on
  failure), the UI-thread marshaling seam.
- **`EscapeClosesBehaviorTests`**: the pure `ShouldClose` helper behind the
  ESC-closes-dialogs behavior (true for `Key.Escape`, false for other keys).
  The KeyDown-to-Close wiring is rendered UI and not covered by a
  rendered-control test.
- **`DesktopIdentityOptionsTests`**: the explicit X11 `WM_CLASS` constant
  matches the Velopack pack id (`ModifAmorphic.ModificusCurator`) and the
  factory builds an `X11PlatformOptions` carrying it, without starting Avalonia
  or initializing X11 (no `DISPLAY` required).

The internal `NxmModDownloadHandler` implementation is visible to the test
assembly via `InternalsVisibleTo` (the handler is constructed by the
composition root via a factory; tests construct it directly with a
pass-through UI-thread seam).

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [UI architecture](../architecture/ui-architecture.md): the shell
  layout, the profile session, the mod list, the update UI, the DMF prompt,
  and the dialog / preferences / i18n design.
- [App auto-update architecture](../architecture/app-auto-update.md): the
  Velopack-packaged self-update flow behind `IAppUpdateService`, the
  startup-only check, and the lifecycle interaction.
- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md): the
  high-level tie-together (component model, the Relay contract Curator
  consumes, profiles, launch).
- [integrations](integrations.md): the `INexusAuthService`,
  `IModAcquisitionService`, and `IUpdateCheckService` the UI consumes.
- [profiles](profiles.md): `IProfileService`, `IModOrderResolver`, and the
  profile / mod-list model the UI drives.
- [mods](mods.md): `IModRepository`, `IModImportService`, and the
  source / version-policy model the UI reads.
- [config](config.md): `CuratorConfig`, `PreferencesConfig`, `ThemeMode`,
  `NexusConfig`.
