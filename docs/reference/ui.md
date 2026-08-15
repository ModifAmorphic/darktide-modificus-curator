# UI (`Modificus.Curator.UI`): reference

> The Avalonia 12 front end of Modificus Curator. Owns the SplitView shell with
> five hosted destinations (Profiles, Mods, Nexus, Preferences,
> Settings), profile management, the mod list, every true modal (Welcome,
> confirm, import, discovery escape-hatch, alert, progress), global preferences
> (theme, font scale, language), the i18n infrastructure, the DMF install-prompt
> coordinator, the update-check runner, and the app self-update service. Domain
> data stays behind backend library services; the one focused exception is the
> UI-owned thumbnail presentation cache (see
> [Mod thumbnail service](#mod-thumbnail-service)), which downloads + caches
> image bytes because it returns an Avalonia `IImage` no backend library can
> own.

The UI is an executable (`OutputType=WinExe`), not a library: it is the
composition root and the only project that constructs Avalonia windows. It
exposes a small set of interfaces and types so its logic stays unit-testable
and so backend libraries do not depend on it.

## The profile session

### `IProfileSession`

The single authority for "which profile is active, can it change, and is the
game running." Both the Profiles destination's switch and its create-sets-active
route through the same gate.

```csharp
public interface IProfileSession : INotifyPropertyChanged
{
    Guid? ActiveProfileId { get; }
    bool IsRunning { get; }
    bool HasPendingChanges { get; set; }
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
- `HasPendingChanges`: session-scoped edit/stage coordination state. True when
  the active profile's `profile.json` has structural/version edits not yet
  reflected in the staged tree the running game loaded. Set by mod-list edits
  (toggle/move/policy/remove/add/link/auto-sort/update); cleared on the next
  successful stage (a launch). In-memory only (never persisted). The shell
  surfaces this as a yellow "changes pending" status dot while the game runs,
  since Curator does not re-stage the mod tree mid-session.
- `RequestActive(id)`: the sole active-change gate. Applied and persisted
  only when the game is not running; otherwise a no-op (the active stays
  put). Both the Profiles destination's switch and its create-sets-active call
  this. Delete-of-active does not (delete uses `ReconcileActive`).
- `CanDeleteProfile(id)`: whether the profile `id` may be deleted right now.
  False when `id` is the active id and the game is running; true otherwise.
  The Profiles destination binds its Delete button to this so it disables
  while the game runs.
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

## The shell

### `ShellDestination`

The five hosted destinations, in navigation-rail order:

```csharp
public enum ShellDestination
{
    Profiles,
    Mods,
    NexusIntegrations,
    Preferences,
    Settings,
}
```

`Mods` is the initial selection. Selecting the current destination is a strict
no-op.

### `ShellViewModel`

The view model behind the main window. Owns SplitView navigation (the
`ShellDestination` enum), the global Launch action, and the global status strip
(running + pending + nxm-handler + app-update notice). The active profile is
owned by `IProfileSession`; launch availability derives directly from
`ActiveProfileId` + `IsGameRunning` + the shell's own
`IsLaunchAttemptInProgress`, never a cached snapshot.

- `CurrentDestination`: the active destination. Starts on `Mods`. Mutated only
  through `NavigateAsync`, which runs the guarded leave/enter lifecycle; the
  private setter prevents callers from switching the page while bypassing those
  effects.
- `IsNavigationPaneOpen`: whether the navigation pane is expanded. Starts
  collapsed (compact icon rail). Toggled by `ToggleNavigationPaneCommand` (the
  hamburger button).
- `CurrentDestinationTitle`: the localized title of the current destination,
  shown in the global header. Re-resolves on a destination change and on a
  culture change.
- `IsProfilesSelected` / `IsModsSelected` / `IsNexusIntegrationsSelected` /
  `IsPreferencesSelected` / `IsSettingsSelected`: the selected projections that
  drive the nav-rail buttons' `selected` class.
- `IsProfilesVisible` / `IsModsVisible` / `IsNexusIntegrationsVisible` /
  `IsPreferencesVisible` / `IsSettingsVisible`: the visibility projections that
  drive which hosted page is shown in the content area. Exactly one is true at
  a time.
- `NavigateCommand` (`RelayCommand`, parameter = `ShellDestination`): the nav-
  rail entry point, delegating to `NavigateAsync`.
- `NavigateAsync(ShellDestination)`: the guarded navigation core. Same-
  destination is a strict no-op (so a pending DMF trigger survives same-
  destination Mods clicks; it is consumed only by a real navigation into
  Mods). For a real change: (1) leaving Profiles awaits the unsaved-changes
  three-choice guard (`ProfilesViewModel.ConfirmCanNavigateAwayAsync`), and
  Cancel/ESC/X or a Save that the service rejected keeps everything
   unchanged; (2) run the current destination's leave effects (Nexus
   Integration: `Deactivate` + mod-list reload, with no registration probe on
   the way out; Settings:
   mod-list reload + re-read `CheckOnStartup` + refresh the app-update notice);
  (3) switch `CurrentDestination`; (4) run the target's enter effects (Settings:
  `RefreshFromConfig` synchronously; Nexus: await `RefreshAsync`;
  Mods: await `DmfPromptService.ProcessPendingAsync` after the destination is
  already Mods, then reload the mod list when a trigger was consumed). The
  destination is switched before any enter await so it stays active even if a
  refresh or the DMF prompt reports an error.
- `NavigateToIntegrationsAsync()`: the internal awaitable entry point the
  first-run onboarding reuses for its "Set up Nexus" choice, so onboarding-
  completion persistence and Integrations activation share one navigation path.
- `Profiles` / `ModList` / `Integrations` / `Preferences` / `Settings`: the
  five hosted page view models (singletons, injected into the shell).
- Launch + status-strip surface (`IsGameRunning`, `HasPendingStagedChanges`,
  `GameRunningText`, the `Show*Dot` states, `IsNxmRegistered` + its derived
  text/tooltip (mirrored from the shared NXM registration state; see
  [Shared NXM registration state](#shared-nxm-registration-state)),
  `ShowAppUpdateNotice` + its text/tooltip, and the
  `CheckAppUpdateNowCommand` / `DismissAppUpdateCommand` notice flow): described
  where relevant below and in the app-update section.
- `IsLaunchAttemptInProgress`: whether a launch attempt is executing, from the
  executable launch request (where it is set before anything else, disabling
  the button via `LaunchCommand`'s can-execute) through the pre-launch render
  yield, the synchronous launch call, failure-dialog handling, and the
  post-spawn wait for the session's running-state signal to observe Darktide
  (or a 30-second timeout, started only after the spawn returns; a false
  polling result never clears it). Shell-owned and distinct from
  `IsGameRunning`: the attempt covers the process-detection gap the session's
  detector cannot yet see. A method-level guard refuses a second,
  direct/programmatic execution while an attempt is active. The state clears
  in all completion and exception paths; on the `Launched` path only after
  the handoff resolves (game observed -> the ordinary running gate keeps
  Launch disabled; timeout with the game absent -> retry is possible). While
  the state is true, the shell also shows the full-client launch overlay (a
  scrim + centered indeterminate progress card layered over the disabled
  shell inside `MainWindow`; see the `MainWindow` section). The button's
  text, tooltip, and accessible name never change. The pre-launch yield
  (production: one Avalonia dispatcher yield at
  `DispatcherPriority.Loaded`, after layout + render and before subsequent
  input, so the disabled style + overlay paint before the synchronous launch
  work resumes) and the handoff timeout are injected delegates, so unit tests
  run deterministically without a live dispatcher or real waiting. The wait
  observes the existing session signal only (subscribe-before-check, the
  temporary subscription removed deterministically): bounded detector
  handoff, not process supervision (no process handle is taken; Relay and
  Darktide stay fire-and-forget).

The hosted page view models are application-lifetime singletons; navigation
never calls an old Window-close final-cleanup (`Detach`) path. There is no
shared `IPage` / `INavigationService` lifecycle interface: Profiles, Settings,
and Nexus have deliberately different activation/deactivation
capabilities, so the shell calls each concrete page VM directly.

### `MainWindow`

```csharp
public partial class MainWindow : Window
{
    internal const double PaneOpenMin = 200.0;
    internal const double PaneOpenMax = 360.0;
    internal const double PaneIconColumn = 48.0;
    internal const double PaneLabelMargin = 12.0;
    internal const double PaneTrailingBreathingRoom = 16.0;

    internal const double DefaultWidth = 960.0;        // matches XAML Width
    internal const double DefaultHeight = 640.0;       // matches XAML Height
    internal const double MinWindowWidth = 720.0;      // matches XAML MinWidth
    internal const double MinWindowHeight = 480.0;     // matches XAML MinHeight
    internal const double CorrectionTolerance = 1.0;   // DIP, #19431 threshold

    public MainWindow();                               // XAML runtime/designer path (no store)
    internal MainWindow(IAppStateStore stateStore);    // production path

    internal static double ComputeOpenPaneLength(double widestLabelWidth);
    internal static (double Width, double Height) NormalizeSavedSize(
        AppWindowState? saved, double? workAreaWidth, double? workAreaHeight,
        double minWidth, double minHeight);
    internal static bool NextMeaningfulMaximized(WindowState current, bool previous);
    internal static bool IsTrustedResizeReason(WindowResizeReason reason);
    internal static Size ResolveTrustedNormal(Size? trustedCandidate, WindowState state, Size current);
    internal static bool ShouldCorrectFromLayout(
        Size? layoutCandidate, bool layoutSawOpen, WindowState state, Size resolvedNormal);
    internal static bool PersistedSeedsMaximized(AppWindowState? saved);
    internal static bool TryConvertWorkAreaDip(
        double scaling, double pixelWidth, double pixelHeight,
        out double widthDip, out double heightDip);
}
```

The Avalonia main window. Owns only view mechanics: SplitView pane sizing, the
no-profile handoff link, the full-client launch overlay (in
`MainWindow.axaml`), and the persisted window geometry. State, navigation, and
service calls stay in `ShellViewModel`. The public parameterless constructor
loads XAML + safe in-memory defaults and is the Avalonia runtime/designer
loader path (it performs no store IO and locates no service). Production
construction goes through the internal `MainWindow(IAppStateStore)` overload,
supplied by an explicit singleton factory in the composition root before the
window is returned/shown.

- **Root composition + the full-client launch overlay.** The window's content
  is a root `Grid` with two children: the named `NavSplitView` shell and, as
  the final child (explicit `ZIndex` on top), the `LaunchAttemptOverlay` panel
  bound to `ShellViewModel.IsLaunchAttemptInProgress`. While a launch attempt
  is in progress the SplitView's `IsEnabled` is bound to the inverse state
  (keyboard + pointer activation are blocked at the shell root) and the
  overlay takes the input surface: a semi-opaque scrim background covering
  the whole client area (hit-testable, so pointer input stops at the overlay;
  defense in depth behind disabling the shell) with a centered progress card
  in the iron-and-rust launch palette (`CuratorLaunchOverlay*` app-owned
  brushes; theme-independent card, theme-dependent scrim opacity, never the
  platform accent). The card carries the localized `Launch_OverlayTitle` /
  `Launch_OverlayMessage` strings and an ordinary indeterminate `ProgressBar`
  animated by the Fluent ControlTheme's own keyframes, with declarative
  accessibility metadata (`AutomationProperties.Name` from the localized
  strings and a polite `AutomationProperties.LiveSetting`; no focus trap, no
  imperative accessibility code-behind). There is no Cancel control (once
  Relay starts there is no safe cancellation contract), and the overlay is a
  layered sibling rather than inserted into the content flow, so layout never
  shifts. Native window chrome stays available: the overlay lives inside the
  client area only, so the window can still be moved, minimized, or closed.
  Failure + discovery dialogs are separate OS-owned dialog windows and
  therefore appear above the overlay while failure handling is in progress;
  the overlay disappears through the existing attempt-state clear in the
  shell's `finally`.
- **Open-pane width grows to fit the widest localized label.** The SplitView's
  XAML `OpenPaneLength=200` is the design-time/startup fallback and the lower
  bound. Once the window is open, `UpdateOpenPaneLength` measures the live
  localized pane labels (the five destinations plus the pane-bottom Exit) with
  the representative `NavMeasureLabel` TextBlock's actual typography
  (`FontFamily`, `FontStyle`, `FontWeight`, `FontStretch`, `FontSize`,
  `LetterSpacing`) via the Avalonia 12.1 `TextLayout` API, unwrapped with
  infinite width, and grows `NavSplitView.OpenPaneLength` to
  `clamp(ceil(48 + 12 + widest + 16), 200, 360)`. Future translations and font
  scales therefore do not clip at the original 200px; the cap keeps the pane
  from eating too much of the content area, and beyond it each label's
  `TextTrimming=CharacterEllipsis` is the graceful fallback (the full text
  remains in the tooltip and the automation name). Re-measurement fires on
  inherited `Window.FontSize` changes and on LocalizationService Culture /
  `Item[]` changes.
- **Pure arithmetic helper** `ComputeOpenPaneLength(widestLabelWidth)` is the
  unit-testable seam for pane sizing. Constants (`PaneOpenMin`, `PaneOpenMax`,
  `PaneIconColumn`, `PaneLabelMargin`, `PaneTrailingBreathingRoom`) name the
  pieces so future tweaks are deliberate.
- **Persisted window geometry.** The last unmaximized (Normal) client size in
  DIP and whether the last meaningful state was Maximized are read from
  `IAppStateStore.MainWindowState` on the production path, validated + clamped
  by the pure `NormalizeSavedSize` helper to the XAML minimums (`MinWindowWidth`
  / `MinWindowHeight`) and, when available, the primary screen's working area
  converted from physical pixels to DIP via `Screen.Scaling` (the pure
  `TryConvertWorkAreaDip` validates finite + positive scaling and dimensions),
  then applied as `Width`/`Height` before first Show so the platform has the
  right restore size. The persisted maximized flag seeds the in-memory
  meaningful-state flag and the one-shot first-open maximize immediately (the
  pure `PersistedSeedsMaximized` seam); when the flag is set, the window
  maximizes once in `OnOpened` (after Show) for Win32/X11 consistency, so a
  later unmaximize restores to the saved Normal size. The last Normal size is
  tracked through deferred, coalesced, reason-aware resize observation:
  `OnResized` tags each observation by `WindowResizeReason` and whether the
  window had opened, then posts one apply; the pure `IsTrustedResizeReason`
  treats User, Unspecified, Application, and DpiChange as authoritative and
  `Layout` as never authoritative, and `ResolveTrustedNormal` updates the last
  Normal size only for a trusted observation while the settled state is Normal.
  The meaningful-state flag is tracked through `OnPropertyChanged` for
  `WindowStateProperty` via the pure `NextMeaningfulMaximized` policy: Normal
  clears it, Maximized sets it, and Minimized and FullScreen leave the
  preceding flag unchanged.
- **Avalonia #19431 visible-restore correction.** At Windows scaling such as
  175%, a Maximized to Normal transition can emit a correct `Unspecified`
  Normal resize followed by a stale `Layout` resize carrying the maximized
  `ClientSize`. `MainWindow` uses manual top-level sizing, so a post-open
  `Layout` resize is not a user sizing intent. The pure
  `ShouldCorrectFromLayout` seam decides, after the trusted candidate has been
  resolved into the last Normal size, whether a post-open `Layout` observation
  that materially conflicts (more than `CorrectionTolerance` DIP) while Normal
  should trigger a reapply of the trusted size through `ClientSize`. The
  correction never persists a new size from `Layout` and never manipulates
  window position; a trusted observation arriving in the same burst as the
  stale `Layout` wins first, so the correction targets the trusted value.
- **Close path.** `OnClosing` calls base (so a `Window.Closing` subscriber can
  still cancel), and if not cancelled it marks the window closing (queued
  applies then no-op), consumes any pending trusted candidate when the settled
  state is Normal (never the raw `ClientSize`, which may be the stale #19431
  value), and persists one atomic `AppWindowState`. Closing while Maximized or
  Minimized keeps the tracked last-Normal size and meaningful flag. State is
  never written on every resize, only once through the close path. No window
  position is stored, and `WindowStartupLocation` stays `CenterScreen`. The
  screen read is defensive: an unavailable or invalid screen, an absent or
  invalid persisted state, or a corrupt store all fall back to the XAML 960x640
  size and never crash startup.
- **Exit action.** A pane-bottom button (the only pane `Grid.Row="2"` control;
  the middle `*` row of `Auto,*,Auto` holds it at the bottom) calls `Close()`
  exactly like the title-bar close, so the persisted window state lands through
  the same `OnClosing` path with no `Shutdown`/`Environment.Exit`. It is not a
  destination and never carries a selected state. Compact mode shows its drawn
  Material logout geometry with a tooltip + accessibility name; expanded mode
  adds the localized `Exit` label.
- **Launch action + theme-safe update notice.** The header Launch button carries
  a branded iron-and-rust treatment via the `Button.launchAction` class: a dark
  gunmetal face, off-white Quantico Bold display text (the embedded
  `Assets/fonts/Quantico-Bold.ttf`, SIL OFL 1.1, shipped unmodified; bold with
  1-DIP letter spacing and `TextOptions.TextHintingMode="Strong"` /
  `BaselinePixelAlignment="Aligned"` for crisp rendering; its
  `OFL.txt` license is copied to build and publish output), a rust lower edge, a drawn play-arrow `<Path>`, and the
  uppercase `Launch_ButtonDisplay` visible label while the accessible name and
  tooltip stay `Launch_Button`. The dismissible app-update status-strip pill,
  its link (`HyperlinkButton.updateNoticeLink`), and its dismiss
  (`Button.updateNoticeDismiss`) draw only from app-owned theme-driven brushes.
  Both surfaces' colors are app-owned resources in `App.axaml`:
  theme-independent `CuratorLaunch*` brushes (face/text/rust/focus, identical in
  Light and Dark) and per-theme `CuratorUpdateNotice*` brushes (background,
  hover, pressed, foreground, border, focus per Light/Dark `ThemeDictionary`).
  Scoped `/template/ ContentPresenter#PART_ContentPresenter` selectors cover
  normal, `:pointerover`, `:pressed`, `:disabled`, and `:focus-visible` for both,
  outranking the Fluent ControlTheme's accent-reapplying per-state setters so
  neither surface depends on `SystemAccentColor`.
- **Falls back silently** to the XAML `OpenPaneLength=200` when the live
  `LocalizationService` is unavailable (design-time paths) or when
  measurement throws at runtime; never crashes the window.

## Dialog service

### `IDialogService`

The application's true-modal dialog abstraction. Keeps view models free of
direct Avalonia `Window` construction so their logic stays unit-testable: a
view model depends on this seam, and tests inject a recording fake instead of a
real window. The production `DialogService` owns every real `Window` and
`ShowDialog` wiring. Hosted destinations (Profiles, Mods, Nexus,
Preferences, Settings) are not modals and never flow through this seam; the
inline import card is a hosted `UserControl` (the `ImportWorkflowViewModel`),
not a modal.

```csharp
public interface IDialogService
{
    Task<WelcomeChoice> ShowWelcomeAsync();
    Task<bool> ConfirmAsync(string title, string message);
    Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields);
    Task ShowAlertAsync(string title, string message);
    Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(string title, string message, bool canSave);
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
```

Six true-modal methods: the first-run Welcome, a binary confirm, the launch
discovery escape hatch, a single-button alert, an unsaved-changes three-choice
prompt, and a non-dismissable progress spinner. Copied local-import failures
surface inline in the `ImportWorkflowView` card (not through this seam); the
linked-folder flow continues using `ShowAlertAsync` for its failures.

- `ShowWelcomeAsync()`: the first-run Welcome modal. Returns the user's typed
  `WelcomeChoice` (`Continue` or `SetUpNexus`). ESC, title-bar close, and
  window close are equivalent to `Continue`.
- `ConfirmAsync(title, message)`: a modal confirmation. Returns true when
  the user confirms, false otherwise (cancel / dismiss). Gates destructive
  actions (profile delete, mod remove, the DMF download prompt). The
  three-choice unsaved-changes flow uses `ShowUnsavedChangesAsync` instead.
- `ShowDiscoveryEscapeHatchAsync(missingFields)`: the discovery escape-hatch
  modal, focused on the missing discovery fields the launch reported. Inputs
  are shown only for the fields in `missingFields`. Alongside the rows the
  dialog carries the same global `OverrideAutomaticDiscovery` toggle + Discover
  button as Settings (both write-through: the toggle persists the mode and
  turning it off runs an ordinary `ISteamService.Discover`; the Discover button
  calls `ISteamService.Rediscover`; row editability follows the mode). Returns
  true when the user submitted, false when they cancelled. No auto-retry: the
  caller does not re-launch on a true return; the user clicks Launch again.
- `ShowAlertAsync(title, message)`: a simple modal alert (a single OK
  button, no cancel). Used to surface a launch `Error`, a download failure,
  a linked-folder failure, or the DMF informational case where there is
  nothing for the user to decide, only acknowledge.
- `ShowUnsavedChangesAsync(title, message, canSave)`: the dedicated three-
  choice unsaved-changes prompt (left to right: Cancel, Don't save, Save;
  Save is the accent button). The `UnsavedChangesChoice` enum defaults to
  `Cancel`, so ESC, the title-bar close, and a window close all behave like
  the explicit Cancel button. When `canSave` is false the Save button is
  disabled and a concise localized explanation shows beneath the buttons so
  the disabled action is not mysterious; Cancel and Don't save stay
  available. Caller-side semantics: Save runs the caller's save core and
  proceeds only on success, Don't save reloads authority and proceeds,
  Cancel preserves the staged state and stops the attempted transition.
- `ShowProgressAsync<T>(title, message, work)`: a buttonless, non-closeable
  modal spinner over the supplied async work. The user cannot dismiss the
  spinner: the work runs to completion and the caller surfaces its result.
  The work's exception (if any) propagates to the caller; the spinner is
  closed in either case. Used for the DMF in-app download and the app
  self-update download.

### `DialogService`

```csharp
public sealed class DialogService : IDialogService
{
    public DialogService(Window owner, LocalizationService localization, IConfigLoader configLoader);
}
```

The concrete implementation. `owner` is the main window (a singleton; resolved
by the desktop lifetime and by `DialogService` for modal parenting).
`localization` is handed to the Welcome title and the escape-hatch VM (header +
per-row labels). `configLoader` is handed to the escape-hatch VM (one read-
modify-save on submit). `DisableOwnerForModal` is the nesting-safe owner-
disable workaround (a reference count tracks overlapping modals; the owner
re-enables only when the outermost modal closes).

## Preferences service

### `IPreferencesService`

The single authority for applying user-facing preferences (theme, font scale,
language, show-Relay-console) to the running app and persisting them to
`CuratorConfig`.

```csharp
public interface IPreferencesService
{
    void ApplyAndPersist(ThemeMode theme, double fontScale, string language, bool showRelayConsole);
}
```

`ApplyAndPersist` applies the theme via `Application.RequestedThemeVariant`,
the font scale via application-level `AppFontSize` + `AppStatusFontSize`
resources (cascading to all controls through inheritance and `DynamicResource`),
and the language via `LocalizationService.SetCulture`. It then persists all
four to the config file via a read-modify-save through `IConfigLoader`.
`showRelayConsole` is persisted only (no live-apply): it is read at launch time
by the Relay launcher to decide whether to hide the console window. Safe to
call at startup (the values may match the loaded config, which is a no-op
apply).

The theme mapping honors Gaming Mode: `ThemeMode.System` normally maps to
`ThemeVariant.Default` (follow the OS), but while running in a Steam Deck
Gaming Mode session (see `IGamingModeState`) it applies `ThemeVariant.Dark`
as the effective runtime theme, because the Gaming Mode session reports no
usable desktop appearance preference. The stored preference stays `System`;
explicit Light and Dark remain authoritative everywhere. The pure mapping
(`ResolveThemeVariant(theme, isGamingMode)`) is the policy seam.

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
completion, and navigates the shell to Nexus on a "Set up Nexus"
choice. After the first run, the call is a no-op for the lifetime of the process.

```csharp
public sealed class OnboardingService
{
    public OnboardingService(
        IAppStateStore appState,
        IDialogService dialogs,
        Func<Task> navigateToIntegrations,   // resolves to ShellViewModel.NavigateToIntegrationsAsync
        ILogger<OnboardingService> logger);

    public Task ShowWelcomeIfFirstRunAsync();
}
```

- `ShowWelcomeIfFirstRunAsync()`: one-shot. Reads the persisted
  `OnboardingCompleted` flag (plus an in-process guard) and no-ops when already
  complete; otherwise shows the Welcome modal, persists completion BEFORE any
  further UI (so navigating away from Nexus, or the navigation
  failing, can never cause Welcome to repeat), and on a
  `WelcomeChoice.SetUpNexus` choice navigates the shell to Nexus
  via the injected `navigateToIntegrations` delegate.
- `navigateToIntegrations`: resolved lazily through
  `ShellViewModel.NavigateToIntegrationsAsync` at composition, so the
  destination's auth + registration-state refresh runs and the
  leave-Integrations mod-list reload applies after the Welcome-driven visit
  too. Kept as a delegate so the coordinator stays unit-testable.
- `WelcomeChoice`: the typed result returned through
  `IDialogService.ShowWelcomeAsync`. `Continue` (the default; also ESC, the
  title-bar close button, and a window close) persists completion and leaves the
  user at the default destination; `SetUpNexus` persists completion then
  navigates to Nexus.

The App wires the call after the main window is actually opened (Avalonia modal
dialogs require a shown owner): a one-shot `Opened` handler resolves the
coordinator and fires the call; a failure is logged and swallowed so it never
crashes startup.

## Shared NXM registration state

The OS `nxm://` association is inherently racy (any other manager can claim it
at any time; the OS routes a click at click time), so the UI does not chase
freshness. One application-lifetime singleton, `NxmRegistrationState`
(`src/ui/Session/`), holds the last-known registration and publishes changes to
every consumer surface:

```csharp
public interface INxmRegistrationState
{
    bool IsAvailable { get; }       // a platform registrar exists (Windows/Linux)
    bool IsRegistered { get; }      // last-known; false when unknown/unavailable
    event Action? Changed;          // raised on the UI thread after any refresh
    void RefreshFromOs();           // synchronous probe; the only writer
}
```

- **Deliberate probe points, exactly three kinds:** one seed `RefreshFromOs()`
  in the `ShellViewModel` constructor (startup), one per Nexus-destination
  enter (`IntegrationsViewModel.RefreshAsync` -> `RefreshNxmState`), and one
  after each register/release action on the Nexus page. `RefreshFromOs` is the
  only writer: it reads `INxmHandlerRegistrar.IsRegistered()` (on Linux a
  sanitized `xdg-mime` child; see the
  [nxm reference](nxm.md#os-scheme-handler-registration-service)), catches any
  throw as not-registered, and marshals `Changed` to the UI thread through the
  shared `Action<Action>` seam (defensively; every caller is already there).
  No other probe exists in the UI layer: `ModListViewModel.Reload()` and
  navigation-leave effects never touch the OS registration.
- **Consumers:** the shell status strip (its `IsNxmRegistered` mirrors the
  state, null when unavailable), the Mods empty-state Nexus hint (the mod
  list's `IsNxmRegistered` follows the state; `Reload` stays probe-free), the
  Nexus destination (copies `IsAvailable`/`IsRegistered` after each refresh and
  performs the mutations through the registrar it still injects), and the DMF
  download-prompt wording (reads the state; never probes). All of them accept
  staleness between deliberate refreshes by design.

## Gaming Mode state

Steam Deck Gaming Mode sessions cannot host desktop workflows: file/folder
pickers are unusable, file-manager opens depend on a desktop shell, and Steam's
built-in Gaming Mode browser does not hand `nxm://` links to Curator. The UI
therefore gates those surfaces. One application-lifetime singleton,
`GamingModeState` (`src/ui/Session/`), captures the answer once:

```csharp
public interface IGamingModeState
{
    bool IsGamingMode { get; }   // fixed for the process lifetime
}
```

- **Single source of truth:** `GamingModeState` reads
  `GamingModeDetector.IsGamingMode()` (the [steam](steam.md) library's
  environment-signature detector: `SteamOS=1`, `SteamGamepadUI=1`,
  `XDG_CURRENT_DESKTOP=gamescope`, all required) once at construction. Nothing
  else in the UI layer touches the environment; every gate flows from the
  injected state. The value is immutable because a session cannot change from
  Gaming Mode to Desktop Mode (or back) without restarting Curator.
- **Effective theme:** `PreferencesService` maps `ThemeMode.System` to
  `ThemeVariant.Dark` while gaming (see
  [Preferences service](#preferences-service)); the stored preference stays
  `System`.
- **Picker and file-manager gating:** the Mods Add split button (archive,
  folder, link-external), the Settings + escape-hatch discovery Browse
  buttons, the Settings open-folder buttons, and a linked row's open-folder
  badge are disabled while gaming, each carrying a Desktop Mode tooltip that
  shows even on the disabled control (`ToolTip.ShowOnDisabled`) plus an inline
  per-section hint reachable by touch/controller. Every gated path also has a
  code-level guard (view handlers early-return; the open-folder commands
  early-return), so a programmatic invocation launches no picker or file
  manager. Manual discovery-path entry and submission stay available.
- **Nexus browser-flow gating:** while gaming, Add Nexus Mods, the per-mod
  update action for regular/unverified accounts, and the DMF download prompt
  for regular/unverified/unauthenticated accounts surface localized Desktop
  Mode guidance (alerts, tooltips, the empty-state hint) instead of launching
  the browser. Premium update installs and the Premium DMF download run their
  normal in-app acquisition paths in Gaming Mode.

## The DMF prompt coordinator

### `DmfPromptService`

The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator.
Subscribes to `IProfileService.ProfileCreated` at construction (the shell's DI
registration resolves `DmfPromptService` before `ShellViewModel` so the
subscription exists before any profile can be created), records the trigger as
pending, and the shell consumes it on the next real navigation into Mods
(`NavigateAsync` sets `CurrentDestination = Mods` first, then awaits
`ProcessPendingAsync`, then reloads `ModListViewModel` when a trigger was
consumed), so the DMF prompt runs as the topmost modal with Mods already
selected underneath. A pending trigger survives visits to other destinations
and is consumed only on a real Mods entry.

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
        INxmRegistrationState nxmRegistration,
        Func<Uri, bool>? launchExternal = null);

    // Returns true when a pending trigger was consumed (a prompt may or may
    // not have fired depending on the active-id + DMF checks); false when
    // there was no pending trigger, so the caller knows no mod-list reload
    // is warranted.
    public Task<bool> ProcessPendingAsync();
}
```

- `DmfModId`: the Nexus mod id of Darktide Mod Framework (8). DMF is
  required for most Darktide mods; the prompt offers to install it when
  missing.
- `ProcessPendingAsync()`: processes any pending new-profile trigger. Called by
  `ProfilesViewModel` immediately after a successful create + activation. Safe
  to call when nothing is pending (a no-op). The trigger is consumed (cleared)
  before it is processed so an exception in the prompt does not leave it stuck
  pending for the next call; the prompt is wrapped in a try/catch that logs
  and swallows non-cancellation exceptions.

### Trigger + cases

One trigger fires when DMF is not in the active profile:

1. **New profile becomes active** (a fresh ask per profile; no persisted
   flag). A profile created while Darktide runs does not become active (the
   session gates it), so no prompt fires in that case.

Two cases on a trigger:

1. DMF in the repo but not in the profile: a Yes/No confirm. On Yes,
   `IProfileService.AddMod` adds it instantly.
2. DMF not in the repo: a Yes/No confirm (the message tailors to whether Curator
   owns the `nxm://` handler, read from the shared NXM registration state with
   no probe: the manager-download path when it does,
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
navigates to Nexus; the one-time Nexus setup offer lives in the
first-run Welcome flow. Whenever an accepted path calls `AddMod` with DMF, the
profile add boundary's fresh-add rule places it first (rank 0) and
order-locked; the prompt carries no placement choreography (see
[profiles](profiles.md)).

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
runtime change in the Nexus destination takes effect without a
restart.

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

The header rate-limit pill is coupled to the refresh button, not the raw result
flag: when a check result is rate-limited, the button stays disabled until the
server-reported reset in `UpdateCheckResult.RateLimitResetsAt` elapses (or a
1-minute client-side fallback when Nexus sent no reset, e.g. an HTTP 429 with no
`x-rl-*` headers), and the pill reads "Refresh disabled due to rate-limiting"
exactly while the button is rate-limit-blocked. Both clear together the moment
the reset passes (the list VM re-evaluates `IsRateLimitActive` on each shared
1-second countdown tick). The rate-limit reason takes tooltip precedence when
both the rate limit and the manual fire-count throttle are active; the two
causes share one countdown timer, so either keeps the button disabled.

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
The shell re-reads the toggle when leaving Settings so the notice tracks a
runtime toggle without a restart. The toggle is surfaced in the Settings
destination "Updates" section (read-modify-save, no caching).

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

## Mod list density / detailed rows

The Compact/Detailed row-density choice for the Mods destination. Compact is the
dense one-line row (the default, unchanged behavior); Detailed adds the Nexus
summary + a cached thumbnail across multiple lines while preserving every
existing row action. Three UI-layer components cooperate: the
`DetailedModRowsViewModel` coordinator (the lifecycle + orchestration owner),
`ModListViewModel` (which exposes it read-only + hands it the row snapshot on
reload), and `ModItemViewModel` (which gains display state but stays
state-only). The thumbnail cache lives in its own focused service
([below](#mod-thumbnail-service)).

### `DetailedModRowsViewModel`

A focused application-lifetime child of `ModListViewModel`, analogous to
`ImportWorkflowViewModel`: it isolates asynchronous metadata/thumbnail
orchestration from the already-large parent so the parent does not widen with
those mechanisms. Registered as a singleton **before** `ModListViewModel` (which
takes it as a constructor child; see [DI registration](#di-registration)).

```csharp
public sealed partial class DetailedModRowsViewModel : ObservableObject
{
    public DetailedModRowsViewModel(
        IConfigLoader configLoader,
        INexusModMetadataService metadataService,
        IModRepository repository,
        IModThumbnailService thumbnailService,
        ILogger<DetailedModRowsViewModel> logger);

    public ModRowDensity RowDensity { get; private set; }   // persisted Compact/Detailed selection; mutated only by SetDensityCommand
    public bool IsCompact { get; }                        // RowDensity == Compact (the default)
    public bool IsDetailed { get; }                       // RowDensity == Detailed

    // CommunityToolkit-generated from SetDensity(ModRowDensity):
    public IRelayCommand<ModRowDensity> SetDensityCommand { get; }

    public Task SetRowsAsync(IReadOnlyList<ModItemViewModel> rows);
}
```

- `RowDensity`: the persisted density, read + normalized (only `Detailed`
  survives; every other numeric value, including undefined, becomes `Compact`)
  from `CuratorConfig.Preferences.ModRowDensity` at construction. The setter is
  private, so `SetDensityCommand` is the only mutation path. Setting it through
  the command normalizes, persists (a focused live
  read-modify-save of only this property through `IConfigLoader`, **not**
  `IPreferencesService.ApplyAndPersist`), and reprocesses the current rows. A
  value-equal (after normalization) click is a strict no-op: no save, no reload,
  no backfill.
- `IsCompact` / `IsDetailed`: projections that drive the toolbar's two density
  buttons' `selected` class (the shell's conditional-class pattern, not a
  `ToggleButton`). Re-fire on `RowDensity` change.
- `SetDensityCommand`: the toolbar's density selector entry point. Generated by
  `[RelayCommand]` from `SetDensity`.
- `SetRowsAsync(rows)`: the handoff from `ModListViewModel.Reload`. The
  synchronous setup runs before the method returns: it cancels the prior
  generation (a `CancellationTokenSource` whose token is captured), snapshots
  the rows into an internal array, pushes the current density to each row
  (`row.IsDetailed`), and on Compact clears every row's `Thumbnail`. In Compact
  mode the returned `Task` is already completed. In Detailed mode the returned
  `Task` represents the complete generation: known-thumbnail hydration for
  eligible rows, the metadata backfill, and every thumbnail load a backfill
  result starts. The parent intentionally discards the returned task
  (`_ = DetailedRows.SetRowsAsync(...)`); the task absorbs cancellation and logs
  every other exception internally (`ProcessDetailedSafelyAsync`), so a
  discarded task can never fault and a caller that awaits it waits for the whole
  generation to settle.

**Generation-based stale-result protection.** Every `SetRowsAsync` call cancels
the prior generation and starts a new one (an incrementing generation counter).
Metadata and thumbnail results are applied only when the generation is still
current, the mode is still Detailed, the exact row object is still in the current
snapshot, and (for thumbnails) the row's `ThumbnailUrl` still matches the one
the load requested. A profile switch, a Compact toggle, or a superseding reload
prevents stale assignment without aborting the thumbnail service's shared cache
load (the caller's cancellation token cancels only the caller's wait, not the
shared uncancellable load).

**Detailed-mode pipeline.** (1) Start known-thumbnail hydration for rows with
eligible persisted metadata (`CanLoadThumbnail`); (2) invoke
`INexusModMetadataService.BackfillMissingAsync` with the current row container
ids in row order as priority; (3) for each container the backfill enriched,
re-read its metadata as authoritative from the repository, apply it to the row
still in the snapshot, and start its thumbnail when newly eligible; (4) await
all started thumbnail loads so the generation task does not settle before them.
All observable row mutations happen in generation-checked continuations on the
captured UI context (no `ConfigureAwait(false)`, the UI-layer convention).

### `ModListViewModel` integration

`ModListViewModel` exposes the coordinator as a read-only child:

```csharp
public DetailedModRowsViewModel DetailedRows { get; }
```

- `Reload` joins each row's `ModDisplayMetadata` from the container when it
  builds the row, then hands the final row snapshot to
  `DetailedRows.SetRowsAsync(Mods.ToArray())` fire-and-forget after the rows are
  built. An empty snapshot is handed on the no-profile path so old work is
  cancelled. The handoff is the single seam; `ModListViewModel` performs no
  density, backfill, or thumbnail work itself.

### `ModItemViewModel` display state

The row gains observable state + derived projections for display metadata,
density, the thumbnail image, and thumbnail eligibility. It performs no I/O and
calls no service (state-only, unchanged contract). The new members:

| Member | Meaning |
| --- | --- |
| `ModDisplayMetadata? DisplayMetadata` | The display metadata joined from the container at construction + updated by the coordinator when backfill enriches it. `null` means none fetched. Drives the summary/thumbnail/eligibility projections. |
| `IImage? Thumbnail` | The decoded thumbnail image set by the coordinator, or `null` when none loaded or cleared (Compact switch, metadata change to an incompatible URL). |
| `bool IsDetailed` | Whether this row is displayed in Detailed mode. Pushed down by the coordinator; drives `CanLoadThumbnail`. |
| `string SummaryText` | The trimmed summary, or a localized fallback ("Details unavailable") when metadata is absent or the summary is empty. Re-resolves on a culture change. |
| `string? SummaryTooltip` | The full untrimmed summary for the tooltip/accessibility name, or `null` when there is none (the fallback is already shown). |
| `bool IsAdultContent` | The metadata adult flag, or `false` when no metadata. The coordinator uses it to skip thumbnail loading. |
| `string? ThumbnailUrl` | The thumbnail URL the coordinator reads to decide a load, or `null`. The coordinator matches it against the requested URL before assigning a result (stale-result protection). |
| `bool HasThumbnail` | Whether a decoded thumbnail is currently bound. |
| `bool CanLoadThumbnail` | Detailed + Nexus source + non-null metadata + not adult + non-empty `ThumbnailUrl`. The coordinator checks this before calling the thumbnail service. |
| `ApplyDisplayMetadata(ModDisplayMetadata?)` | Applies newly backfilled (or initially joined) metadata; clears the existing thumbnail when the new metadata is adult, has no URL, or carries a different URL than the old thumbnail was loaded from. No I/O, no service call. |

`Refresh()` re-fires `SummaryText` + `SummaryTooltip` (the fallback is
localized) alongside the existing localized members on a culture change.

### `ModItemViewModel` reorder + order-lock state

The row carries lock + drag state pushed down by `ModListViewModel.Reload` (and
the view's gesture, for the transient marker/source flags). No I/O, no service
calls (state-only):

| Member | Meaning |
| --- | --- |
| `bool OrderLocked` | Joined from `ModListEntry.OrderLocked` on reload. A locked row keeps its exact zero-based position; its grip + both move buttons are disabled. |
| `bool CanMoveUp` / `bool CanMoveDown` | Whether the row can move to the previous/next unlocked rank (an unlocked row with an unlocked row above/below). Computed by the parent over unlocked rows on reload; `false` for a locked row. |
| `bool IsGripEnabled` | Derived: `!OrderLocked`. Bound to the grip's `IsHitTestVisible` so a locked grip stops intercepting pointer input and falls through to touch scrolling. |
| `string OrderLockTooltip` | Localized tooltip + click-action text for the order-lock button (lock vs. unlock). |
| `string OrderLockAutomationName` | Localized automation name describing the row's current locked/unlocked state. |
| `bool ShowReorderMarkerBefore` / `bool ShowReorderMarkerAfter` | Set by the view on at most one row while dragging: the accent insertion line anchors before/after that row. |

`Refresh()` also re-fires `OrderLockTooltip` + `OrderLockAutomationName` on a
culture change. The lock toggle (`ToggleOrderLock`) and the reorder commit
(`CommitReorder`) live on `ModListViewModel`; `ModReorderPlanner` is the pure
order-construction helper (rejects same-rank / out-of-range / locked-source /
missing-source without a service call), and `ReorderGestureMath` is the pure
pointer-gesture math (threshold, target rank, marker, lift translation, edge
auto-scroll + clamp),
both unit-tested separately. The order-lock button reads through BOTH shape
(closed vs. open padlock) and color: locked carries a caution-yellow
`CuratorCautionBackgroundBrush` fill + caution-yellow closed-padlock foreground
(preserved on hover); unlocked is a neutral open-padlock with a large shackle
opening. The lifted drag row is realized by lifting the actual item container (a
render transform follows the pointer while the layout slot stays reserved) +
z-index + an opaque/cornered/shadowed style; every mutated container property is
restored on each finish/cancel path. The view-side gesture is single-pointer: a second
press while a row gesture is armed is ignored, and Move / Release / CaptureLost
process only the active captured pointer (by reference); on a release inside the
viewport the target is recomputed from the final release position before the
commit. The live multi-pointer wiring is reviewer/hardware verified (it cannot be
exercised in this suite without Avalonia.Headless, which is not added for this
feature).

### XAML affordances

- **Toolbar density selector.** Two adjacent drawn-icon `Button`s (the `icon`
  + `density` classes) immediately before the Add split button, using Material
  geometry (`view_headline` for Compact, `view_agenda` for Detailed). The active
  one carries the `selected` class, bound to `DetailedRows.IsCompact` /
  `DetailedRows.IsDetailed` (the shell's conditional-class pattern, not a
  `ToggleButton`). A click runs `DetailedRows.SetDensityCommand` with the
  `ModRowDensity` enum value as its parameter. Localized tooltip +
  `AutomationProperties.Name`. A click on the already-active density is a
  strict no-op at the coordinator, so both buttons stay enabled. The rate-limit
  notice column was reshaped to a `*` column with `HorizontalAlignment=Left` so
  the pill keeps its content width at wide widths while still ellipsizing at
  narrow widths (full text in the tooltip) without pushing the density pair or
  Add out.
- **Row template.** A `Panel` hosts two mutually exclusive roots selected by the
  row's `IsDetailed` projection: the existing Compact `Grid` (now with a
  left-edge drag-grip column + an order-lock button beside Move Up / Move Down)
  and a Detailed `Border` (the `detailedRow` style: rounded, low-emphasis). The
  Detailed card is one adaptive
  Grid whose card root carries `Container.Name="detailedModRow"` +
  `Container.Sizing="Width"`, so a `ContainerQuery Name="detailedModRow"
  Query="max-width:680"` in `UserControl.Styles` swaps the layout at the 680-DIP
  card-width breakpoint. Column 0 is the drag-reorder grip, column 1 is the
  thumbnail/placeholder slot, column 2
  holds the name + source badge (row 0) and the summary (row 1), and row 2 is
  the action strip. Wide (card width greater than 680 DIP): a 112-DIP
  `UniformToFill` thumbnail spans all three rows (`RowSpan=3`) and the action
  strip occupies only the content column. Constrained (at or below 680 DIP): the
  thumbnail shrinks to 72 DIP spanning only name + summary (`RowSpan=2`) and the
  action strip moves to a full-width row beneath all three columns
  (`Grid.ColumnSpan=3`). Width, height, row span, and action column/span that
  change at the breakpoint are style-driven (default wide styles + the
  container-query overrides), not local values; constant row/column positions
  stay local. Both roots bind the exact same per-row state + route to the exact
  same code-behind handlers, so no action behavior forks between modes.
- **Summary.** `MaxLines="2"` + `TextWrapping="Wrap"` + `TextTrimming="CharacterEllipsis"`; the full text is retained in `ToolTip.Tip` (when non-null) + `AutomationProperties.Name` (always, so the fallback stays reachable by assistive tech).
- **Thumbnail area.** A rounded `Border` with `ClipToBounds`; the `Image` shows
  only when `HasThumbnail`, otherwise a neutral drawn-geometry placeholder
  (Material `image`) fills the box. The placeholder scales with the slot through
  the same styles as the thumbnail (36 DIP wide; 28 DIP constrained). Adult rows
  never receive a thumbnail (the coordinator skips them), so they fall through
  to the placeholder.
- **Responsive/accessibility.** The Detailed action strip is a `WrapPanel`
  (`ItemSpacing` / `LineSpacing`, `ItemsAlignment="End"` so every wrapped line is
  right-aligned in both states), so controls wrap at the containing edge at
  narrow widths with no clip and no horizontal scrolling; the container query
  moves it between the right column (wide) and a full-width row (constrained)
  through style-driven `Grid.Column` / `Grid.ColumnSpan`, never by duplicating
  the action tree. The name ellipsizes safely (full text in tooltip + automation
  name). All actions remain available at the 720px minimum window width with the
  navigation pane expanded and at the maximum font scale.

## Mod thumbnail service

`IModThumbnailService` is the one focused UI-owned presentation-media service:
it downloads + caches thumbnail images for display in detailed mod rows. It
returns an Avalonia `IImage`, which is why it lives in the UI layer (no backend
library can own an Avalonia bitmap type). Registered as a singleton (decoded
images are kept alive for the app lifetime so multiple rows + reloads share
them).

```csharp
public interface IModThumbnailService
{
    Task<IImage?> GetThumbnailAsync(string? thumbnailUrl, CancellationToken ct = default);
}
```

`GetThumbnailAsync(thumbnailUrl, ct)` returns the decoded image, or `null` on
any expected failure (invalid/non-HTTPS URL, HTTP failure, oversize data, I/O
failure, decode failure). A cache hit (in-memory or disk) serves without a
network round-trip. `OperationCanceledException` propagates to the caller whose
token fired. The service is the focused, documented exception to the rule that
the UI talks to backend services for data: it owns a presentation-media cache.

**Adult-content policy is NOT this service's responsibility.** The caller (the
detailed-rows coordinator) decides whether to request a thumbnail for a given
row (`CanLoadThumbnail` excludes adult rows); the service fetches whatever
trusted HTTPS URL it is handed.

Cache + failure behavior (`ModThumbnailService`):

- **Cache key + root.** Keyed by the lowercase SHA-256 hex of the normalized
  absolute URL (`Uri.AbsoluteUri`). Files under
  `AppPaths.ModThumbnailCacheDir` (`<app-data>/cache/mod-thumbnails`); no
  extension (bytes decoded from the stream contents).
- **HTTPS only.** Non-HTTPS, relative, malformed, or empty URLs return `null`
  without creating the cache dir or touching the network.
- **8 MiB cap.** A declared `Content-Length` over the cap is rejected before
  streaming; a streamed body past the cap aborts mid-copy.
- **Atomic write.** Download to a sibling temp file, then same-volume `File.Move`
  into place. A download failure returns `null` without creating the final file.
- **Decode.** Production uses `Bitmap.DecodeToWidth(stream,
  ModThumbnailService.DecodeWidth, BitmapInterpolationMode.HighQuality)` (256 px,
  sized for the 112-DIP detailed-row thumbnail on scaled displays) on a
  background thread; the render size is responsive (112 DIP wide, 72 DIP
  constrained). The 256-px constant is the single named literal
  (`ModThumbnailService.DecodeWidth`) referenced from the DI wiring.
- **Four-slot load bound.** A `SemaphoreSlim(4)` bounds concurrent distinct-key
  fetch/decode work.
- **Same-URL coalescing.** Concurrent calls for one URL install one shared
  `Lazy<Task<IImage?>>`; losers await the winner. The shared load runs with
  `CancellationToken.None` semantics so **no caller can cancel another's load**:
  every caller (including the installer) awaits the shared task with
  `WaitAsync(ct)`, so a caller's cancellation propagates only to that caller.
  The shared load continues to completion and may populate the disk +
  in-memory caches even when every current caller cancelled.
- **Corrupt-disk retry once.** A corrupt/unreadable cache entry is deleted and
  re-downloaded + re-decoded exactly once; a second decode failure returns
  `null` without another network round-trip. Finalization (publishing a success
  into the in-memory cache + conditionally retiring the in-flight entry by key +
  exact `Lazy` identity) runs INSIDE the shared task's own body, before the task
  completes to any awaiter. A caller that observes `null` (or a fault) therefore
  resumes only after its in-flight entry has already been retired, so an
  immediate retry starts a fresh load instead of re-awaiting the completed
  failed task (load A's finalization never removes replacement load B).
- **Application-lifetime in-memory cache.** Successful decodes are kept alive so
  multiple rows/reloads share one image and no bound row observes a disposed
  bitmap.
- **Per-caller cancellation.** `WaitAsync(ct)` on the shared load. An internal
  `TaskCanceledException` (a timeout, not a caller token) is treated as an
  expected load failure returning `null`.
- **90-day prune.** Once per service instance, best-effort on a background
  thread, deletes ordinary cache files older than 90 days by last-write time.
  Per-file `IOException` / `UnauthorizedAccessException` is logged so one locked
  file does not abort the sweep. Prune failure never blocks startup or image
  loading (the benign prune/load race is accepted: a pruned old file causes the
  normal disk-decode fallback/download path).
- **No `ConfigureAwait(false)`.** Async continuations resume on the captured UI
  context (the UI-layer convention). CPU-bound decode + prune run on `Task.Run`.

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

Applied to the closeable modal dialogs: `ConfirmDialog`,
`DiscoveryEscapeHatchDialog`, `WelcomeWindow`. `ProgressDialog`
(non-closeable by design, `DialogTitleBar.ShowClose="False"`) and the main
window do not opt in, so ESC never dismisses a spinner or exits the app. ESC
bubbles from focused children (TextBox, ComboBox) to the window.

The key decision is factored into the `internal static ShouldClose` pure helper
so it is unit-testable without rendering a window; the KeyDown-to-Close wiring
is rendered UI and covered by code inspection, not a rendered-control test.

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
services.AddSingleton<INxmRegistrationState>(sp => new NxmRegistrationState(  // shared last-known nxm state (before its consumers)
    sp.GetService<INxmHandlerRegistrar>(),
    sp.GetRequiredService<Action<Action>>(),
    sp.GetRequiredService<ILogger<NxmRegistrationState>>()));
services.AddSingleton<UpdateCoordinator>();                 // one-install-at-a-time gate
services.AddSingleton<IAutomaticUpdateService, AutomaticUpdateService>(); // Premium auto-installer
services.AddSingleton<IModThumbnailService>(sp => new ModThumbnailService( // UI-owned thumbnail cache (before the coordinator that injects it)
    sp.GetRequiredService<IHttpClientFactory>().CreateClient,
    cacheDirOverride: null,
    decode: stream => Bitmap.DecodeToWidth(stream, ModThumbnailService.DecodeWidth, BitmapInterpolationMode.HighQuality),
    logger: sp.GetRequiredService<ILogger<ModThumbnailService>>()));
services.AddSingleton(sp => new DetailedModRowsViewModel(     // density coordinator (before ModListViewModel)
    sp.GetRequiredService<IConfigLoader>(),
    sp.GetRequiredService<INexusModMetadataService>(),
    sp.GetRequiredService<IModRepository>(),
    sp.GetRequiredService<IModThumbnailService>(),
    sp.GetRequiredService<ILogger<DetailedModRowsViewModel>>()));
services.AddSingleton<ImportWorkflowViewModel>();            // inline import card (before ModListViewModel)
services.AddSingleton<ModListViewModel>();                   // injects ImportWorkflowViewModel + DetailedModRowsViewModel as children
services.AddSingleton<ProfilesViewModel>(sp => { /* resolves DmfPromptService eagerly */ });
services.AddSingleton<IntegrationsViewModel>();
services.AddSingleton<PreferencesViewModel>();
services.AddSingleton<SettingsViewModel>();
services.AddSingleton(sp => new ShellViewModel(/* … all five page VMs, IAppUpdateService, Action<Action> */,
                                              sp.GetRequiredService<INxmRegistrationState>()));
services.AddSingleton<IDialogService>(sp => new DialogService(/* owner, localization, configLoader */));
services.AddSingleton(sp => new UpdateCheckRunner(/* … incl. IAutomaticUpdateService, StartUpdateCheckPolling */));
#if CURATOR_VELOPACK
services.AddSingleton<IAppUpdateService>(sp => new VelopackAppUpdateService(
    sp.GetRequiredService<IConfigLoader>(),
    sp.GetRequiredService<ILogger<VelopackAppUpdateService>>()));
#else
services.AddSingleton<IAppUpdateService, NoopAppUpdateService>();
#endif
services.AddSingleton(sp => new AppUpdateCheckRunner(/* IAppUpdateService, IConfigLoader, logger */));
services.AddSingleton(sp => new DmfPromptService(/* … */, sp.GetRequiredService<INxmRegistrationState>()));
services.AddSingleton(sp => new OnboardingService(
    sp.GetRequiredService<IAppStateStore>(),
    sp.GetRequiredService<IDialogService>(),
    () => sp.GetRequiredService<ShellViewModel>().NavigateToIntegrationsAsync(),
    sp.GetRequiredService<ILogger<OnboardingService>>()));
```

Key wiring notes:

- `IProfileSession` is registered with a factory that injects the polling
  timer (`StartRunningStatePolling` constructs a `DispatcherTimer` at
  `ProfileSession.PollInterval`). The session is shared by the shell, the
  Profiles destination, the update-check runner, and the DMF prompt
  coordinator.
- The five hosted page view models (`ProfilesViewModel`, `ModListViewModel`,
  `IntegrationsViewModel`, `PreferencesViewModel`, `SettingsViewModel`) are
  registered as singletons (one instance per page, kept alive and subscribed
  for the application lifetime) and injected into `ShellViewModel`.
  `DmfPromptService` is registered BEFORE `ShellViewModel` so the shell's
  factory can resolve it eagerly and inject it as a concrete dependency; the
  coordinator's constructor subscribes to the synchronous
  `IProfileService.ProfileCreated` event, so the subscription exists before
  any profile can be created. The shell consumes the pending trigger on the
  next real navigation into Mods; `ProfilesViewModel` is narrowly coupled to
  profile workflow and does no DMF or mod-list work after Save.
- `INxmRegistrationState` is registered before the VMs/services that inject it
  (`ModListViewModel`, `IntegrationsViewModel`, `DmfPromptService`,
  `ShellViewModel`). It wraps the optional `INxmHandlerRegistrar` (resolved via
  `GetService`: null on platforms without a registrar, which maps to
  `IsAvailable = false` instead of an activation failure) and owns the UI's only
  OS probes (see
  [Shared NXM registration state](#shared-nxm-registration-state)). Only
  `IntegrationsViewModel` still injects the registrar itself, for the
  register/release mutations; the registrar self-guards release (it never
  removes another program's registration; whether it is a no-op or removes
  only Curator's own files depends on the platform state). The composition
  root never auto-registers the handler.
- `OnboardingService` resolves `ShellViewModel.NavigateToIntegrationsAsync`
  lazily through its `navigateToIntegrations` delegate, so the first-run
  Welcome "Set up Nexus" choice navigates to Nexus through the
  shell's standard path (the destination's auth + registration-state refresh
  runs, and leaving it later reloads the mod list).
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
  `ThemeMode`, `ModRowDensity`, `NexusConfig`, `DiscoveryConfig`), `general`
  (`IConfigLoader`, `IAppStateStore`, `LoggingBootstrap`), `profiles`
  (`IProfileService`, `ProfileSummary`, `ModListEntry`, `IModOrderResolver`),
  `mods` (`IModRepository`, `IModImportService`, `ModContainer`,
  `ModDisplayMetadata`, `ModVersion`, `ModVersionPolicy`, `ModSource`,
  `NexusSource`, `UntrackedSource`, `LinkedSource`),
  `integrations` (`INexusAuthService`, `IModAcquisitionService`,
  `INexusModMetadataService`, `IUpdateCheckService`, `UpdateCheckResult`,
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

- **`ShellViewModelTests`**: navigation across all five destinations, default
  Mods selection, compact-pane toggle, same-destination no-op, leave/enter
  lifecycle, dirty-Profiles-draft navigation cancellation, entering Settings
  rehydrates + leaving Settings runs the mod-list + app-update refresh, entering
  Integrations refreshes + leaving cancels auth + reloads the mod list with
  zero additional registration refreshes on any leave, Launch
  CanExecute + execution following `IProfileSession.ActiveProfileId` directly
  (including a live active-id change), the launch result branches
  (Launched / DiscoveryIncomplete / StagingFailed / Error), and
  the nxm handler status (exactly one seed refresh at construction, the strip
  following a shared-state publish, unavailable when no registrar exists).
- **`ShellLaunchAttemptTests`**: the shell-owned launch-attempt state with
  deterministic timing seams (a controllable pre-launch render yield + a
  controllable handoff timeout, no live dispatcher and no real 30-second
  wait): the attempt state disables Launch before the launch service runs, a
  false eager refresh + false polling notification never re-enable it while
  waiting, a later `IsRunning = true` completes the handoff (attempt cleared,
  Launch still disabled by the running gate), the timeout clears the attempt
  and re-enables retry when the game stays absent, failure results keep the
  attempt through the dialog then clear and permit retry, a launch-service
  exception clears it, and a direct concurrent execution is refused (one
  launch call).
- **`LaunchOverlayTests`**: the full-client launch overlay as repository
  source tests over MainWindow.axaml / App.axaml / Strings.resx (the XAML
  parsed as XML): the overlay binds to `IsLaunchAttemptInProgress` while the
  SplitView's `IsEnabled` binds to the inverse, the overlay is the final
  top-layered hit-testable child carrying the scrim brush, the card holds
  the localized title/message + a stock indeterminate `ProgressBar` on
  app-owned brushes, no interactive control or command exists in the
  overlay, declarative accessibility metadata is present, the Launch
  button's surface is unchanged, native window chrome is not suppressed,
  the palette passes WCAG contrast in both themes, and the resx strings
  avoid an em dash.
- **`ShellViewModelAppUpdateTests`**: the status-strip notice (show/hide on
  `IsUpdateSupported` + `LastCheckResult` + dismissal, session-only dismiss,
  the `UpdateStateChanged` marshal), and the notice-click flow (confirm gate,
  download under the progress dialog, apply on success, alert on failure,
  cancel dismisses the notice for the session).
- **`ProfileSessionTests`**: the gate (RequestActive applies only when not
  running), persistence, `CanDeleteProfile`, `ReconcileActive` (delete of
  active clears to null; never auto-selects), `Refresh`.
- **`ProfilesViewModelTests`**: profile create / save / cancel / delete /
  switch, no-active states, running-state gates, dirty navigation, banner /
  picker, inline launch-settings validation + atomic save, and DMF prompt
  timing after create.
- **`LaunchSettingsEditorViewModelTests`**: the reusable inline launch-settings
  editor VM (existing-settings load, add/remove rows, inline localized
  validation -- empty / `=` / NUL name, NUL value, case-insensitive duplicate,
  reserved name, all delegated to the shared `LaunchSettingsValidator` from the
  Profiles library -- plus the `EnableLuaLogs` Logging toggle + the `SkipSplash`
  skip-splash toggle).
- **`ModListViewModelTests`**: enable / disable, reorder, per-mod policy,
  remove (with confirm), auto-sort (identity resolver), the inline import workflow
  integration (child VM exposure, `ItemImported` reload for the active profile,
  no-misdirect for an inactive profile, add-mode stability, end-to-end
  create/activate/import), the linked-folder flow
  (`LinkMods`: peek, collision-refusal, re-link refresh, `LatestPolicy` add;
  `OpenFolder`: launches the file manager at the normalized external path,
  failure alert, no-op for non-linked/broken rows; the linked badge two-state
  available/broken, disabled policy edit, empty update-action cell,
  `IsExternalBroken` on Reload), `CheckCompleted` per-row state,
  `UpdateCommand` success / failure / one-at-a-time / premium gating,
  `CheckForUpdatesNow`, `IsRateLimited` + the coupled `IsRateLimitActive`
  refresh-button/pill gating (server reset + fallback cooldown, precedence over
  the manual throttle), the `NamesChanged` in-place row
  name refresh (refreshed when the flag is set, untouched when it is not), and
  the empty-state Nexus hint (construction + both `Reload` paths perform zero
  registration probes; `IsNxmRegistered` follows the shared state).
- **`ModListOrderLockTests`**: the profile-scoped load-order lock + drag-reorder
  surface through the VM, against the lock-aware `FakeProfileService` projection:
  `OrderLocked` + move/grip availability on reload, `ToggleOrderLock` persists
  without `HasPendingChanges`, locked-row move/drag no-ops, Move Up / Down skip
  locked rows (locked-first-stays-first, crossing locks), drag `CommitReorder` to
  first / middle / last unlocked rank with multiple locks (one `SetModOrder`
  call + exact final order), same-rank / invalid-rank / locked-source /
  missing-source / no-active-profile rejection, and the no-lock move regression.
- **`ReorderGestureMathTests`**: the pure pointer-gesture math (8-DIP threshold
  inclusive at the boundary, target unlocked rank over other-unlocked centers,
  insertion marker before/after/none for up/down/no-op, lift translation
  (pointer delta + scroll-offset delta; pointer-only, scroll-only compensation,
  combined, both directions, zero), edge-band auto-scroll
  direction, and offset clamping to `[0, ScrollBarMaximum]`).
  Also: the `DetailedRows` child VM exposure + `Reload` handoff (joins each
  row's `ModDisplayMetadata` from the container, hands the final row snapshot to
  `DetailedRows.SetRowsAsync` fire-and-forget, empty snapshot on the no-profile
  path).
- **`PreferencesViewModelTests`** + **`PreferencesServiceTests`**: the
  Preferences destination view model and the service that applies theme / font
  scale / language / show-Relay-console and persists.
- **`LocalizationServiceTests`**: the indexer, `Format`, `SetCulture`
  (unknown name -> invariant), the `Item[]` event that refreshes every
  indexer binding.
- **`SettingsViewModelTests`** + **`SettingsViewModelAppUpdateTests`**: the
  Settings destination (the global discovery-mode toggle + the read-only
  automatic / editable manual row contract, the Discover button forcing a
  `Rediscover`, the platform-gated rows, and the open-folder Storage buttons),
  plus the Updates section (current version, manual check + inline status, the
  `UpdateStateChanged` marshal, Download and Restart, the unsupported-build
  disabled controls, and the startup-check toggle persist + pre-fill).
- **`ImportWorkflowViewModelTests`**: the inline import workflow state machine
  (editing, processing, terminal failure), per-item import orchestration with
  the split Task.Run thread boundary, batch advance/close/reset, base-name
  collision and expected/unexpected failure handling, the controllably-blocked
  fake import proving processing state is observable, and the active-profile-
  change-during-processing edge cases (finish-current/abort-rest and
  reset-on-failure).
- **`DiscoveryEscapeHatchViewModelTests`**: the focused escape-hatch form
  (only the missing fields shown), the shared global mode toggle (turning it off
  runs an ordinary Discover + refreshes the rows; turning it on enables editing)
  and the Discover button (forces a `Rediscover`, preserves the mode), plus
  submit/cancel behavior under each mode.
- **`IntegrationsViewModelTests`**: the Nexus destination (OAuth
  login, API-key validate, sign-out), auth controls staying usable while
  Darktide runs, the "Nexus download links" section (status display, register
  confirm / success / failure, unregister delegating straight to the
  self-guarded registrar with no UI-side pre-check probe and exactly one
  post-action state publish, unavailable when no registrar), and `Deactivate`
  (prompt OAuth cancellation on navigation away, idempotent, does not disable a
  later auth attempt).
- **`NxmRegistrationStateTests`**: the production shared-state contract
  (unavailable without a registrar yet a refresh still publishes, the registrar
  read on refresh, a probe throw treated as not-registered, and `Changed`
  marshaled through the UI seam).
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
  opens), the prompt-timing-after-create (the prompt fires from
  `ProfilesViewModel` immediately after the create + activation), the premium
  in-app download, the non-premium / unknown / no-auth browser-open path (opens
  regardless of the registration state; the download-confirm wording follows
  the shared state with zero probes), and the browser-launch failure
  fallback alert.
- **`OnboardingServiceTests`**: the first-run Welcome coordinator (already
  complete no-op, Continue persists + skips Integrations, Set up Nexus
  persists before navigating to Integrations once, the close == Continue
  equivalence, the in-process one-shot guard, and navigation-failure
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
- **`DetailedModRowsViewModelTests`**: config/density normalization (default
  Compact, old config without `ModRowDensity` loads Compact, Detailed
  round-trips through JSON, undefined normalizes to Compact), the
  `SetDensityCommand` (immediate state + persistence via a focused
  read-modify-save, same-density no-op), the coordinator pipeline (Compact
  returns a completed task + clears thumbnails; Detailed starts thumbnails +
  backfill with the ordered container ids), generation-based stale-result
  protection (a profile switch, a Compact toggle, and a superseding reload each
  prevent stale assignment), backfill-driven thumbnail hydration for newly
  eligible rows, the adult-content thumbnail skip (the coordinator never calls
  the thumbnail service for adult rows), and the failure-absorption boundary
  (cancellation absorbed, every other exception logged so a discarded task never
  faults). Also: `ModItemViewModel` display state (`SummaryText` fallback,
  `SummaryTooltip`, `IsAdultContent`, `ThumbnailUrl`, `HasThumbnail`,
  `CanLoadThumbnail`, `ApplyDisplayMetadata` clearing the thumbnail on an
  incompatible-URL/adult change).
- **`ModThumbnailServiceTests`**: HTTPS-only validation (non-HTTPS/relative/
  malformed/empty return `null` without network or cache side effects), the
  lowercase SHA-256 cache key, the 8 MiB cap (declared + streamed), the atomic
  sibling-temp move, the four-slot load bound, same-URL coalescing with
  per-caller cancellation (one caller's `WaitAsync(ct)` cancellation does not
  cancel the shared load, which may still populate the cache), the corrupt-disk
  retry-once path, the app-lifetime in-memory image cache, the 90-day prune, and
  the expected-failure -> `null` paths (HTTP failure, oversize, I/O failure,
  decode failure). Against in-memory fakes (a stub HTTP handler + a fake decode
  seam + a controllable clock), no real network.

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
  `IModAcquisitionService`, `INexusModMetadataService`, and
  `IUpdateCheckService` the UI consumes.
- [profiles](profiles.md): `IProfileService`, `IModOrderResolver`, and the
  profile / mod-list model the UI drives.
- [mods](mods.md): `IModRepository`, `IModImportService`, `ModDisplayMetadata`,
  and the source / version-policy model the UI reads.
- [config](config.md): `CuratorConfig`, `PreferencesConfig`, `ThemeMode`,
  `ModRowDensity`, `NexusConfig`.
