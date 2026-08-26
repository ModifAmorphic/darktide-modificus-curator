# UI (`Modificus.Curator.UI`): reference

> The Avalonia 12 front end of Modificus Curator. Owns the SplitView shell with
> five hosted destinations (Profiles, Mods, Nexus, Preferences,
> Settings), profile management, the mod list + its download rows, every true
> modal (Welcome,
> confirm, import, discovery escape-hatch, alert, progress), global preferences
> (theme, font scale, language), the i18n infrastructure, the serial Nexus
> download queue (the one download engine behind nxm clicks, premium update
> installs, the automatic batch, and the DMF download), the DMF install-prompt
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
  (toggle/move/policy/remove/add/link/update); cleared on the next
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

- The active id, restored from `IProfileActivationState` at startup (straight into
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
        IProfileActivationState appState,
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
  destination is a strict no-op (so a queued modal survives same-destination
  clicks; it runs only on a real navigation into its destination). For a real
  change: (1) leaving Profiles awaits the unsaved-changes
  three-choice guard (`ProfilesViewModel.ConfirmCanNavigateAwayAsync`), and
  Cancel/ESC/X or a Save that the service rejected keeps everything
   unchanged; (2) run the current destination's leave effects (Nexus
   Integration: `Deactivate` + mod-list reload, with no registration probe on
   the way out; Settings:
   mod-list reload + re-read `CheckOnStartup` + refresh the app-update notice);
  (3) switch `CurrentDestination`; (4) run the target's enter effects (Settings:
  `RefreshFromConfig` synchronously; Nexus: await `RefreshAsync`); (5) drain
  the shell-owned modal queue for the entered destination, so a queued modal
  (the DMF install prompt after a profile create) runs as the topmost modal
  over the freshly painted page. The destination is switched before any enter
  await so it stays active even if a refresh or a drained modal reports an
  error.
- `IShellNavigation` (implemented by the shell, registered as a lazy forward
  to the shell singleton): the guarded navigation surface UI-layer services
  consume; the first-run onboarding reuses it for its "Set up Nexus" choice, so
  onboarding-completion persistence and Integrations activation share one
  navigation path.
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
  AND the spawned Relay process to exit (or a 30-second timeout, started only
  after the spawn returns, releasing the whole combined wait; a false
  polling result never clears it). Shell-owned and distinct from
  `IsGameRunning`: the attempt covers the process-detection gap the session's
  detector cannot yet see. A method-level guard refuses a second,
  direct/programmatic execution while an attempt is active. The state clears
  in all completion and exception paths; on the `Launched` path only after
  the handoff resolves (game observed and Relay exited -> the ordinary
  running gate keeps Launch disabled; timeout with the wait unresolved ->
  retry is possible). While
  the state is true, the shell also shows the full-client launch overlay (a
  scrim + centered indeterminate progress card layered over the disabled
  shell inside `MainWindow`; see the `MainWindow` section). The button's
  text, tooltip, and accessible name never change. The pre-launch yield
  (production: one Avalonia dispatcher yield at
  `DispatcherPriority.Loaded`, after layout + render and before subsequent
  input, so the disabled style + overlay paint before the synchronous launch
  work resumes) and the handoff timeout are injected delegates, so unit tests
  run deterministically without a live dispatcher or real waiting. The wait
  observes the existing session signal + the launch facade's relay-exit task
  (from `LaunchResult.RelayExited`; a null task behaves as already complete):
  subscribe-before-check, the temporary subscription removed
  deterministically, the combined conditions awaited against the single
  timeout. Bounded detector handoff, not process supervision (no process
  handle is taken; the facade owns the spawned handle and its disposal, and
  Darktide stays untracked beyond the session signal).
- `LaunchCommand` result handling, including the game-dir consent chain: a
  `GameDirConflict` result shows the two-choice game-dir conflict modal
  through `IDialogService.ShowGameDirConflictAsync`. Rename performs the
  consented `IGameDirModsHost.TakeOver(result.GameDirPath)` (returning the
  renamed entry's path), shows a one-line notice carrying it, and retries the
  launch once (the notice precedes the retry so the information survives a
  later launch failure; a null return skips the notice and still retries);
  Cancel aborts. The retry is
  one-shot per consent: a second conflict in the same attempt chain surfaces
  the standard error alert instead of another prompt, so the flow can never
  loop. A takeover failure surfaces an alert (the exception's message after
  the localized framing) with no retry. The attempt state holds through the
  modal + notice + retry exactly like the failure dialogs. The other statuses
  are unchanged: `DiscoveryIncomplete` opens the escape hatch (no retry),
  `StagingFailed` + `Error` show alerts, and `Launched` runs the running-state
  handoff described above.

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

    public MainWindow();                               // XAML runtime/designer path (no store)
    internal MainWindow(IMainWindowStatePersistence stateStore);    // production path

    internal static double ComputeOpenPaneLength(double widestLabelWidth);
    internal static bool TryConvertWorkAreaDip(
        double scaling, double pixelWidth, double pixelHeight,
        out double widthDip, out double heightDip);
}
```

The Avalonia main window. Owns only view mechanics: SplitView pane sizing, the
no-profile handoff link, the full-client launch overlay (in
`MainWindow.axaml`), and the persisted-window-geometry wiring (the state
machine itself lives on the `WindowGeometryTracker`, below). State, navigation, and
service calls stay in `ShellViewModel`. The public parameterless constructor
loads XAML + safe in-memory defaults and is the Avalonia runtime/designer
loader path (it performs no store IO and locates no service). Production
construction goes through the internal `MainWindow(IMainWindowStatePersistence)` overload,
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
  `IMainWindowStatePersistence.MainWindowState` on the production path, validated + clamped
  by `WindowGeometryTracker.SeedPersisted` (the pure `NormalizeSavedSize`
  policy inside the tracker) to the XAML minimums (`MinWindowWidth`
  / `MinWindowHeight`) and, when available, the primary screen's working area
  converted from physical pixels to DIP via `Screen.Scaling` (the pure
  `TryConvertWorkAreaDip` validates finite + positive scaling and dimensions),
  then applied as `Width`/`Height` before first Show so the platform has the
  right restore size. The persisted maximized flag seeds the tracker's
  meaningful-state flag and its one-shot first-open maximize (consumed by
  `OnOpened`); when the flag is set, the window maximizes once (after Show)
  for Win32/X11 consistency, so a later unmaximize restores to the saved
  Normal size. The observation state machine (deferred, coalesced,
  reason-aware tracking; the meaningful-state policy; the #19431 correction
  decision) lives on the `WindowGeometryTracker` below; `MainWindow` feeds it
  from `OnResized` and `OnPropertyChanged` and keeps only the Window
  operations (apply size, one-shot maximize, the single close-time persist
  write through `PrepareClose`).
- **Avalonia #19431 visible-restore correction.** At Windows scaling such as
  175%, a Maximized to Normal transition can emit a correct `Unspecified`
  Normal resize followed by a stale `Layout` resize carrying the maximized
  `ClientSize`. `MainWindow` uses manual top-level sizing, so a post-open
  `Layout` resize is not a user sizing intent. The tracker decides (its pure
  `ShouldCorrectFromLayout` policy, after the trusted candidate has been
  resolved into the last Normal size) whether a post-open `Layout` observation
  that materially conflicts (more than the 1.0 DIP tolerance) while Normal
  should trigger a reapply of the trusted size through `ClientSize`, surfaced
  as the tracker's `CorrectionRequested` event. The
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

### `WindowGeometryTracker`

```csharp
internal sealed class WindowGeometryTracker
{
    internal const double CorrectionTolerance = 1.0;

    public WindowGeometryTracker(Size defaultNormalSize, Action<Action>? post = null);

    public Size LastNormalSize { get; }
    public bool LastMeaningfulMaximized { get; }
    public event EventHandler<Size>? CorrectionRequested;

    public bool SeedPersisted(AppWindowState? saved, Size? workAreaDip, double minWidth, double minHeight);
    public bool ConsumeMaximizeOnFirstOpen();
    public void NotifyOpened();
    public void ObserveWindowState(WindowState state);
    public void ObserveResize(Size clientSize, WindowResizeReason reason);
    public void PrepareClose(WindowState closingState);

    internal static (double Width, double Height) NormalizeSavedSize(...);
    internal static bool NextMeaningfulMaximized(WindowState current, bool previous);
    internal static bool IsTrustedResizeReason(WindowResizeReason reason);
    internal static Size ResolveTrustedNormal(Size? trustedCandidate, WindowState state, Size current);
    internal static bool ShouldCorrectFromLayout(...);
    internal static bool PersistedSeedsMaximized(AppWindowState? saved);
}
```

The main window's geometry state machine, extracted from `MainWindow` so it is
unit-testable headless. The window feeds observations (`ObserveResize` /
`ObserveWindowState` / `NotifyOpened`) and queries actions (`SeedPersisted`'s
normalized size, `ConsumeMaximizeOnFirstOpen`, `PrepareClose`'s close-path
snapshot, and the `CorrectionRequested` #19431 reapply); the tracker never
touches a `Window`. The deferred apply posts through the injected `post` seam
(the UI dispatcher in production, captured or inline in tests). The pure
policy statics (`NormalizeSavedSize`, `NextMeaningfulMaximized`,
`IsTrustedResizeReason`, `ResolveTrustedNormal`, `ShouldCorrectFromLayout`,
`PersistedSeedsMaximized`) are the unit-testable seams the
`WindowGeometryTrackerTests` exercise alongside the state-machine behaviors
(deferred/coalesced applies, Layout never authoritative, the end-to-end
#19431 burst, correction non-recursion, the close path).

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
    Task<GameDirConflictChoice> ShowGameDirConflictAsync(string title, string message);
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
```

Seven true-modal methods: the first-run Welcome, a binary confirm, the launch
discovery escape hatch, a single-button alert, an unsaved-changes three-choice
prompt, the game-dir conflict prompt, and a non-dismissable
progress spinner. Copied local-import failures + the edit-import-details
correction surface live inline in the `ImportWorkflowView` card (not through
this seam); the linked-folder flow continues using `ShowAlertAsync` for its
failures.

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
  button, no cancel). Used to surface a launch `Error`, an nxm gate failure
  (no auth / no active profile / non-Darktide link; there is no download row
  to host those on), a linked-folder failure, or the DMF informational case
  where there is nothing for the user to decide, only acknowledge.
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
- `ShowGameDirConflictAsync(title, message)`: the game-dir conflict prompt
  (left to right: Cancel, Rename; Rename is the
  accent button). Shown when a launch returns `GameDirConflict` (a foreign
  entry occupies the game-dir `mods` slot; nothing was mutated). The
  `GameDirConflictChoice` enum defaults to `Cancel`, so ESC, the title-bar
  close, and a window close behave like the explicit Cancel button. Caller-
  side semantics: Rename performs `IGameDirModsHost.TakeOver` (whose return
  value drives the rename notice) + one retry, Cancel aborts.
- `ShowProgressAsync<T>(title, message, work)`: a buttonless, non-closeable
  modal spinner over the supplied async work. The user cannot dismiss the
  spinner: the work runs to completion and the caller surfaces its result.
  The work's exception (if any) propagates to the caller; the spinner is
  closed in either case. Used for the app self-update download (mod downloads
  render as rows on the mod list through the download queue instead).

### `DialogService`

```csharp
public sealed class DialogService : IDialogService
{
    public DialogService(Window owner, LocalizationService localization, IDiscoveryEscapeHatchFactory escapeHatchFactory);
}
```

The concrete implementation. `owner` is the main window (a singleton; resolved
by the desktop lifetime and by `DialogService` for modal parenting).
`localization` is handed to the Welcome title. `escapeHatchFactory` builds the
one dialog VM with service dependencies (the discovery escape hatch; see
`IDiscoveryEscapeHatchFactory` below), so the service constructs no view
models itself. `DisableOwnerForModal` is the nesting-safe owner-
disable workaround (a reference count tracks overlapping modals; the owner
re-enables only when the outermost modal closes).

### `IDiscoveryEscapeHatchFactory`

```csharp
public interface IDiscoveryEscapeHatchFactory
{
    DiscoveryEscapeHatchViewModel Create(IReadOnlyList<string> missingFields);
}
```

The narrow per-dialog factory for the one dialog VM with service dependencies:
the escape-hatch VM needs the live `IConfigLoader`, `ISteamService`,
`LocalizationService`, and `IGamingModeState`, none of which the
`DialogService` otherwise has a reason to hold. Registered in
`CuratorComposition`; returns the VM (not the Window) because the dialog's
result lives on the VM and the VM-to-Window pairing belongs to the code that
shows the Window. Deliberately not a generalized all-dialogs factory: the
other dialogs need no VM dependencies.

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

### `LocalizedViewModel`

```csharp
public abstract class LocalizedViewModel : ObservableObject
{
    protected LocalizationService _localization;

    protected LocalizedViewModel(LocalizationService localization);

    protected abstract IReadOnlyList<string> LocalizedProperties { get; }
    protected virtual void OnCultureChanged();
    protected void DetachLocalization();
}
```

The shared culture-refresh mechanism for localized view models: subscribes
once, and on a culture change re-fires the derived VM's registered localized
property names (one `LocalizedProperties` declaration per VM, next to the
properties) plus the `OnCultureChanged` hook for the non-list work a few VMs
genuinely do (the mod list's per-row refresh + gate re-render, Integrations'
state re-resolve, Profiles' editor validation refresh). Transient dialog VMs
call `DetachLocalization` on close so they are collectable against the
application-lifetime service. Deliberately tiny (no caching, no lookup
helpers) so it cannot become a dumping ground. The row VM
(`ModItemViewModel`) keeps its parent-driven `Refresh()` pattern instead.

A source-scan unit test (`LocalizedViewModelRegistrationTests`) reads every VM
source file, finds every property getter that indexes `_localization[...]`,
and fails when such a getter is not in its class's registered refresh list
(`LocalizedProperties`, or the Refresh re-fire list for `ModItemViewModel`) and
when a class with localized getters is outside the known VM set, so the
forget-to-register failure is a red test rather than stale text.

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
the app starts with `IOnboardingState.OnboardingCompleted` still `false`, persists
completion, and navigates the shell to Nexus on a "Set up Nexus"
choice. After the first run, the call is a no-op for the lifetime of the process.

```csharp
public sealed class OnboardingService
{
    public OnboardingService(
        IOnboardingState appState,
        IDialogService dialogs,
        IShellNavigation navigation,         // the shell's guarded navigation surface
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
  via the injected `IShellNavigation`.
- `IShellNavigation`: implemented by `ShellViewModel` and registered by the
  composition root as a plain forward to the shell singleton (resolved lazily,
  no construction-time cycle), so the destination's auth +
  registration-state refresh runs and the leave-Integrations mod-list reload
  applies after the Welcome-driven visit too. An interface (not a delegate) so
  the seam is a named capability the shell owns.
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
Subscribes to `IProfileService.ProfileCreated` at construction (composition
resolves it once at startup, before the window shows, so the subscription
exists before any profile can be created; nothing depends on the coordinator)
and enqueues its prompt onto the `IShellModalQueue` for the Mods destination;
the shell's `NavigateAsync` drains the queue after the destination switch +
enter effects, so the DMF prompt runs as the topmost modal with Mods already
selected underneath. The drained delegate runs the prompt (fail-isolated) and
reloads `ModListViewModel` itself (the reload is the enqueuer's business). A
queued entry survives visits to other destinations, runs once, and a newer
create replaces an unconsumed entry (newest-wins).

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
        IGamingModeState gamingMode,
        IExternalLauncher externalLauncher,
        IShellModalQueue modalQueue,
        IModListRefresh modListRefresh);
}
```

- `DmfModId`: the Nexus mod id of Darktide Mod Framework (8). DMF is
  required for most Darktide mods; the prompt offers to install it when
  missing.

### The shell modal queue (`IShellModalQueue`)

```csharp
public interface IShellModalQueue
{
    void Enqueue(object owner, ShellDestination showOn, Func<Task> modal);
    Task DrainAsync(ShellDestination destination);
}
```

The shell-owned queue of deferred modal operations: a service that needs a
modal to run the next time the user enters a particular destination enqueues
it here instead of coupling the shell to the service; the shell drains the
queue in its navigation lifecycle, after the destination switch + the enter
effects, so the page is painted underneath the modal. A queued entry for
destination X runs once, survives visits to other destinations in between,
and a newer enqueue from the same owner replaces that owner's unconsumed
entry (newest-wins); different owners queue independently. `DrainAsync`
consumes the matching entries before running any (an exception inside one
modal cannot re-fire it) and awaits each sequentially in enqueue order. UI
thread only. Single implementation (`ShellModalQueue`, an application-lifetime
singleton).

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
   download enqueued onto the shared download queue: the concrete head file is
   resolved first (`IModAcquisitionService.ResolveLatestNexusAsync`, one
   file-listing call, no download) so the queue's dedupe key is real and the
   download fetches the exact file the user was offered at confirm, then the
   download row owns progress and the queue's completion owns the add +
   reload; a resolve failure (no row exists to host it) surfaces the localized
   alert and enqueues nothing. Everyone else (no auth,
   regular, or unknown premium state) gets the DMF Nexus files page
   (`https://www.nexusmods.com/<darktide-domain>/mods/8?tab=files`) opened in
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

The browser-open runs through the shared `IExternalLauncher` (General library),
so tests exercise the failure path with an in-memory fake instead of launching
a real browser.

## The update check runner

### `UpdateCheckRunner`

The UI-layer glue between `IProfileSession` (the active-profile authority)
and `IUpdateCheckService` (the Integrations update check). The check itself
is backend-only; this runner owns when the UI fires it. The runner also owns
the candidate pull: each fire reads the profile's mod list through
`IProfileService` inside its thread-pool task and maps the entries to
`ModListCandidate` records (container id + policy, via one small internal UI
extension) at the call site, so Integrations holds no Profiles dependency. A
pull failure (a deleted or unreadable profile) is logged and the run skipped:
no check call, no `LastResult` mutation. After each check completes, the
runner captures the exact result (not a potentially raced `LastResult`) and
chains the `IAutomaticUpdateService` (the opt-in Premium automatic batch) on
the captured UI context, so a manual CheckNow keeps its spinner active
through the head resolves + enqueues (the installs themselves run on the
download queue afterward). The check flags mods via three tiers (the server's
`viewerUpdateAvailable`, a mod-level version compare, and a latest-file-version
confirmation that clears tier-2 false positives against the actual latest
file); see
[the update-detection tiers](rate-limiting-strategy.md#update-detection-tiers).

```csharp
public sealed class UpdateCheckRunner
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    public UpdateCheckRunner(
        IProfileSession session,
        IProfileService profiles,           // the candidate pull
        IUpdateCheckService updateCheck,
        IConfigLoader configLoader,
        IUpdateCheckScheduleState appState,
        IAutomaticUpdateService autoUpdate,
        ILogger<UpdateCheckRunner> logger,
        Action<Action>? startTimer = null,
        Func<DateTimeOffset>? getNow = null,
        Action<Action>? invokeOnUi = null,          // the gate's StateChanged marshal
        Action<Action>? startCountdownTimer = null, // the gate's 1-second countdown
        Action? stopCountdownTimer = null);

    public UpdateRefreshGate RefreshGate { get; }
    public DateTimeOffset? NextManualRefreshAllowedAt { get; }

    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    public void Start();
    public Task CheckNowAsync();
}
```

- `RefreshGate`: the runner-owned refresh-gate policy (see
  [UpdateRefreshGate](#updaterefreshgate)). Every check result the runner
  captures is fed into it (`ApplyResult`), and a throttled manual attempt
  re-evaluates it so the countdown engages; the mod-list VM renders its state
  through the marshaled `StateChanged` event.
- `CheckCompleted`: the UI-facing re-raise of the check completion (the
  underlying `IUpdateCheckService` event), marshaled to the UI thread through
  the injected `invokeOnUi` seam. The runner is the sole driver of the check
  service, so the mod list subscribes here instead of holding the service;
  the raw service's own event stays untouched for its own subscribers.
  (Install completions are not re-raised here: they surface through the
  download queue's own `UpdatesApplied` event, which the mod list consumes
  directly.)

- `TickInterval`: the periodic timer's fixed tick granularity (1 minute).
  The user-configured interval
  (`CuratorConfig.Nexus.AutoUpdateCheckIntervalMinutes`) is honored to this
  granularity: the runner fires when that much time has elapsed since the
  last check, checked on each tick.
- `Start()`: seeds the last-check timestamp
  (`IUpdateCheckScheduleState.LastUpdateCheckUtc`) and the manual throttle's sliding window
  (`IUpdateCheckScheduleState.ManualRefreshTimestamps`) from the persisted store,
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
(`IUpdateCheckScheduleState.LastUpdateCheckUtc`) and seeded at `Start()`, so the interval
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
(`IUpdateCheckScheduleState.ManualRefreshTimestamps`), seeded at `Start()` and written back
on every successful fire, so closing and reopening the app does not reset the
free-refresh budget. See
[the rate-limiting strategy](rate-limiting-strategy.md) for the thresholds.

### `UpdateRefreshGate`

The refresh-gate policy for the manual "check now" affordance, owned + exposed
by the runner. Absorbs everything the list VM used to compute itself: the
rate-limit tracking (fed by every check result the runner captures), the
effective-reset computation (the server-reported reset governs; a 1-minute
client-side fallback when Nexus sent no reset, e.g. an HTTP 429 with no
`x-rl-*` headers), the manual-throttle read
(`NextManualRefreshAllowedAt`), the shared 1-second countdown timer lifecycle,
and the functional decisions.

```csharp
public sealed class UpdateRefreshGate
{
    public void ApplyResult(UpdateCheckResult? result);  // the runner's feed
    public void Reevaluate();                            // results, blocked attempts, ticks

    public bool IsRateLimited { get; }                   // the last result was rate-limited
    public DateTimeOffset? RateLimitResetsAt { get; }    // the raw server reset
    public bool IsRateLimitActive { get; }               // blocked until the effective reset elapses
    public bool IsManualThrottled { get; }               // the sliding-window cooldown holds
    public DateTimeOffset? ManualThrottleClearsAt { get; }
    public bool IsRefreshEnabled { get; }                // !IsRateLimitActive && !IsManualThrottled

    public event Action? StateChanged;                   // marshaled to the UI thread; readers pull
}
```

- The list VM keeps ONLY the localized rendering: the tooltip priority
  (rate-limit > throttle > normal), the `m:ss` countdown format, and the
  `IsCheckingNow` affordance. It re-fires its bound properties when
  `StateChanged` fires and composes `IsRefreshEnabled` with its own
  `IsCheckingNow`.
- The header rate-limit pill is coupled to the refresh button, not the raw
  result flag: the pill reads "Refresh disabled due to rate-limiting" exactly
  while `IsRateLimitActive` holds, and both clear together the moment the
  effective reset passes (each 1-second countdown tick re-evaluates). The
  rate-limit reason takes tooltip precedence when both the rate limit and the
  manual fire-count throttle are active; the two causes share one countdown
  timer, so either keeps the button disabled.

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
  `accessToken: null`, `prerelease: false`, stable releases only); a set value
  (a local directory path or a URL) builds the manager from `UpdateManager`'s
  `urlOrPath` overload instead, the local-testing / self-hosted-feed path with
  no code change. Both constructions pass
  `UpdateOptions { AllowVersionDowngrade = true }`, so the latest stable
  release is offered even when semver-older than the installed version.
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

## The download queue

The one Nexus download engine: a serial, FIFO, deduplicated queue
(`ui/Session/ModDownloadQueue.cs`) that owns every acquisition end to end. All
three download paths run through it: the `nxm://` click (the
`NxmModDownloadHandler` enqueue adapter, above), the manual per-row Premium
update action, and the automatic Premium batch (both through
`ModUpdateEnqueuer`). There is no separate update installer: the queue's
single worker is the one-download-at-a-time gate, an update and an nxm click
can never hold two acquisitions at once, and a click for a file already live
in the queue joins the existing item and pulses its row.

```csharp
public interface IModDownloadQueue
{
    ObservableCollection<DownloadItem> Items { get; }   // admission order; UI-thread mutations
    event Action<DownloadItem>? ItemChanged;            // admit / resolve / terminal, on the UI thread
    event EventHandler? UpdatesApplied;                 // after a successful UpdateInstall completion

    DownloadItem Enqueue(ModDownloadRequest request);   // thread-safe; joins + pulses a same-key live item
    void Cancel(DownloadItem item);                     // queued: drop; active: token-authoritative
    void Dismiss(DownloadItem item);                    // Failed-only
    DownloadItem Retry(DownloadItem item);              // re-issues the identical request
}

public sealed record ModDownloadRequest(
    string GameDomain, int ModId, int FileId,
    DownloadPurpose Purpose,            // ProfileAdd (nxm click, DMF) | UpdateInstall
    Guid? ContainerId, string DisplayName,
    Guid TargetProfileId, string TargetProfileName,   // captured at enqueue; display-only name
    string? NxmKey = null, long? NxmExpires = null,   // the nxm per-file tokens
    string? ExpectedVersion = null);                  // UpdateInstall eligibility input
```

- **Dedupe**: the key is (game domain, mod id, file id), case-insensitive on
  the domain. A second click on a non-terminal item is a join: no new item, no
  new download, one pulse counter increment the row renders as a flash.
- **The worker pipeline** (per item, on the single worker): dequeue-time auth
  re-check (a sign-out between enqueue and dequeue fails the item inline);
  UpdateInstall eligibility revalidation via `UpdateEligibility` (a stale flag
  is a silent no-op, not an error row); the repository hit check (the exact
  `FileId` against every version of the mod's container; a hit completes with
  no network); the miss path (`AcquireFromNexusAsync` with the item's token,
  progress wired to the row, the cancellation token passed in); then the
  per-purpose completion.
- **Policy rule (both paths)**: a head file (the matched version's `IsLatest`,
  or the acquisition's `IsHeadFile`) registers `LatestPolicy`; a non-head file
  registers `PinnedPolicy` pinned to the clicked version. A ProfileAdd for a
  container already in the target profile applies via `SetModPolicy` (`AddMod`
  would no-op on policy; the user's click must win); a fresh container takes
  `AddMod`. A deleted target profile or a mod removed mid-flight fails the row
  inline.
- **Completions**: ProfileAdd acknowledges the install best-effort and reloads
  the mod list when the target is still the active profile (through the lazy
  `IModListRefresh` seam); UpdateInstall acknowledges once and raises
  `UpdatesApplied` (the queue raises it on the UI thread; `ModListViewModel`
  consumes it directly to flag the session pending + reload).
- **Cancellation is token-authoritative**: `Cancel` cancels the item's token
  synchronously and marshals only the presentation; the worker re-checks the
  token at dequeue (a queued cancel never starts the acquisition) and honors
  it mid-acquisition, so no phase-write race can resurrect a cancelled
  download. Completed and cancelled items leave the collection; failed items
  stay until dismissed or retried.
- **Threading**: `Enqueue` is safe from any thread; every item state and
  collection mutation publishes through the injected `Action<Action>` marshal
  seam, so `Items`, `ItemChanged`, and the item property notifications are
  observed on the UI thread. The worker itself is an explicit `Task.Run`
  loop.

Downloads render as rows in the mod list (see
[Download rows](#download-rows)); there is no popup, flyout, or modal spinner
for a mod download.

### `ModUpdateEnqueuer`

The enqueue front for premium mod-update installs. Both callers (the manual
per-row update action and the automatic Premium batch) resolve the mod's head
file through `IModAcquisitionService.ResolveLatestNexusAsync` (one file-listing
call, no download) and admit one `UpdateInstall` item onto the shared queue,
so the queue's dedupe key is the real head file. The head resolve is the one
step with no row to host a failure on: its exceptions propagate to the caller
(the manual path surfaces the localized failure alert; the batch aggregates
resolve failures into its summary alert). Once an item is admitted, failures
land on the row.

### `IAutomaticUpdateService`

The opt-in Premium automatic update batch, chained directly from
`UpdateCheckRunner` after a check completes (the runner captures the exact
result, not a potentially raced `LastResult`). Independent of
`ModListViewModel` (to avoid the existing ModListViewModel -> UpdateCheckRunner
dependency becoming circular). The installs are `UpdateInstall` items admitted
onto the download queue through `ModUpdateEnqueuer`; the service owns only the
gates, the enqueue batch, the stop/cancel-on-profile-switch policy, and the
aggregated resolve-failure alert.

```csharp
public interface IAutomaticUpdateService
{
    Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default);
}
```

- `RunAfterCheckAsync`: gates on the result's outcome being authoritative
  `Success` with updates, `NexusConfig.AutomaticUpdatesEnabled` being on, the
  active profile still matching, and a fresh `GetCurrentStateAsync` returning
  `IsPremium == true` (the Premium request fires ONLY when the gates pass, so
  an empty result or a disabled setting costs no extra API call). Then runs
  the enqueue batch: each iteration re-checks the active profile (a switch
  stops scheduling further entries and cancels the still-queued items admitted
  for the left profile; an item the worker already started completes under its
  own rules), resolves the head file, and admits one item. A download failure
  renders inline on its row (the queue's Failed phase with retry), so only
  resolve failures (no row exists to host them) surface here, as one
  aggregated localized summary alert naming the mods; a fully successful batch
  is silent beyond the download rows. The reload signal is the queue's own
  `UpdatesApplied` event, which `ModListViewModel` consumes directly.

This is independent of `NexusConfig.AutoUpdateCheckEnabled`: periodic checking
being off never disables automatic installation (startup + switch + manual
checks still drive it), and changing the periodic-check toggle never clears a
configured `true` here.

## The inline import card

The `ImportWorkflowViewModel` + `ImportWorkflowView` own two exclusive modes
over one shared editing form (the same fields, the same
`ImportSourceValidator` rules), rendered in two places:

- **Batch mode** (the top card below the toolbar, gated by the workflow's
  `IsBatchActive` projection): the ordered import of picked/dropped paths. The
  per-item form (name, source, conditional Nexus version/URL/policy), the
  three-state lifecycle (editing, processing, terminal failure), and the
  per-item orchestration (`GetBaseName` + `Import` on `Task.Run`; the profile
  queries and writes on the captured UI context). Emits
  `ItemImported(profileId)`; the mod list reloads when the captured profile is
  still active. A batch edits many mods, one item at a time, so it stays at
  the top.
- **Edit mode** (an in-row band on the edited row): the per-container
  correction surface for a mod's import details (name, source association,
  release tag), entered from a row's pencil button via
  `ModListViewModel.EditImportDetailsCommand` -> `StartEdit(containerId)` and
  applied through the repository's `EditImportDetails` primitive. The band is
  a leading section inside the row template (an ItemsControl cannot host
  injected elements between items, so "right above the row" lives in the row
  markup): ONE shared `ContentControl` + `ModRowEditBandTemplate` precedes
  both density roots, hosting the SAME `ImportWorkflowView` the top card
  uses (the removal-confirm stage + the failure area ride inside it). The
  band follows the `ActiveDownload` morph pattern: the parent assigns the
  row's `IsEditTarget` flag + `EditBandContext` (the workflow VM) from the
  workflow's `EditTargetContainerId` (the shared child subscription, the
  `IsListToolingEnabled` propagation shape) on activation + on every reload,
  so the form instantiates only on the editing row and a mid-edit reload
  re-attaches the band to the rebuilt row instance for the same container.
  Opening the band brings its row into view (the realized container's
  `BringIntoView`, posted at Loaded priority). While the band is open the
  row is anchored like an order-locked row (the grip is not hit-testable,
  the move commands refuse, and other rows' drag math skips it as a
  destination; the enabled toggle, policy, lock, and remove stay live), and
  a download morph arriving on the edited container closes the edit
  automatically (the container became downloaded = not editable; the morph
  is the visible explanation). The form itself: prefilled from the container
  (name, source choice, the bare mod id, the latest version's tag); the
  policy picker hides (policy is per-row, not import details); Save applies
  the primitive with the same validation the batch form enforces (a version
  is required when saving as Nexus, so the edit can never create a
  version-unknown state; switching to Untracked clears the version field);
  Cancel deactivates the band. Downloaded mods are not editable: a version
  carrying a FileId OR a RemoteUploadedAt grounds the container, the row's
  pencil is hidden (its always-laid-out slot is preserved, so the strip
  geometry never shifts), and both `StartEdit` and the primitive refuse
  (defense in depth; no degraded fields, no band). The name field is
  editable only for the Untracked choice, and locks as read-only (never
  disabled) for the Nexus choice; the id, version, and source switch stay
  editable for an ungrounded container. A multi-version identity change
  swaps the form for the inline removal-confirm stage (never a nested modal),
  with the save-time state refresh and the typed
  `RemovalConfirmationRequiredException` catch covering a version landing
  while the band is open. Refused saves and disk failures surface inline in
  the band's status area with the form still editable. A successful save
  deactivates the band and raises `ImportDetailsEdited(containerId)`, the
  mod list's reload signal (the edited container's name, source, and version
  can all have changed).

The modes are mutually exclusive, and the exclusion is symmetric with the
load-order card below through the shared `ModCardsGate` (each card VM reports
its activity to the gate and refuses to start while any other card is open;
neither VM references the other). The gate is also the one any-card source
behind `IsAddEnabled`, `IsListToolingEnabled`, and the view's picker/drop
entry guards. The card is an application-lifetime singleton child VM
registered before `ModListViewModel`; navigating away from Mods preserves an
in-flight card.

## The load-order import card

The `LoadOrderImportViewModel` + `LoadOrderImportView` card, hosted below the
import-workflow card (always the top-below-toolbar card; it edits the whole
list, never one row, so it never renders as an in-row band). Entry: the Add
split button's sticky fifth mode, "Import load order" (a txt file picker; the
primary click reopens it; Gaming Mode inherits the Add button's disable).
The picker's file feeds `StartImport(path)`:

- **Activation**: read + parse the file (`ModLoadOrderParser`, the DML-exact
  reader), reconcile it against the active profile and repository
  (`ILoadOrderReconciler`), and open the review table. Refuses while any
  other hosted card is active (the shared gate), with no active profile, or
  on an unreadable file (localized alert). A profile switch mid-review
  resets.
- **The table**: one compact ordinary-controls row per file line (file
  order): the folder name, the match (the mod's display name, or `-`), the
  localized outcome ("will be reordered" / "can be added" / "not found"),
  an include checkbox (reorder lines default checked, add lines default
  unchecked, unresolved lines disabled + unchecked), two reserved mod
  id + version columns for the resolver tiers (fixed widths in the shared
  header + row layout, so activating their cells never reshuffles the
  table; no edit behavior exists yet), and an open-on-Nexus link on
  unresolved rows (the folder name as the search keyword,
  `IExternalLauncher`, fallback alert on failure). Unmatched names are
  fully visible, never dropped.
- **The sibling tier** (the migration path): the picked txt's own
  directory is scanned for sibling mod folders (a directory containing
  `<dirName>/<dirName>.mod`), skipping `base` (the old DML loader runtime,
  never a mod) and the txt itself. An unresolved line whose name matches a
  sibling upgrades to the "will be imported" outcome (resolved lines are
  never upgraded: the profile/library match wins); IO failures degrade to
  the plain unresolved rows. Migration users are identified by exact
  base-name match, with search as their fallback.
- **Apply** (single button, enabled when at least one line is included; an
  empty/comment-only file shows the localized notice and refuses rather
  than a no-op write), sequenced membership-before-order (the order write
  cannot place ids that are not profile members yet):
  1. **Imports**: each INCLUDED sibling-import line imports its folder
     through `IModImportService.Import` (source = `NexusSource` of the
     identified id when the row is identified, else `UntrackedSource`;
     version = the row's typed version when identified + non-empty, else
     empty, the version-unknown path). The identified sibling import IS
     the association; there is no separate identity rewrite at apply, and
     lines matching an existing untracked container stay plain adds. A
     per-line import failure is recorded on the line and the rest
     continue.
  2. **Adds**: `AddMod` (Latest policy) for every included add line
     (library + the imported containers).
  3. **Order**: ONE `SetModOrder` carrying every matched + newly-created
     container in file order, so every add lands at its file position.
     The checkboxes gate only adds; order application is not optional, and
     `SetModOrder`'s own lock projection keeps locked entries at their
     exact slots.
  4. **Enqueues**: for each INCLUDED identified not-in-Curator line, a
     Premium account (verified fresh through
     `INexusAuthService.GetCurrentStateAsync` at apply time) gets a
     download enqueued onto the shared queue (the head file resolved
     first so the queue's dedupe key is real; a `ProfileAdd` item with no
     container; the download rows own progress + completion + the
     reload). The typed version is informational on these lines: the
     download resolves the real version. Non-premium accounts perform no
     network action (the rows carry the open-on-Nexus link).
  On full success the session is marked pending, the card deactivates,
  and `OrderApplied` reloads the mod list. A card-level failure (a
  profile/repo/import failure, or stop-on-429 in the enqueue batch, which
  keeps prior work and says the run can be re-applied) or any per-line
  failure keeps the review open so the messages stay readable and a
  re-run can finish; re-runs are idempotent (imports dedupe, `AddMod`
  no-ops on existing membership, the queue dedupes live downloads).
- **The resolver tier** (the identification workspace): after the repo tier
  resolves what it can, a serial human-paced search queue (one row at a time,
  table order, no retries; a failed search is logged and leaves the row
  unresolved with the manual path available) fires
  `INexusClient.SearchModsAsync` with the folder name normalized into search
  terms (lowercase, underscores/hyphens to spaces, whitespace collapsed).
  Each unresolved row gets a workspace: the TOP candidate inline (name +
  mod id + a one-click Accept), an expand affordance revealing the
  alternates (each with its own Accept), and the manual id/URL entry in the
  reserved cells (parsed via the shared `ImportSourceValidator` rules; a
  bare id or a `nexusmods.com` URL both accepted). Accepted or manually
  entered identification marks the row identified: the id cell shows the
  fact, the version cell activates (empty by default, validated
  non-empty-when-Nexus like the import form), and the apply path decides
  what the version means per destination. Identification never checks the
  include checkbox (the identified default stays excluded; identification
  is a correction, not consent). Cancelling the card stops the queue;
  arrived candidates stay on their rows.
- **Cancel**: no writes, the card deactivates.

## Mod list density / detailed rows

The Compact/Detailed row-density choice for the Mods destination. Detailed is
the default: the multi-line row with the Nexus summary + a cached thumbnail,
preserving every existing row action; Compact is the dense one-line row,
surviving only when persisted or selected. Three UI-layer
components cooperate: the
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
    public bool IsCompact { get; }                        // RowDensity == Compact (the one-line row)
    public bool IsDetailed { get; }                       // RowDensity == Detailed (the default)

    // CommunityToolkit-generated from SetDensity(ModRowDensity):
    public IRelayCommand<ModRowDensity> SetDensityCommand { get; }

    public Task SetRowsAsync(IReadOnlyList<ModItemViewModel> rows);
}
```

- `RowDensity`: the persisted density, read + normalized (only `Compact`
  survives; every other numeric value, including undefined, becomes `Detailed`)
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

### `ModListViewModel` filter / search projection

The mod list renders a projection of the authoritative `Mods` list under three
session-transient controls. The new public surface:

| Member | Meaning |
| --- | --- |
| `ObservableCollection<ModItemViewModel> VisibleMods` | The rendered projection: `Mods` minus rows hidden by the filter/search. Rebuilt by one private `RebuildVisibleMods` at the end of every `Reload` (after the known-update-flag hydration, so the updates-only filter sees hydrated flags), on every filter/search state change, and after an enable toggle under an active filter. `Mods` stays authoritative (update hydration, the row-context fan-out, and the density coordinator's snapshot all still read the full list, so a filter change never re-triggers thumbnail/metadata hydration). |
| `bool HasVisibleMods` | Whether the projection holds at least one row. Drives the row-list ScrollViewer visibility (it collapses when a filter empties the visible set). |
| `bool HideDisabledMods` | The hide-disabled visibility toggle (session-transient; never persisted, survives reloads + navigation, cleared on an active-profile change). Changing it rebuilds the projection. |
| `bool ShowUpdatesOnly` | The updates-only filter toggle (same transient lifecycle as the hide flag). The filter keeps only rows whose `UpdateAvailable` is true; changing it rebuilds the projection. A landed check also reprojects, since it can change the flags. |
| `string SearchText` | The search box text (keystroke-live TwoWay; case-insensitive ordinal substring on the row name; empty or whitespace matches everything; same transient lifecycle as the flag). |
| `bool HasSearchText` | Whether any text is typed (drives the inner clear affordance). Distinct from `IsFilterOrSearchActive`: whitespace-only text shows the clear button but filters nothing. |
| `bool IsFilterOrSearchActive` | Whether the hide filter, the updates-only filter, or a non-whitespace search is active. Suppresses the add-hints empty state. |
| `string HideDisabledTooltip` | Localized hide/show tooltip + automation name for the toggle (describes the action the click performs). Re-fires on a culture change. |
| `string UpdatesOnlyTooltip` | Localized filter/show-all tooltip + automation name for the updates-only toggle (describes the action the click performs). Re-fires on a culture change. |
| `string NoMatchesText` | Localized no-matches message. Re-fires on a culture change. |
| `bool ShowNoMatchesMessage` | Derived: an active profile with a non-empty full list whose projection is empty while a filter/search is active. Exclusive with `ShowAddModsHint` (which now also gates on `!IsFilterOrSearchActive`). |
| `ToggleHideDisabledCommand` | Flips `HideDisabledMods`. |
| `ToggleUpdatesOnlyCommand` | Flips `ShowUpdatesOnly`. |

Move availability (`CanMoveUp` / `CanMoveDown`) is computed over the VISIBLE
unlocked rows: Move Up / Move Down cross to the adjacent visible unlocked row,
and a row with only hidden or locked rows above it cannot move up. Reorder
commits map visible ranks onto the stored order through the visibility-aware
`ModReorderPlanner` (below).

### `ModListViewModel` manager-banner state

The list surfaces the alternate-mod-manager derivation as two members:

| Member | Meaning |
| --- | --- |
| `bool IsModManagerActive` | Whether the active profile has an enabled alternate mod manager mod. Read from the same `IProfileService.GetActiveModManager` derivation the launch path hands to Relay as its `--mod-manager` flag (one derivation, so the banner and the flag can never disagree); re-derived on every `Reload` and on the enable-toggle path (in place: the toggle deliberately skips the row rebuild). Drives the banner's visibility. |
| `string ModManagerBannerText` | The localized caution text, formatted with the manager mod's display name: the loaded row's name, falling back to the repository container's name, then the literal `base`. Re-fires on a culture change. |

The banner is live state, not a notification: it is visible exactly while the
manager is active, is not dismissible (disable or remove the manager mod to
clear it), and gates nothing (reorder/lock controls stay fully functional).

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
| `bool CanMoveUp` / `bool CanMoveDown` | Whether the row can move to the previous/next VISIBLE unlocked rank (an unlocked visible row with an unlocked visible row above/below; a row with only hidden or locked rows above cannot move up). Computed by the parent over the visible projection on reload and on every filter/search change; `false` for a locked or hidden row. |
| `bool IsGripEnabled` | Derived: `!OrderLocked`. Bound to the grip's `IsHitTestVisible` so a locked grip stops intercepting pointer input and falls through to touch scrolling. |
| `string OrderLockTooltip` | Localized tooltip + click-action text for the order-lock button (lock vs. unlock). |
| `string OrderLockAutomationName` | Localized automation name describing the row's current locked/unlocked state. |
| `bool ShowReorderMarkerBefore` / `bool ShowReorderMarkerAfter` | Set by the view on at most one row while dragging: the accent insertion line anchors before/after that row. |

`Refresh()` also re-fires `OrderLockTooltip` + `OrderLockAutomationName` on a
culture change. The lock toggle (`ToggleOrderLock`) and the reorder commit
(`CommitReorder`) live on `ModListViewModel`; `ModReorderPlanner` is the pure
visibility-aware order-construction helper: it receives each row's container
id, locked flag, and visibility under the current filter/search, moves the
source by remove + insert within the non-locked stream anchored to
visible-unlocked rows (locked rows keep their exact slots, hidden rows shift
at most one slot and keep their relative order, an all-visible input
reproduces the pure lock projection), and rejects same-order / out-of-range /
locked-source / hidden-source / missing-source requests without a service
call. `ReorderRequest.TargetUnlockedRank` is the insertion rank among the
visible unlocked OTHER rows. `ReorderGestureMath` is the pure
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
- **Toolbar search box + hide-disabled toggle.** A fixed-width (200 DIP)
  TextBox between the flexible column and the density pair: a keystroke-live
  TwoWay binding to `SearchText` (no `UpdateSourceTrigger`; Avalonia TextBox
  bindings update per keystroke by default) with a localized `PlaceholderText`
  watermark. The inner clear affordance is a `TextBox.InnerRightContent`
  Button reusing the Fluent theme's own clear-button chrome
  (`Theme=FluentTextBoxButton` + the theme's drawn X geometry via `PathIcon`,
  invoking the TextBox's `Clear` method, `Focusable=False` so the box keeps
  focus after the click), visible only while `HasSearchText` is true. The
  hide-disabled toggle is a third `icon density` button inside the density
  group: `selected` bound to `HideDisabledMods`, drawn
  `visibility_off`/`visibility` paths swapped by the flag, bound to
  `ToggleHideDisabledCommand`, with the dynamic hide/show tooltip +
  automation name. The updates-only toggle is a fourth `icon density` button
  in the same group: one stable drawn Material `update` glyph (updates-only
  has no natural crossed-out variant to swap), `selected` bound to
  `ShowUpdatesOnly`, bound to `ToggleUpdatesOnlyCommand`, with the dynamic
  filter/show-all tooltip + automation name. While any hosted card is active
  (the import workflow in either mode, or the load-order review), the
  projection-touching toolbar controls (the search box,
  the density + filter cluster, and the check-now refresh cluster) disable
  through `ModListViewModel.IsListToolingEnabled` (reading the shared
  `ModCardsGate`, re-fired through its single `Changed` subscription), so no
  filter or search change can hide the
  row being edited under its open editor; row-level controls stay live.
- **Edit-card name field.** In the edit mode the name TextBox locks as
  read-only (`IsReadOnly` bound to `!IsNameEditable`), never disabled: the
  Fluent dark theme renders disabled text near-invisibly, which read as an
  empty field, while read-only text renders at full contrast and stays
  selectable so the name being edited is always legible. It is the only
  locked field on the card (the version/URL fields are never locked for a
  container that can open it; a downloaded container never opens the card).
- **Manager banner.** A full-width caution `Border` in the page grid's row 2,
  between the inline import card (row 1) and the row list (row 3):
  `IsVisible` bound to `IsModManagerActive`, a `CuratorCautionBackgroundBrush`
  face carrying a drawn Material `swap_vert` icon (caution foreground) + the
  wrapping `ModManagerBannerText`. Non-dismissible live state (no close
  affordance) that gates nothing; it clears when the manager mod is disabled
  or removed.
- **Row template.** A `Panel` hosts two mutually exclusive roots selected by the
  row's `IsDetailed` projection: the Compact `Grid` (`compactRow`, four
  columns: grip, name, badges, action strip) and a Detailed `Border` (the
  `detailedRow` style: rounded, low-emphasis). The grip, the badge cluster,
  and the action strip are ONE shared definition each (`ModRowGripTemplate`,
  `ModRowBadgesTemplate`, `ModRowActionStripTemplate` DataTemplate resources
  hosted by both roots through `ContentControl.ContentTemplate`), so no row
  action can fork between modes; the page styles + the container query reach
  the realized template instances (styles select through the logical tree),
  and the event handlers resolve against the page code-behind unchanged.
  The Detailed card is one adaptive
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
  (`Grid.ColumnSpan=3`, driven by the `ContentControl.detailedActions` styles).
  Width, height, row span, and action column/span that
  change at the breakpoint are style-driven (default wide styles + the
  container-query overrides), not local values; constant row/column positions
  stay local. The shared strip's spacing is style-driven per density
  (`WrapPanel.actionStrip` base + the `Grid.compactRow`-scoped margins that
  reproduce the Compact single-line layout), and the Enabled checkbox's label
  is the row's density-aware `EnabledLabel` (null in Compact).
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

## The mod-list row context + the linked-mods child

### `ModRowContext`

```csharp
public partial class ModRowContext : ObservableObject
{
    public ModRowContext(
        INexusAuthService auth,          // the one-shot premium read
        IGamingModeState gamingMode,
        ILogger<ModRowContext> logger);

    public bool IsGamingMode { get; }               // constant
    public bool IsPremiumUser { get; set; }         // the read lands here
}
```

The one shared observable context for the row-affecting global mod-update
state (premium / gaming). Created once in composition before
`ModListViewModel`, which passes the same instance to every
`ModItemViewModel` at construction: rows read their global halves off it
(their public property names stay as context-forwarding reads, so bindings are
unchanged), and the list VM's single context subscription fans change
notifications into the live rows (no per-row subscription against the
application-lifetime context, so rows dropped by a reload cannot leak).
Install-busy state is not here: an update in flight is a queue item, and the
row renders it as the download morph (see [Download rows](#download-rows));
there is no separate global busy flag to mirror.

### Download rows

Downloads render as rows in the mod list, never popups or flyouts. One item
template (the shared download status template in `ModListView.axaml`) is
hosted in two places:

- **In-place morph**: a download whose container is referenced by the active
  profile's current row set AND realized in `VisibleMods` morphs that row
  (`ModItemViewModel.ActiveDownload`, assigned exclusively by the parent's
  hosting projection). While morphed, the summary/metadata area and the
  action strip swap to the download content, the policy editor and the
  update-action cell suppress (the morphing download is about to write the
  policy itself; hiding the button is the double-click guard), and the
  structural controls (grip, lock, move, remove, enabled) stay functional:
  position and membership are profile metadata staged at launch.
- **Appended row**: every other item (fresh mods, cross-profile targets,
  filtered-hidden targets, null container ids) renders below the profile rows
  in a dedicated `ItemsControl` inside the same scroll region, in admission
  order, always showing its target-profile label.

The hosting projection is re-derived from scratch (no stored placement
state) on every coordinator collection/state change, every reload, every
filter/search change, and every profile switch: the first item per container
id that finds a visible row wins the morph, later items with the same
container append (how a failed corpse and a fresh attempt for the same mod
coexist as two rows). The projection is structurally separate from the mod
rows: `DownloadRows` is an `ObservableCollection<DownloadRowViewModel>` that
never intersects `VisibleMods` or `Mods`, so download rows can never enter
the reorder planner's inputs, the drag gesture's container math, or the move
commands.

`DownloadRowViewModel` is the row-facing projection of one `DownloadItem`:
it owns no download state (every phase, byte, name, and pulse change arrives
through the item's own UI-thread property notifications) and re-fires the
derived bindables the shared template consumes: the phase flags, the
determinate/indeterminate progress choice (percent + MB/MB with a known
total), the localized status word, the always-shown target-profile label,
the failure text, the automation string, and the join-pulse flash. Its
Cancel, Dismiss, and Retry commands forward straight to the queue with the
wrapped item. An active download also suppresses the no-mods/add-hints empty
state (an active download above "no mods yet" reads as a contradiction;
terminal failed corpses do not suppress).

### `LinkedModsViewModel`

```csharp
public partial class LinkedModsViewModel : ObservableObject
{
    public LinkedModsViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        IDialogService dialogs,
        LocalizationService localization,
        IExternalLauncher externalLauncher,
        IGamingModeState gamingMode,
        ILogger<LinkedModsViewModel> logger);

    public event EventHandler? ModsLinked;

    public IAsyncRelayCommand<IReadOnlyList<string>?> LinkModsCommand { get; }
    public IAsyncRelayCommand<ModItemViewModel?> OpenFolderCommand { get; }
}
```

The link-external-folder child of `ModListViewModel` (the
`ImportWorkflowViewModel` pattern: an application-lifetime singleton
registered before the parent + exposed read-only for view binding; the parent
keeps no `IModImportService` dependency). `LinkModsCommand` owns the picker
flow (peek the base name, hard-block the base-name collision excluding a
re-link, `LinkFolder`, `AddMod` with Latest policy; a failed peek, a
containment failure, or a collision aborts the remaining batch) and
`OpenFolderCommand` opens a linked row's external folder in the OS file
manager (gated off in Gaming Mode, with a launcher-failure fallback alert).
`ModsLinked` fires exactly where the flow finishes; the parent reloads the
active list on it. The three launcher-failure alerts (files page, games page,
external folder) share one internal `LaunchAlerts` helper (title key,
message key, args).

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
// The download queue + its enqueue front, registered right after AddNxm()
// and before the INxmModDownloadHandler override (all three below).
services.AddSingleton<IModDownloadQueue>(sp => new ModDownloadQueue(/* acquisition, repo, profiles, session, update state, config, Func<IModListRefresh>, loc, Action<Action>, logger */));
services.AddSingleton(sp => new ModUpdateEnqueuer(/* acquisition, queue, profiles */));  // premium update installs
// … the INxmModDownloadHandler override (last-wins over AddNxm's no-op) …
// Singletons: one shell, one list, one dialog service, one session.
services.AddSingleton<IProfileSession>(sp => new ProfileSession(
    sp.GetRequiredService<ISteamService>(),
    sp.GetRequiredService<IProfileService>(),
    sp.GetRequiredService<IProfileActivationState>(),
    StartRunningStatePolling));                 // DispatcherTimer, 3s
services.AddSingleton<LocalizationService>();
services.AddSingleton<IPreferencesService, PreferencesService>();
services.AddSingleton<MainWindow>();
services.AddSingleton<Action<Action>>(_ => action => Dispatcher.UIThread.Post(action));
services.AddSingleton<INxmRegistrationState>(sp => new NxmRegistrationState(  // shared last-known nxm state (before its consumers)
    sp.GetService<INxmHandlerRegistrar>(),
    sp.GetRequiredService<Action<Action>>(),
    sp.GetRequiredService<ILogger<NxmRegistrationState>>()));
services.AddSingleton<IModListRefresh>(sp => sp.GetRequiredService<ModListViewModel>()); // the queue's + children's reload seam (resolved lazily)
services.AddSingleton<IAutomaticUpdateService, AutomaticUpdateService>(); // Premium automatic batch (enqueues via ModUpdateEnqueuer)
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
services.AddSingleton(sp => new LinkedModsViewModel(/* … */)); // link-external child (before ModListViewModel)
services.AddSingleton(sp => new ModRowContext(/* auth, gamingMode, logger */)); // row globals (before ModListViewModel)
services.AddSingleton<ModListViewModel>();                   // injects the three children + the row context + the download queue
services.AddSingleton<ProfilesViewModel>();
services.AddSingleton<IntegrationsViewModel>();
services.AddSingleton<PreferencesViewModel>();
services.AddSingleton<SettingsViewModel>();
services.AddSingleton<IShellModalQueue, ShellModalQueue>();  // the shell's modal queue (before its enqueuers)
services.AddSingleton(sp => new DmfPromptService(/* … incl. IShellModalQueue + IModListRefresh */));
services.AddSingleton(sp => new ShellViewModel(/* … all five page VMs, IAppUpdateService, IShellModalQueue, Action<Action> */,
                                              sp.GetRequiredService<INxmRegistrationState>()));
services.AddSingleton<IDiscoveryEscapeHatchFactory>(sp => new DiscoveryEscapeHatchFactory(/* config, steam, loc, gaming */));
services.AddSingleton<IDialogService>(sp => new DialogService(/* owner, localization, factory */));
services.AddSingleton(sp => new UpdateCheckRunner(/* … incl. IAutomaticUpdateService, StartUpdateCheckPolling */));
#if CURATOR_VELOPACK
services.AddSingleton<IAppUpdateService>(sp => new VelopackAppUpdateService(
    sp.GetRequiredService<IConfigLoader>(),
    sp.GetRequiredService<ILogger<VelopackAppUpdateService>>()));
#else
services.AddSingleton<IAppUpdateService, NoopAppUpdateService>();
#endif
services.AddSingleton(sp => new AppUpdateCheckRunner(/* IAppUpdateService, IConfigLoader, logger */));
services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellViewModel>()); // plain forward
services.AddSingleton(sp => new OnboardingService(
    sp.GetRequiredService<IOnboardingState>(),
    sp.GetRequiredService<IDialogService>(),
    sp.GetRequiredService<IShellNavigation>(),
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
  for the application lifetime) and injected into `ShellViewModel`. Nothing
  depends on `DmfPromptService`: the coordinator enqueues onto
  `IShellModalQueue` (registered before it), and the composition root
  resolves it once after the provider is built (best-effort) so its
  `IProfileService.ProfileCreated` subscription exists before any profile
  can be created. The shell drains the queue on destination entry;
  `ProfilesViewModel` is narrowly coupled to profile workflow and does no DMF
  or mod-list work after Save.
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
  this supersedes the no-op default registered inside `AddNxm()`. The handler
  acknowledges a successful acquisition through `IUpdateStateStore` (the
  Integrations seam) and reloads the list through `IModListRefresh`, a
  one-member interface the composition root forwards to the `ModListViewModel`
  singleton (a plain interface forward, resolved lazily). See
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
  (`IConfigLoader`, `IExternalLauncher`, `NexusGameIdentity`, the app-state
  role interfaces, `LoggingBootstrap`), `profiles`
  (`IProfileService`, `ProfileSummary`, `ModListEntry`),
  `mods` (`IModRepository`, `IModImportService`, `ModContainer`,
  `ModDisplayMetadata`, `ModVersion`, `ModVersionPolicy`, `ModSource`,
  `NexusSource`, `UntrackedSource`, `LinkedSource`),
  `integrations` (`INexusAuthService`, `IModAcquisitionService`,
  `INexusModMetadataService`, `IUpdateCheckService`, `UpdateCheckResult`,
  `ModUpdateInfo`, `ModListCandidate`, `IUpdateStateStore`), `steam` (`ISteamService`), `relay-client`
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
  controllable handoff timeout + a controllable relay-exit task, no live
  dispatcher and no real 30-second
  wait): the attempt state disables Launch before the launch service runs, a
  false eager refresh + false polling notification never re-enable it while
  waiting, a later `IsRunning = true` completes the handoff (attempt cleared,
  Launch still disabled by the running gate), a held relay exit keeps the
  attempt set even after Darktide is observed (and still holds an
  already-running session at handoff entry) until the exit lands, the timeout
  clears the attempt and re-enables retry when the combined wait stays
  unresolved, failure results keep the
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
  remove (with confirm), the inline import workflow
  integration (child VM exposure, `ItemImported` reload for the active profile,
  no-misdirect for an inactive profile, add-mode stability, end-to-end
  create/activate/import), the linked-folder flow
  (end-to-end through the `LinkedMods` child: peek, collision-refusal,
  re-link refresh, `LatestPolicy` add, the parent's reload-on-`ModsLinked`;
  `OpenFolder`: launches the file manager at the normalized external path,
  failure alert, no-op for non-linked/broken rows; the linked badge two-state
  available/broken, disabled policy edit, empty update-action cell,
  `IsExternalBroken` on Reload), `CheckCompleted` per-row state,
  `UpdateCommand` (the Premium resolve + enqueue branch incl. the
  resolve-failure alert and the unresolved-version no-op, and the
  regular/unknown files-page branch with premium gating),
  `CheckForUpdatesNow`, the gate-fed `IsRateLimited` + the coupled
  `IsRateLimitActive` refresh-button/pill rendering (server reset + fallback
  cooldown, precedence over the manual throttle), the `NamesChanged` in-place row
  name refresh (refreshed when the flag is set, untouched when it is not), and
  the empty-state Nexus hint (construction + both `Reload` paths perform zero
  registration probes; `IsNxmRegistered` follows the shared state).
- **`ModListModManagerBannerTests`**: the manager-banner state through the VM
  (the banner follows the `GetActiveModManager` result at every `Reload` and on
  the enable-toggle path without an intervening reload, both directions, an
  unrelated toggle leaving state + text unchanged, and the row / repository /
  `base` name fallback chain).
- **`ModRowContextTests`**: the shared row-context contract -- a premium flip
  on the context re-fires exactly the row + list-VM properties the former
  per-flag pushes re-fired, the gaming constant reads
  through rows + the list VM, rows dropped by a reload receive no context
  notifications (no per-row subscription to leak), and a failed premium read
  leaves the flag false.
- **`ModRowSharedTemplatesTests`**: the single-definition contract for the
  shared row markup -- every shared row control exists exactly once in
  `ModListView.axaml`, both row roots host the shared templates, the Compact
  row keeps its single-line spacing through the scoped styles, and the
  680-DIP container query still moves the strip + thumbnail.
- **`ShellModalQueueTests`**: the queue contract -- run-once after the drain,
  newest-wins per owner, independent owners in enqueue order, other
  destinations' entries stay queued, and a drained entry that throws is
  consumed (never re-fired).
- **`WindowGeometryTrackerTests`**: the pure geometry policies (size
  normalization + clamping, the meaningful-state policy, reason-aware trust,
  the #19431 correction decision, the persisted seeding) plus the state
  machine fed headless through the injectable post seam (deferred/coalesced
  applies with latest-candidate resolution, Layout never authoritative, the
  end-to-end #19431 burst resolving to the trusted size, correction
  non-recursion, the pre-open Layout exclusion, post-close ignoring, the
  close-path candidate consumption). `MainWindowStateTests` keeps the
  constants + the screen conversion seam that stay on the window.
- **`LocalizedViewModelRegistrationTests`**: the source scan -- every
  property getter indexing `_localization[...]` must appear in its class's
  registered refresh list, and every class with localized getters must be in
  the known VM set, so a forget-to-register localized property is a red test
  rather than stale text on a culture switch.
- **`UpdateRefreshGateTests`**: the gate directly -- server-reset governance,
  the 1-minute fallback cooldown, immediate clearing on a non-rate-limited
  result, the null-result no-op, the shared countdown-timer lifecycle, the
  marshaled `StateChanged` event, and the manual-throttle coupling.
- **`ModListOrderLockTests`**: the profile-scoped load-order lock + drag-reorder
  surface through the VM, against the lock-aware `FakeProfileService` projection:
  `OrderLocked` + move/grip availability on reload, `ToggleOrderLock` persists
  without `HasPendingChanges`, locked-row move/drag no-ops, Move Up / Down skip
  locked rows (locked-first-stays-first, crossing locks), drag `CommitReorder` to
  first / middle / last unlocked rank with multiple locks (one `SetModOrder`
  call + exact final order), same-rank / invalid-rank / locked-source /
  missing-source / no-active-profile rejection, and the no-lock move regression.
  These run with no filter active, so they double as the all-visible degenerate
  parity for the visibility-aware planner.
- **`ModListFilterTests`**: the filter/search projection through the VM:
  hide-filter / search / combined narrowing (case-insensitive ordinal
  substring, whitespace-only matches everything but still counts as typed
  text), clearing restores, projection mirrors the full list with no filter,
  survives a reload, clears on a profile switch, `ToggleEnabled` under the
  hide-filter, the no-matches vs add-hint exclusivity matrix, the
  updates-only filter (flagged-rows projection incl. the reload
  hydration-ordering regression, AND-composition with the other filters, a
  landed check reprojecting the live filter, session-transient lifecycle,
  empty-state exclusivity, tooltip locality), move availability
  over visible unlocked neighbors, and reorder-through-filter (Move Up / Down +
  `CommitReorder` with hidden rows keeping relative order + one `SetModOrder`
  call + locked rows keeping indices, no-op / hidden-source / out-of-range
  rejection, the top visible unlocked row cannot move up).
- **`ModReorderPlannerTests`**: the pure visibility-aware planner: all-visible
  parity with the lock-aware projection, move up / down across hidden rows
  landing the source adjacent in the stored order, drop-at-end with trailing
  hidden rows, hidden rows never anchoring, locked rows keeping exact indices
  while hidden rows shift, single-visible-row no-op, hidden / locked / missing
  source rejection, and visible-unlocked-only rank ranges.
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
  opens), the prompt-timing-after-create (the coordinator enqueues on
  `ProfileCreated`; the prompt itself fires only when the shell's modal queue
  drains for Mods), the drained entry's post-prompt reload (including when
  the prompt body skips), the premium
  enqueued download (head resolved at enqueue, admitted as a ProfileAdd item;
  a resolve failure surfaces the alert and enqueues nothing), the
  non-premium / unknown / no-auth browser-open path (opens
  regardless of the registration state; the download-confirm wording follows
  the shared state with zero probes), and the browser-launch failure
  fallback alert.
- **`OnboardingServiceTests`**: the first-run Welcome coordinator (already
  complete no-op, Continue persists + skips Integrations, Set up Nexus
  persists before navigating to Integrations once, the close == Continue
  equivalence, the in-process one-shot guard, and navigation-failure
  isolation).
- **`NxmModDownloadHandlerTests`**: the Darktide-only gate (rejects other
  games before auth / profile / enqueue), the auth + active-profile
  gates, the peek + enqueue flow (the repository peek names the row, the
  request carries the container id + profile + nxm tokens, nothing is
  acquired and no profile is written), the enqueue-failure error wiring
  (alert), the prompt-return contract, and the UI-thread marshaling seam.
- **`ModDownloadQueueTests`**: the coordinator end to end against fakes:
  cross-thread enqueue publishing on the seam thread, the dedupe join + pulse
  (same key case-insensitive on the domain; a different file of the same mod
  queues separately), FIFO serial processing with no overlap, the hit path
  (exact file-id match completes with no network; head version registers
  Latest, non-head pins to that version's folder; container/version/name
  resolution), the miss path (byte progress, the Importing transition, the
  name swap, head/non-head policy), the completions (an existing container's
  policy rewritten via SetModPolicy, reload only when the target is still
  active, target-profile-deleted and mod-removed inline failures, the
  acknowledge-failure log), cancel semantics (queued drop, active
  token-authoritative incl. an IOException surfacing as canceled, terminal
  no-op), the dequeue-time sign-out inline failure, retry/dismiss, the
  UpdateInstall purpose (acquire + acknowledge once + the applied event,
  ineligible/removed/re-pinned silent no-ops, acquisition failure without
  acknowledging, cancel propagation, a background-profile completion still
  acknowledging + raising), mixed nxm + update clicks sharing the serial
  worker, the admission-event ordering on a fast hit, and the
  UpdateInstall request validation.
- **`ModListDownloadRowsTests`**: the row hosting through the list VM: an
  item targeting a visible row morphs it in place, a filtered-out target
  renders appended, a profile switch rehosts both directions, a null
  container id always appends, a resolve landing a container id moves an
  appended item in place, a reload preserves the morph on the rebuilt row, a
  failed corpse + a fresh attempt render side by side, removing the item
  clears the morph + the appended row, an active download suppresses the
  add-hints empty state (a failed corpse does not), the no-matches message
  coexists with appended rows, a morphed row suppresses the policy + update
  affordances but keeps the structural controls (incl. lock semantics), the
  Cancel/Dismiss/Retry forwarding, every phase/byte projection render state,
  the always-shown target-profile label, the join pulse re-fire + decay, the
  resolved-name forward, and reorders committing through the mod rows with
  downloads present.
- **`DownloadRowXamlTests`**: the download-row surfaces as repository source
  tests over `ModListView.axaml` / `Strings.resx` (the XAML parsed as XML):
  the shared download status template exists once and is hosted by all three
  surfaces (the Compact morph slot, the Detailed morph slot, the appended
  row), the appended section is a separate `ItemsControl` over `DownloadRows`
  with no reorder affordances, the morph suppresses the update affordances
  and disables policy, the morphed strip binds the wrapper's Cancel, retry +
  dismiss exist once in the shared template, the join-pulse flash is bound on
  every host with a fading animation, every download resx key exists, and
  every download icon is drawn geometry (no glyph text).
- **`AutomaticUpdateServiceTests`**: the enqueue batch -- one UpdateInstall
  item per flagged candidate, resolve failures isolated into one aggregated
  alert, a download failure row-hosted and never alerted, scheduling stops
  after a prior profile switch, a mid-batch switch cancels the queued but
  not the active items, the deleted-profile stop, and resolve-cancellation
  propagation.
- **`EscapeClosesBehaviorTests`**: the pure `ShouldClose` helper behind the
  ESC-closes-dialogs behavior (true for `Key.Escape`, false for other keys).
  The KeyDown-to-Close wiring is rendered UI and not covered by a
  rendered-control test.
- **`DesktopIdentityOptionsTests`**: the explicit X11 `WM_CLASS` constant
  matches the Velopack pack id (`ModifAmorphic.ModificusCurator`) and the
  factory builds an `X11PlatformOptions` carrying it, without starting Avalonia
  or initializing X11 (no `DISPLAY` required).
- **`DetailedModRowsViewModelTests`**: config/density normalization (default
  Detailed, old config without `ModRowDensity` loads Detailed, persisted
  `compact`/`detailed` strings load as themselves at the coordinator, Detailed
  round-trips through JSON, undefined normalizes to Detailed), the
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
- [profiles](profiles.md): `IProfileService` and the
  profile / mod-list model the UI drives.
- [mods](mods.md): `IModRepository`, `IModImportService`, `ModDisplayMetadata`,
  and the source / version-policy model the UI reads.
- [config](config.md): `CuratorConfig`, `PreferencesConfig`, `ThemeMode`,
  `ModRowDensity`, `NexusConfig`.
