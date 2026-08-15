# UI layer: architecture

The UI layer is the Avalonia 12 front end of Modificus Curator. It is the only
part of the codebase that talks to the user: the shell window (a SplitView with
five hosted destinations), profile management, the mod list, every modal
(Welcome, confirm, import, discovery escape-hatch, alert, progress), global
preferences (theme, font scale, language), and the dynamic-language
infrastructure. The UI keeps domain data I/O (profile, repository, Nexus,
Steam, launch) behind backend library services; it only presents state and
orchestrates calls. The one deliberate exception is a focused UI-owned
presentation-media service, `IModThumbnailService`, which returns an Avalonia
`IImage` (a presentation type the backend libraries do not depend on) and owns
only an HTTP/disk cache for mod thumbnail images. Everything else the UI
presents flows through a backend library interface.

This doc covers how the UI is structured, how the active profile is owned, how
the shell wires its commands, how the mod list and the update UI behave, how
the app self-update surfaces, how the DMF install prompt fires, and how
dialogs, preferences, and i18n fit together.

> Public surface, exact signatures, and DI registration are documented in the
> [UI reference](../reference/ui.md). This doc covers the
> architecture and the why.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│ MainWindow (shell: a SplitView, CompactInline)                           │
│                                                                          │
│  ┌─ Nav rail (48px compact icon tile; expands to 48px icon + label) ───┐ │
│  │ hamburger toggle                                                    │ │
│  │ [Profiles] [Mods] [Nexus] [Preferences] [Settings]                   │ │
│  │ ... (star row pushes the next item to the bottom) ...                │ │
│  │ [Exit]                                                               │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Global header ─────────────────────────────────────────────────────┐ │
│  │ Current destination title ·························· Launch Darktide│ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Content area (one persistent UserControl per destination,          ┐ │
│  │  visibility-switched by Is*Visible projections)                      │ │
│  │ ProfilesView | ModListView (drag-and-drop) | IntegrationsView |      │ │
│  │   ModListView header: rate-limit notice · refresh ·                  │ │
│  │     auto-sort · Compact/Detailed density selector ·                  │ │
│  │     Add split button (Nexus Mods + 3 pickers)                        │ │
│  │   rows:   Compact Grid OR Detailed card (thumbnail + name +          │ │
│  │     badge + summary · wrapping action strip: enabled · policy ·       │ │
│  │     update-action cell (button) · up · down · remove                 │ │
│  │ PreferencesView | SettingsView                                       │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Status strip ──────────────────────────────────────────────────────┐ │
│  │ Drawn Ellipse (running / stopped) · GameRunningText · NxmHandlerStatus│ │
│  │ · AppUpdateNotice pill (dismissible; shown when a self-update exists) │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

The shell holds no data state of its own. Every long-lived concern is owned by
a UI-layer singleton that the shell (and other view models) inject:

```
 IProfileSession ───────────── single authority for the active profile id,
  │                             the can-change gate, and the live running-state
  │                             (a DispatcherTimer polls ISteamService every 3s)
  │
  ├── ShellViewModel ────────── navigation (ShellDestination, the guarded
  │   │                         NavigateAsync lifecycle) + Launch + the status
  │   │                         strip; mirrors session state
  │   │
  │   ├── ProfilesViewModel ─── the active profile editor (name + description
  │   │   │                     + inline launch settings) + banner/picker
  │   │   │
  │   │   └── LaunchSettingsEditorViewModel  the reusable inline launch-settings
  │   │                                       rows (env vars + args + toggles)
  │   │
  │   ├── ModListViewModel ──── the active profile's mod list; enable/disable,
  │   │   │                     reorder, per-mod policy, remove, import, update
  │   │   │
  │   │   ├── ImportWorkflowViewModel  the inline import-workflow child VM
  │   │   │
  │   │   ├── DetailedModRowsViewModel the Compact/Detailed density coordinator
  │   │   │                             (persisted density, metadata backfill,
  │   │   │                             thumbnail hydration)
  │   │   │
  │   │   └── ModItemViewModel  one row; carries state only (no service calls)
  │   │
  │   ├── IntegrationsViewModel  the Nexus destination (RefreshAsync
  │   │                            on enter, Deactivate on leave)
  │   ├── PreferencesViewModel   the Preferences destination
  │   └── SettingsViewModel ──── the Settings destination (RefreshFromConfig
  │                              on enter)
  │
  ├── UpdateCheckRunner ─────── fires IUpdateCheckService on profile load,
  │                             active-profile switch, a periodic timer, and
  │                             the manual "check now" affordance
  │
  ├── IAppUpdateService ─────── Curator's own self-update (Velopack-packaged
  │                             Windows installer and Linux AppImage);
  │   │                         the shell notice + Settings section read it.
  │   │                         Conditional impl behind CURATOR_VELOPACK
  │   │                         (NoopAppUpdateService everywhere else).
  │   │
  │   └── AppUpdateCheckRunner ── fires one availability check on startup,
  │                              fire-and-forget; no periodic timer (unlike
  │                              UpdateCheckRunner)
  │
  ├── OnboardingService ─────── shows the first-run Welcome modal once, then
  │                             navigates the shell to Nexus on a
  │                             "Set up Nexus" choice
  │
  ├── DmfPromptService ──────── records the new-profile trigger from
  │                             IProfileService; the shell consumes it on the
  │                             next real navigation into Mods (after setting
  │                             CurrentDestination = Mods first)

  IModThumbnailService ──────── the focused UI-owned presentation-media service
                             (the one deliberate exception to the domain-I/O
                             boundary): returns an Avalonia IImage, HTTPS-only,
                             owns the disk + in-memory thumbnail cache
  IDialogService ────────────── the testable dialog seam (six true-modal methods);
                             production DialogService owns the real Window wiring
 LocalizationService ───────── the i18n indexer + dynamic-culture INPC refresh
 IPreferencesService ───────── applies theme / font scale / language + persists
```

The backend libraries (`Profiles`, `Mods`, `Integrations`, `Steam`,
`RelayClient`, `General`, `Nxm`) sit below this layer. The UI injects
their interfaces; it never constructs a backend type directly.

## The profile session (`IProfileSession`)

`IProfileSession` is the single authority for three things: which profile is
active, whether the active profile may change right now, and whether Darktide
is running. Both the Profiles destination's switch and its create-sets-active
route through the same gate (`RequestActive`), so the two paths can never
diverge.

`RequestActive(id)` is applied and persisted only when the game is not
running; otherwise it is a no-op (the active stays put). Delete-of-active is
gated separately by `CanDeleteProfile(id)` (false when the id is the active
one and the game is running); when delete-of-active does happen, the game is
already stopped, so `ReconcileActive` clears the active id to null. The user
then explicitly picks the next profile; Curator never auto-selects a remaining
one on the user's behalf.

The running-state is live. The production `ProfileSession` snapshots
`ISteamService.IsGameRunning()` at construction, then a `DispatcherTimer`
re-checks roughly every 3 seconds (`ProfileSession.PollInterval`). The
session exposes `Refresh()` for callers that just caused a state change (the
shell after a successful launch) so the indicator reacts immediately rather
than waiting for the next poll. The session raises
`INotifyPropertyChanged.PropertyChanged` for `ActiveProfileId` and
`IsRunning`, which the shell and other consumers subscribe to.

The session lives in the UI layer (not a backend library) because the polling
timer is UI-session glue: a `DispatcherTimer` runs on the UI thread and ties
the lifetime of the running-state signal to the desktop lifetime. Backend
services have no UI thread and no business owning one. The timer is injected
as a `startTimer` delegate so unit tests construct the session without a UI
dispatcher and drive `Refresh()` directly for deterministic state changes.

### How session changes cascade

`OnSessionPropertyChanged` on the shell mirrors `IsRunning` into the shell's
own `IsGameRunning` (which cascades through `LaunchCommand`'s can-execute, the
status-strip label, and the navigation + Add/Delete/Switch gates). The mod
list's `OnSessionPropertyChanged` filters to `ActiveProfileId` only: a
running-state change does not reload the list (the list stays put while the
game runs; edits land on the profile the user will launch next). Active-id
changes rebuild the list from the new profile.

## The shell (`ShellViewModel` + `MainWindow`)

The shell owns navigation across five hosted destinations (the
`ShellDestination` enum: Profiles, Mods, NexusIntegrations, Preferences,
Settings), the global Launch action, and the global status strip. It does not
own the active id, the gate, or the running-state; those are the session's.
The shell does own the launch-attempt state (below). Launch availability
derives directly from `IProfileSession.ActiveProfileId` + `IsGameRunning` +
the shell's `IsLaunchAttemptInProgress`, and Launch resolves the active id
from the session at execution time rather than from a cached selection. Mods
is selected initially; the pane starts collapsed (compact icon rail); the
hamburger button toggles `IsPaneOpen`.

### Shell appearance: the Launch action and the theme-safe update notice

The global Launch action (the header `Button` bound to `LaunchCommand`) is the
primary game action and carries a branded iron-and-rust treatment via the
`Button.launchAction` class: a dark gunmetal face, off-white text set in the
embedded **Quantico Bold** display font (`avares://Modificus.Curator/Assets/fonts/Quantico-Bold.ttf#Quantico`, SIL OFL 1.1, shipped unmodified), a rust lower
edge, and a low corner radius. A drawn
Material play-arrow `<Path>` precedes the uppercase visible label
(`Launch_ButtonDisplay`); the accessible name and tooltip stay the ordinary
`Launch_Button` ("Launch Darktide"). The label is bold with 1-DIP letter
spacing and `TextOptions.TextHintingMode="Strong"` plus
`TextOptions.BaselinePixelAlignment="Aligned"` so the display face stays crisp
at button size and at fractional scaling. The font's human-readable license and
copyright (`Assets/fonts/OFL.txt`) are copied to both build and publish output
alongside the assembly. Every face, border, and foreground color
is an app-owned `CuratorLaunch*` resource defined once in `App.axaml`
(theme-independent, so the branded look is identical in Light and Dark), set on
the Fluent `ContentPresenter` so explicit `:pointerover` / `:pressed` /
`:disabled` / `:focus-visible` states outrank the theme's own per-state setters.
Press keeps the Fluent `scale(0.98)` compression (no layout jump); only colors
change. Keyboard focus turns the border Curator cyan at the same thickness, so
the focus indicator is highly visible and adds no layout shift. MinHeight is 44
DIP. No custom control, converter, or code-behind behavior is used; it is pure
scoped styling over the stock Fluent Button.

The dismissible app-update pill in the status strip is theme-safe (issue #181):
the pill, its link, and its dismiss icon draw only from app-owned
theme-driven brushes (the `CuratorUpdateNotice*` resources, one set per Light
and Dark `ThemeDictionary`), never from `SystemAccentColor` or
`SystemControlForegroundAccentBrush`. The link (`HyperlinkButton.updateNoticeLink`)
and the dismiss (`Button.updateNoticeDismiss`) are scoped with `/template/
ContentPresenter#PART_ContentPresenter` selectors covering normal,
`:pointerover`, `:pressed`, `:disabled`, and `:focus-visible`; class-qualified
selectors outrank the Fluent ControlTheme's accent-reapplying per-state setters,
so a low-contrast platform accent (SteamOS supplies one the app cannot control)
cannot make the notice illegible. The link keeps its underline and hand cursor.
A subdued Curator cyan/teal treatment reads as informational without competing
with the Launch action. Behavior (`CheckAppUpdateNowCommand` /
`DismissAppUpdateCommand`, the tooltips, and the layout relative to the adjacent
status indicators) is unchanged.

### Open-pane width grows to fit the widest localized label

The SplitView's XAML `OpenPaneLength=200` is the design-time/startup fallback
and the lower bound. Once the window is open, `MainWindow.axaml.cs` measures
the live localized pane labels (the five destinations plus the pane-bottom
Exit) with the representative `NavMeasureLabel` TextBlock's actual typography
(`FontFamily`, `FontStyle`, `FontWeight`, `FontStretch`, `FontSize`,
`LetterSpacing`) via the Avalonia 12.1 `TextLayout` API, unwrapped with
infinite width, and grows `NavSplitView.OpenPaneLength` to
`clamp(ceil(48 + 12 + widest + 16), 200, 360)`. Future translations and font
scales therefore do not clip at the original 200px; the cap keeps the pane
from eating too much of the content area, and beyond it each label's
`TextTrimming=CharacterEllipsis` is the graceful fallback (the full text
remains in the tooltip and the automation name). Re-measurement fires on
inherited `Window.FontSize` changes (PreferencesService applies the user's
font scale by overwriting the AppFontSize DynamicResource) and on
LocalizationService Culture / `Item[]` changes (a culture flip re-resolves
every label). The pure arithmetic lives in the internal
`MainWindow.ComputeOpenPaneLength` helper so unit tests can exercise it
without a live Window; the live measurement + update path is covered by XAML
compilation + operator visual testing.

### Persisted window geometry + the Exit anchor

`MainWindow` remembers the last unmaximized (Normal) client size and whether
the last meaningful state was Maximized, persisted as one atomic record under
`IAppStateStore.MainWindowState` (the `AppWindowState` value type in General;
width + height in DIP plus the boolean flag). No window position is stored;
`WindowStartupLocation` stays `CenterScreen`.

At construction `MainWindow` reads the saved state and the primary screen's
working area (physical pixels, converted to DIP via `Screen.Scaling`), runs
both through the pure `NormalizeSavedSize` helper (which validates finite +
positive dimensions, clamps to the XAML minimums `MinWidth=720` /
`MinHeight=480` and the work area, and signals a fallback when the screen is
unavailable or the state is absent/invalid), and applies the clamped Normal
size as `Width`/`Height` before first Show so the platform has the right
restore size. The persisted maximized flag seeds both the in-memory
meaningful-state flag and the one-shot first-open maximize immediately, so a
Maximized close reopens Maximized regardless of `OnOpened`/`OnPropertyChanged`
ordering. When the saved flag was Maximized, the window maximizes once in
`OnOpened` (after Show) for Win32/X11 consistency; a later unmaximize then
returns to the saved Normal size. An unavailable or invalid screen (non-finite
or non-positive scaling or working-area dimensions), an absent or corrupt
persisted state, or a corrupt store all fall back to the XAML 960x640 size and
never crash startup.

The last Normal size is tracked through deferred, coalesced, reason-aware
resize observation, because the platform's settled-state ordering is not
reliable: Win32 reports the maximized resize BEFORE its managed `WindowState`
change, while X11 generally reports the state first. `OnResized` tags each
observation by `WindowResizeReason` and by whether the window had already
opened, then posts ONE apply to the UI thread. At apply time the settled state
has propagated, and a trusted observation (User, Unspecified, Application,
DpiChange) becomes the last Normal size when the settled state is `Normal`,
the window is not closing, and the candidate is valid. A `Layout` observation
is never authoritative for the persisted size. The meaningful-state flag is
tracked through `OnPropertyChanged` for `WindowStateProperty` via the pure
`NextMeaningfulMaximized` policy: `Normal` clears the flag, `Maximized` sets
it, and `Minimized` and `FullScreen` leave the preceding flag unchanged. So a
Normal then Minimized then Close restores Normal, and a Maximized then
Minimized then Close restores Maximized with the saved unmaximized size;
Minimized is never persisted as a launch state.

A narrow post-open correction works around Avalonia issue #19431
(https://github.com/AvaloniaUI/Avalonia/issues/19431): at Windows scaling such
as 175%, a Maximized to Normal transition can emit a correct `Unspecified`
Normal resize followed by a stale `Layout` resize carrying the maximized
`ClientSize`. `MainWindow` uses manual top-level sizing, so a post-open
`Layout` resize is not a user sizing intent. When the settled state is Normal
and a post-open `Layout` observation materially conflicts (more than 1 DIP)
with the trusted last Normal size, `MainWindow` reapplies that trusted size
through `ClientSize`. The correction never persists a new size from `Layout`
and never manipulates window position; a trusted observation arriving in the
same burst as the stale `Layout` wins and becomes the last Normal size before
the correction is decided, so the correction targets the trusted value.

State is never written on every resize. `OnClosing` calls base (so a
`Window.Closing` subscriber can still cancel), and if not cancelled it marks
the window closing (any queued resize apply then no-ops), consumes any pending
trusted candidate when the settled state is Normal (never the raw `ClientSize`,
which may be the stale #19431 value), and writes one atomic `AppWindowState`.
Closing while Maximized or Minimized keeps the tracked last-Normal size and
meaningful flag (the maximized or minimized client size is not what an
unmaximize should restore to). The Exit button in the pane calls `Close()`
exactly like the title-bar close, so the persisted state lands through the
same path.

The Exit button is anchored at the pane bottom by the pane `Grid`'s
`Auto,*,Auto` rows: the hamburger is row 0, the destination `StackPanel` is
the middle star row (it grows to fill and pushes the third row down), and Exit
is the only row-2 control. It is not a destination: it has no `selected`
state, no `NavigateCommand`, no `ShellDestination`. Compact mode shows its
drawn Material logout geometry with a tooltip + accessibility name; expanded
mode adds the localized `Exit` label. Reusing the `navItem` tile chrome keeps
it a visual sibling of the destination buttons.

### The `NavigateAsync` lifecycle

A same-destination call is a strict no-op (no guards, effects, or config
reads), so a pending DMF trigger survives same-destination Mods clicks; it is
consumed only by a real navigation into Mods. For a real destination change,
`NavigateAsync` runs in order: (1) the current destination's leave effects;
(2) switch `CurrentDestination`; (3) the target's enter effects. Leaving
Profiles awaits the unsaved-changes three-choice guard, and Cancel (or a
Save that the service rejected) keeps `CurrentDestination` and all target
state unchanged. Leaving Nexus calls
`IntegrationsViewModel.Deactivate` (cancels the in-flight auth), then the
shell re-reads the nxm handler status and reloads the mod list. Leaving
Settings reloads the mod list, re-reads the `CheckOnStartup` toggle, and
refreshes the app-update notice. Enter effects: Settings calls
`SettingsViewModel.RefreshFromConfig` synchronously (so escape-hatch / config
changes are visible without a transient stale page); Nexus awaits
`IntegrationsViewModel.RefreshAsync` (paint-then-resolve); Mods awaits
`DmfPromptService.ProcessPendingAsync` after `CurrentDestination` is already
Mods, then reloads `ModListViewModel` when a trigger was consumed so an
accepted existing/Premium DMF add is visible immediately. The destination is
switched before any enter await so it stays active even if a refresh or the
DMF prompt reports an error through its own behavior.

There is no shared `IPage` / `INavigationService` lifecycle interface:
Profiles, Settings, and Nexus have deliberately different
activation/deactivation capabilities, so the shell calls each concrete page
VM directly. The hosted page VMs are application-lifetime singletons;
navigation never calls an old Window-close final-cleanup (`Detach`) path.

### Launch + the launch-attempt state + the result branches

`LaunchCommand` resolves the active id from `IProfileSession.ActiveProfileId`
at execution time. Before invoking the launch service it sets the shell-owned
`IsLaunchAttemptInProgress` (a method-level guard also refuses a second,
direct/programmatic execution while an attempt is active; `CanExecute` alone
gates only the button), then yields once to the Avalonia dispatcher at
`DispatcherPriority.Loaded` (after layout + render, before subsequent input)
so the freshly-disabled button and the launch overlay paint before the
synchronous discovery/staging/spawn call runs on the UI thread. The button's
text, tooltip, and accessible name are unchanged; the overlay is the
additional feedback. It then calls
`IRelayLaunchService.Launch(activeProfileId)` and branches on
`LaunchResult.Status`:

- **`Launched`**: an immediate `_session.Refresh()` so the indicator and
  launch-availability react at once, not on the next poll. Successful launch
  surfaces no status note or other confirmation; the running indicator is the
  durable signal. The attempt state then stays set until the session's
  running-state signal observes Darktide AND the spawned Relay process exits
  (Darktide's process appears before Relay finishes its injection work, so
  the game is not visually up until Relay exits), or a 30-second timeout
  elapses releasing the whole combined wait (the timeout starts only after
  the spawn returns; a false polling result never clears the state). When
  both conditions land, the ordinary `IsGameRunning` gate keeps Launch
  disabled; on timeout with the wait unresolved, retry becomes possible. The
  wait observes the existing session signal + the launch facade's relay-exit
  task (`LaunchResult.RelayExited`: Relay directly on Windows, the Proton
  wrapper process on Linux, whose exit follows Relay's under `proton run`;
  subscribe-before-check so a flip cannot be missed, the temporary
  subscription removed deterministically): bounded detector handoff, not
  process waiting or process ownership (the shell takes no process handle;
  the facade owns the spawned handle and its disposal; Darktide stays
  untracked beyond the session signal).
- **`DiscoveryIncomplete`**: opens the focused escape-hatch dialog with the
  missing fields. No auto-retry: the user submits the paths, closes the
  dialog, and clicks Launch again. A loop here would trap the user if they
  could not get the paths right.
- **`StagingFailed`**: a localized modal alert. `Message` carries the raised
  exception's body (a runtime/OS error); the alert composes the localized
  framing + hint, then appends that body (mirroring the Update/Import failure
  alerts).
- **`Error`**: a modal alert with the result's message.

The attempt state stays set while a failure dialog is open and clears in all
completion and exception paths (a single clear point after the result
handling), so retry becomes possible exactly when the flow finishes. The
pre-launch yield and the handoff timeout are injected delegates (production:
the dispatcher yield + a real 30-second delay; tests: completed or
TaskCompletionSource-backed tasks) so unit tests need no live Avalonia
dispatcher and never wait real time. The relay-exit half of the wait rides
the launch result itself (`LaunchResult.RelayExited`; tests hand the shell a
TaskCompletionSource-backed task through the fake launch service).

### The full-client launch overlay

While `IsLaunchAttemptInProgress` is true, the whole Curator client area is
blocked by an in-window overlay. The main window's content is a root `Grid`
hosting the shell SplitView plus the overlay as its final child (explicit
`ZIndex` on top), so the overlay is a layered sibling, not inserted into the
content flow, and layout never shifts. Blocking is doubled: the SplitView's
`IsEnabled` is bound to the inverse attempt state (keyboard focus and
activation cannot reach any shell control), and the overlay itself is a
hit-testable panel carrying a semi-opaque scrim background across the entire
client area (pointer input stops at the scrim). Native window chrome is
untouched: the overlay lives inside the client area only, so the window can
still be moved, minimized, or closed while a launch is in flight.

Centered over the scrim sits a compact progress card in the same iron-and-rust
palette as the Launch action (app-owned `CuratorLaunchOverlay*` brushes: a
theme-independent gunmetal card face + rust edge + off-white text, and a
theme-dependent scrim opacity, never the platform accent), carrying the
localized "Launching Darktide" title, the localized "Preparing your modded
game…" message (a real ellipsis, matching the localization style), and an
ordinary indeterminate `ProgressBar` animated by the Fluent ControlTheme's
own keyframes (no custom spinner control, no composition animation, no
code-behind animation machinery). There is no Cancel control or any other
interactive element: once Relay starts there is no safe cancellation
contract. Accessibility is declarative: the overlay and card carry
`AutomationProperties.Name` values from the localized strings and the overlay
is marked a polite live region; no focus trap and no imperative
accessibility service is introduced.

The overlay's visibility binds directly to the attempt state (no second state
machine), so it appears with the pre-launch render yield, stays through the
synchronous launch and the post-spawn handoff until Darktide is detected and
the spawned Relay process exits (or the 30-second timeout expires), and
disappears through the existing `finally`
state clear. Failure and discovery dialogs are separate OS-owned dialog
windows (`Window.ShowDialog`), so they appear above the overlay while failure
handling is in progress; the overlay remains behind them as the dimmed shell.

### The DMF install-prompt timing

The `DmfPromptService` subscribes to `IProfileService.ProfileCreated` at
construction (the shell's DI registration resolves `DmfPromptService` before
`ShellViewModel` so the subscription exists before any profile can be created).
When `ProfilesViewModel.Save` calls `CreateProfile`, the already-subscribed
coordinator records the trigger as pending; `ProfilesViewModel` itself does no
DMF or mod-list work after Save. The shell consumes the pending trigger on the
next real navigation into Mods: `NavigateAsync` sets `CurrentDestination = Mods`
first, then awaits `ProcessPendingAsync`, then reloads `ModListViewModel` when a
trigger was consumed. The DMF prompt therefore runs as the topmost modal with
Mods already selected underneath, and an accepted existing/Premium DMF add is
visible immediately afterward. A pending trigger survives visits to other
destinations and is consumed only on a real Mods entry.

`ProcessPendingAsync` snapshots and clears the pending trigger before
processing it, so an exception in the prompt does not leave it stuck pending
for the next call. The boolean return carries "a trigger was consumed" (whether
the prompt actually fired stays internal) so the shell knows whether a
post-prompt mod-list reload is warranted.

## The mod list (`ModListViewModel` + `ModItemViewModel`)

`ModListViewModel` owns the active profile's mod list (the dominant content
area). It subscribes to `IProfileSession.PropertyChanged` (filtered to
`ActiveProfileId`), `LocalizationService.PropertyChanged` (culture refresh),
and `IUpdateCheckService.CheckCompleted` (badge refresh). The active profile
is the session's; the list never decides the active id, it only reloads when
the id changes.

The command set:

- **Enable / disable** (`ToggleEnabled`): the row's `Enabled` is two-way
  bound to its CheckBox; this persists the toggle through
  `IProfileService.SetModEnabled`.
- **Reorder** (`MoveUp` / `MoveDown` / drag grip): moves an unlocked row one
  unlocked rank, crossing any locked rows, and persists the full container-id
  order through `IProfileService.SetModOrder` (exactly once) on a real order
  change. A drag is initiated only from the per-row grip at the left edge (a
  pointer press there calls `PreventGestureRecognition` + captures the pointer,
  and a reorder starts after an 8-DIP threshold); dragging anywhere else on a
  row stays touch scrolling, which matters on the Steam Deck. While dragging,
  the target rank is computed among the other unlocked rows only (locked rows
  are never destinations), a 2-DIP accent insertion line marks the target, the
  realized item container (the full-width actual row) is lifted via a render
  transform + z-index so it follows the pointer while its layout slot stays
  reserved, and a `DispatcherTimer` edge-band auto-scrolls the
  list while keeping the lifted row under the pointer. Every mutated container
  property is restored on each finish/cancel path. A release inside the viewport commits one immutable `ReorderRequest`
  (source container id + target unlocked rank) through `CommitReorder`; the pure
  `ModReorderPlanner` builds the legal full order around locked slots and
  rejects same-rank / out-of-range / locked-source / missing-source requests
  without a service call. On release, the target is recomputed from the final
  release position (so it reflects the layout at release after any auto-scroll),
  then capture is released and `CommitReorder` runs. Escape, capture loss, view
  detach, a release outside the viewport, or an invalid target all cancel without
  persistence. The gesture is single-pointer: a second press while a row gesture
  is armed is ignored, and Move / Release / CaptureLost process only the active
  captured pointer (by reference). The gesture
  is custom pointer handling, separate from the outer Grid's native external
  file/folder `DragDrop` handlers (which stay external-only).
- **Order lock** (`ToggleOrderLock`): toggles `ModListEntry.OrderLocked` through
  `IProfileService.SetModOrderLocked`. A locked row keeps its exact zero-based
  position across any reorder or auto-sort; its grip stops intercepting pointer
  input (the area falls through to touch scrolling) and both move buttons
  disable. Lock metadata alone does NOT set `IProfileSession.HasPendingChanges`:
  it does not change the staged mod tree or `mods.lst`.
- **Per-mod policy** (`SetPolicyLatest` / `SetPolicyPinned`): routes through
  `IProfileService.SetModPolicy`. The pin is a constrained dropdown of the
  container's actual versions (the dropdown exposes the readable tag, stores the
  opaque folder id, and the parent wraps it as
  `PinnedPolicy(versionId)`).
- **Remove** (`Remove`): a confirm gate, then `IProfileService.RemoveMod`.
  The repository copy survives; the confirm is about the profile edit, not
  data loss.
- **Auto-sort** (`AutoSort`): applies the `IModOrderResolver` and persists. The
  current resolver is the identity resolver (order unchanged); a real
  dependency-driven resolver is a separate concern. The seam is DI-swappable, so
  the UI wires against the abstraction, and a locked row keeps its position
  through auto-sort because the resolver routes through the lock-projecting
  `SetModOrder`.
- **Add** (inline import workflow): the Add split button's four flyout items are
  all modes that set themselves as the default on click (the face label tracks
  the mode): "Add Nexus Mods" (the default; opens the Darktide Nexus Mods games
  page in the browser via `AddNexusMods`), "Add Mod (archive)", "Add Mod
  (folder)", and "Link external folder". The archive + folder modes open their
  pickers and share an entry point with drag-and-drop; all forward the selected
  paths to `ImportWorkflowViewModel.StartBatchCommand`, which owns the inline
  card (the batch state machine, the per-item editing form, and the per-item
  import orchestration). The "Link external folder" mode reduces to `LinkMods`
  instead (folder picker, no inline card).

### The inline import workflow (`ImportWorkflowViewModel`)

The inline import card (a hosted `UserControl` directly below the Mods toolbar)
is an application-lifetime singleton
child VM exposed read-only on `ModListViewModel` and registered before it in
`CuratorComposition`. The card processes one path at a time through three
states: editing (the per-item metadata form), processing (filesystem work in
flight), and terminal failure (an inline error with a Close action).

`StartBatchCommand` captures an ordered copy of the selected/dropped paths and
the active profile id. Each item defaults to Nexus source, empty Version/URL,
and Latest policy. `ImportCurrentCommand` runs the per-item import:

1. Peek the base folder name via `IModImportService.GetBaseName` on a worker
   (`Task.Run`). This validates the source structure (exactly one base dir with
   a matching `<base>.mod` descriptor) before any container or version is
   created. An invalid source becomes a terminal inline failure that aborts the
   remaining batch.
2. Resolve the would-be container via `FindExistingContainer` and check the
   captured profile for a base-name collision via
   `IProfileService.GetBaseNameCollision` on the captured UI context (between
   the two worker awaits). Two mods with the same base folder name cannot
   coexist in one profile. The would-be container is excluded (a re-add of a
   mod already in the profile is not a collision). On a hit, the collision
   explanation shows inline and the batch aborts; nothing is created.
3. `IModImportService.Import` (extract or copy into the repository) on a worker
   (`Task.Run`). The chosen policy drives the new entry: `LatestPolicy` (the
   default) tracks the container's newest release; `PinnedPolicy` freezes the
   entry to the version being imported (constructed from the opaque version id
   the import just minted). `AddMod` runs on the captured UI context.
4. On success, raise the narrow `ItemImported` event carrying the captured
   profile id (the mod list reloads when it matches the active profile), mark
   pending changes when the profile is still active, and advance to the next
   path. After the last item the card hides.

Only `GetBaseName` and `Import` run on `Task.Run`; the continuation resumes the
captured UI context between them so the single-UI-thread profile/repository
queries never run on a worker. No `ConfigureAwait(false)`.

A Cancel (editing only), a terminal failure, or a collision clears the
remaining batch. Mods imported earlier in the batch stay imported. The failure
message is derived from a durable descriptor through the live
`LocalizationService` on every access, so a culture change re-resolves it.
Copied local-import failures never call `ShowAlertAsync`; the linked-folder
flow continues using modal alerts.

If the active profile changes while editing or showing a failure, the workflow
resets immediately. If it changes while an item is processing, the confirmed
item finishes against the captured profile, the remaining queue is aborted, and
the workflow resets; the new active profile's pending indicator is never set
for the old profile's success. A failure (expected or unexpected) that lands
after the profile changed also resets rather than showing a failure card.

### The link flow (`LinkMods`)

The "Link external folder" flyout adds an external mod directory to the
active profile **without copying it** (the folder is the user's; Curator
controls only load order and enabled/disabled). No inline workflow card; the
folder picker hands the path straight to `LinkMods`, which processes each path
sequentially:

1. Peek the base folder name via `IModImportService.GetBaseName` (the picked
   folder IS the base and must directly contain `<base>.mod`). An invalid
   shape surfaces an alert naming the path + aborts the remaining batch.
2. Hard-block a base-name collision via
   `IProfileService.GetBaseNameCollision`, excluding a re-link (which
   resolves to the existing linked container and refreshes it instead of
   being flagged). On a hit, an alert + abort; nothing is created.
3. `IModImportService.LinkFolder` (record the metadata-only
   `LinkedSource` container, no copy), then `IProfileService.AddMod` with
   `LatestPolicy` (inert for linked, since a linked container has no
   versions).

A failed peek, a `LinkFolder` failure (e.g. a containment rejection of the
mods/profiles root), or a collision cancels the remaining batch.

### Rows carry state only

Each row is a `ModItemViewModel`: container id (immutable, the join key
against `IModRepository`), display name, source, resolved version tag,
enabled, order, policy, and the per-row policy-edit state. The row also
carries optional display metadata (`ModDisplayMetadata`: summary, thumbnail
URL, adult flag, joined from the container on reload), the decoded
`Thumbnail` image, and an `IsDetailed` projection pushed down by the density
coordinator. The row never talks to `IProfileService` directly; the parent
owns every service call, and the view routes row interactions (toggle, move,
policy, remove, update) through code-behind handlers calling the parent's
commands with the row as the `CommandParameter`. This per-row code-behind
pattern keeps each row a passive state holder while the parent owns the
service boundary.

## Compact / Detailed rows (`DetailedModRowsViewModel`)

The Mods toolbar carries a Compact/Detailed density selector: two drawn-icon
buttons (`view_headline` for Compact, `view_agenda` for Detailed) bound to
`DetailedModRowsViewModel.SetDensityCommand` with the `ModRowDensity` enum as
the parameter. The active button carries the `selected` class (bound to
`IsCompact` / `IsDetailed`, the shell's conditional-class pattern, not a
ToggleButton). The selection persists in `CuratorConfig.Preferences.ModRowDensity`
(absent or unknown normalizes to `Detailed`, the default; Compact survives only
when explicitly persisted or selected).

`DetailedModRowsViewModel` is an application-lifetime singleton child VM of
`ModListViewModel`, analogous to `ImportWorkflowViewModel` and registered
before it in `CuratorComposition`. It isolates the density selection, the
metadata-backfill invocation, and the thumbnail hydration lifecycle from the
already-large parent. It reads and writes `ModRowDensity` through its own
focused read-modify-save (not `IPreferencesService.ApplyAndPersist`), so it
does not widen that method.

### How rows reach the coordinator

`ModListViewModel.Reload` joins each row's `ModDisplayMetadata` from the
container (alongside the name, source, and version) and hands the final row
snapshot to the coordinator via `SetRowsAsync`. The call is fire-and-forget:
the returned task absorbs every failure internally (cancellation is caught,
every other exception is logged), so the parent's intentional discard can
never fault. On a no-active-profile reload, an empty snapshot is handed over
so any prior generation is cancelled.

### The generation lifecycle

Every `SetRowsAsync` call cancels the prior generation and starts a new one
(an incrementing counter). The synchronous setup (snapshot the rows, push the
current density down to each row, clear thumbnails on Compact) runs before the
method returns; in Compact mode the returned task is already completed. In
Detailed mode the returned task represents the whole generation: known-
thumbnail hydration for eligible rows, the metadata backfill, and every
thumbnail load started by a backfill result.

Metadata and thumbnail results are applied only when four conditions all hold
at the continuation: the generation is still current, the mode is still
Detailed, the exact row object is still in the snapshot, and the row's
`ThumbnailUrl` is unchanged. A profile switch, a Compact toggle, or a
superseding reload therefore prevents stale assignment without aborting the
thumbnail service's shared cache load (the load runs to completion and may
still populate the cache for a later caller). All observable row mutation
resumes on the captured UI context; the coordinator uses no
`ConfigureAwait(false)` (the UI-layer convention).

### The Detailed-mode pipeline

In Detailed mode the coordinator:

1. Starts known-thumbnail hydration for every eligible row (Detailed + Nexus
   + non-null metadata + not adult + a non-empty `ThumbnailUrl`).
2. Invokes `INexusModMetadataService.BackfillMissingAsync` with the current
   row container ids as priority order.
3. For each container the backfill enriched, re-reads the repository metadata
   as authoritative (the atomic `TryInitializeDisplayMetadata` is the
   correctness boundary, so a concurrent writer wins), applies it to the row
   via `ApplyDisplayMetadata`, and starts that row's thumbnail load when it
   is now eligible.

`ApplyDisplayMetadata` clears any existing thumbnail when the new metadata is
adult, has no thumbnail URL, or carries a different URL than the old thumbnail
was loaded from. The row itself performs no I/O and calls no service.

### Adult-content policy

An adult-content flag is only a persisted boolean. The coordinator skips the
thumbnail for an adult row (the row shows the ordinary placeholder), and no
badge, filter, warning, or setting hangs off it.

## The mod-thumbnail service (`IModThumbnailService`)

`IModThumbnailService` is the one focused UI-owned presentation-media service
and the deliberate exception to the domain-I/O boundary. It returns an
Avalonia `IImage`, a presentation type the backend libraries do not depend on,
and owns only an HTTP/disk cache. Its contract:

- **HTTPS-only.** A null, empty, malformed, relative, or non-HTTPS URL returns
  `null` without a network round-trip or a cache side effect.
- **Cache key.** The lowercase SHA-256 hex of the normalized URL, stored as
  raw bytes under `AppPaths.ModThumbnailCacheDir`
  (`<app-data>/cache/mod-thumbnails`); no extension is stored.
- **8 MiB cap.** A response declaring more, or streaming past the cap, is
  rejected.
- **Atomic write.** A download lands in a sibling temp file, then a same-volume
  `File.Move` into the cache path; a download failure leaves no final file.
- **Four-slot load bound.** A semaphore bounds distinct concurrent loads to
  four; same-key loads coalesce into one shared task.
- **Per-caller cancellation.** The shared load runs uncancellable
  (`CancellationToken.None`), so cancelling one caller never cancels another's
  load; each caller awaits it with `WaitAsync(ct)`.
- **Corrupt-disk retry once.** A corrupt or unreadable cache entry is deleted
  and re-downloaded once; a second decode failure returns `null`.
- **App-lifetime image cache.** A successful decode is kept in an in-memory
  cache for the app lifetime so multiple rows and reloads share it and no
  bound row observes a disposed image.
- **90-day prune.** Cache files older than 90 days are deleted best-effort
  once per service instance; one locked file does not abort the sweep.

The caller (the density coordinator) decides whether to request a thumbnail
for a given row; the service fetches whatever trusted HTTPS URL it is handed.
Every expected failure (invalid URL, HTTP failure, oversize data, decode
failure, I/O failure) returns `null` and logs, without surfacing a modal;
caller cancellation propagates.

## The update UI

The mod list consumes `IUpdateCheckService` + `IUpdateStateStore` for update
flags and the per-row update-action button. The check itself lives in
Integrations (see [mod acquisition](mod-acquisition.md) and
[Nexus API rate limiting](nexus-rate-limiting.md)); the UI only fires it,
reads the persisted state, and renders.

### Reading the result (profile-scoped + persisted)

Per-row update flags are the profile-scoped known-update state held in
`IUpdateStateStore` (backed by `IAppStateStore.KnownUpdates` in
`app-state.json`), NOT the single in-memory `IUpdateCheckService.LastResult`.
The check service records each authoritative outcome through the store at
publish time; `ModListViewModel` reads the store on reload, on profile switch,
after an acknowledgement, and on `CheckCompleted`. So a restart inside the
interval gate shows prior flags before any API call, and a result from one
profile never bleeds into another.

`ModListViewModel.OnUpdateCheckCompleted` re-hydrates from the store (the store
was just updated by the check service). The handler is marshaled to the UI
thread via an injected `invokeOnUi` seam (`Dispatcher.UIThread.Post` in
production) because `CheckCompleted` fires on the check's completing threadpool
thread and the handler iterates the UI-bound `Mods` collection. The application
is idempotent. One list-level flag still derives from the in-memory last result:

- `IsRateLimited`: the last check was rate-limited. Drives the Mods toolbar
  "check incomplete" notice. (This is a transient session-only signal; it does
  not need to persist and it must not erase known flags.)

### The premium gate

`IsPremiumUser` is read once at construction via
`INexusAuthService.GetCurrentStateAsync()`, fire-and-forget, and pushed down to
each row so the per-row tooltip and click behavior reflect it. The read hits
the network, so blocking the UI-thread constructor on it would stall startup;
the result lands sub-second and flips the flag. There is no mid-session
refresh (re-checking on Integrations activation would burn an API call each
time; a user signing in mid-session needs a restart for the click behavior to
switch to in-app install).

### The per-mod Update command

`Update(row)` branches on the verified Premium state:

- **Premium:** `UpdatePremiumAsync` acquires the global `UpdateCoordinator`
  (one install at a time, shared with the automatic updater; a second click
  while an install runs is a clean no-op), then calls
  `IModAcquisitionService.AcquireLatestNexusAsync(gameDomain, modId)` (the
  premium / auth-only path). The repository's `AddVersion` extracts into a
  sibling temp and atomically swaps on success, so a mid-update failure leaves
  the existing version intact. On success it acknowledges the install
  (`IUpdateStateStore.AcknowledgeInstall`, clearing the persisted known-update
  entry immediately, with no extra API check) and reloads. On failure it
  surfaces a user-facing alert. The finally block clears `row.IsUpdating` and
  releases the coordinator.
- **Regular / unknown:** `OpenFilesPage` opens the mod's Nexus files page in
  the user's browser via an injectable external-launcher seam. A launch failure
  surfaces a user-facing fallback alert (with the URL for manual copy) rather
  than being swallowed.

Defense: no-op when there is no active profile, the row is not Nexus plus
Latest (`IsNexusLatest`), no update is flagged (`UpdateAvailable`), or the row
has no `NexusModId`.

### The automatic-update service

`IAutomaticUpdateService` is chained directly from `UpdateCheckRunner` after
each check completes (the runner captures the exact result, not a potentially
raced `LastResult`). It runs only when the result's outcome is authoritative
`Success` with updates, `NexusConfig.AutomaticUpdatesEnabled` is on, the active
profile still matches, and a fresh `GetCurrentStateAsync` returns
`IsPremium == true` (the Premium request fires ONLY when the gates pass). It
installs sequentially under the `UpdateCoordinator`; per-mod revalidation gates
each entry, a profile switch stops the batch, per-mod failures are isolated and
aggregated into one alert, successful installs acknowledge immediately, and a
fully successful batch is silent beyond the per-mod progress indication. The
service raises `ModUpdateProgress` per mod (active=true before the acquisition,
active=false from the per-mod finally) so `ModListViewModel` can show the
spinner on the currently installing row; it reloads after the batch via the
service's `UpdatesApplied` event.

### The manual "check now" affordance

`CheckForUpdatesNow` routes through `UpdateCheckRunner.CheckNowAsync()` so the
runner stays the single owner of "fire a check" logic and uses the thorough
path (`IUpdateCheckService.CheckThoroughAsync`). `IsCheckingNow` is set before
the await and cleared in the finally block; it drives the header refresh
button's enabled state and an indeterminate `ProgressBar`. The await now also
covers the chained `IAutomaticUpdateService` batch, so the manual spinner stays
active through the installations. The existing `CheckCompleted` subscription
re-hydrates from the store when the result lands.

### View affordances

- **The Mods toolbar.** Refresh + an indeterminate spinner (the manual "check
  now" affordance), the rate-limit notice pill, the hidden auto-sort seam, the
  Compact/Detailed density selector, and the Add split button, in that order.
  The rate-limit pill occupies the toolbar's single flexible (`*`) column with
  `HorizontalAlignment=Left`: at normal and wide widths it keeps its content
  width, while the star column still gives it a finite constraint so its inner
  text ellipsizes (`CharacterEllipsis`, full text in the tooltip) at narrow
  widths rather than pushing the density pair or Add out of the toolbar. The
  density selector is two adjacent drawn-icon buttons (`view_headline` for
  Compact, `view_agenda` for Detailed) bound to
  `DetailedModRowsViewModel.SetDensityCommand`; the active one carries the
  `selected` class (bound to `IsCompact` / `IsDetailed`). A click on the
  already-active density is a strict no-op (the coordinator's value-equal
  guard), so the buttons stay enabled.
- **Row roots.** One row, two mutually exclusive roots selected by the row's
  `IsDetailed` projection: the existing Compact `Grid` (eight columns: name,
  badge area, enabled, policy, update-action cell, up, down, remove) and a
  Detailed rounded card. The Compact root is preserved unchanged except for
  the `IsVisible` flag that now gates it; the Detailed root is a rounded
  `Border` laid out as one adaptive Grid (the card root carries
  `Container.Name` + `Container.Sizing=Width`, so a `ContainerQuery
  max-width:680` in `UserControl.Styles` swaps the layout at the 680-DIP
  card-width breakpoint): column 0 is the thumbnail/placeholder slot, column 1
  holds the name + source badge (row 0) and a two-line plain-text summary
  (row 1, `MaxLines=2`, `TextWrapping=Wrap`, `TextTrimming=CharacterEllipsis`,
  full text in the tooltip and the automation name), and row 2 is the action
  strip. When the card is wide (greater than 680 DIP) a 112-DIP
  `UniformToFill` thumbnail spans all three rows and the action strip occupies
  only the right column; when constrained (at or below 680 DIP) the thumbnail
  shrinks to 72 DIP spanning name + summary and the action strip moves to a
  full-width row beneath both columns. Width, height, row span, and action
  column/span that change at the breakpoint are driven by styles (not local
  values, which would outrank styles); constant row/column positions stay
  local. Both roots bind the exact same per-row state and route to the exact
  same code-behind handlers, so no action behavior forks between modes; the
  markup is deliberately duplicated rather than abstracted. Horizontal
  scrolling is disabled on the page-level `ScrollViewer` (rows wrap rather
  than extend the page); the thumbnail placeholder (a drawn `image` geometry)
  shows for every no-image case (untracked/linked, missing metadata, no auth,
  offline, failed image, adult flag, empty URL), so the slot reads as a
  uniform empty-image affordance.
- The source badge is a `HyperlinkButton` styled as a pill, with
  `NavigateUri` set to the row's `SourceUrl` (the mod's remote page; null for
  untracked, which the `HyperlinkButton` treats as a no-op click). A linked row
  replaces this badge with a two-state indicator in the same cell: available
  shows an "External" pill whose click opens the OS file manager at the
  external folder (`OpenFolder`, via a testable path-launcher seam with a
  fallback alert on failure); broken (the external folder is missing) shows a
  non-clickable "Folder unavailable" text in the caution brush. The broken
  state is pushed from `IModRepository.IsExternalAvailable` at Reload
  (`IsExternalBroken`); there is no watcher, so availability is re-read on the
  next reload. Immediately
  left of the badge, an indeterminate `ProgressBar` (visible only while the
  row's `IsUpdating` is true) shows per-row update activity in the former
  update-status area.
- The stable update-action cell is a fixed-width `Panel` reserved on every row
  so later controls never shift. For Nexus + Latest rows it holds the
  update-action button; for Pinned Nexus, Untracked, and linked rows the cell
  stays reserved but empty (linked mods get no update check). The policy
  ComboBox is disabled for linked rows (a linked container has no versions to
  pin). The button shows for Nexus + Latest rows regardless
  of account tier and regardless of whether an update is available, and it
  stays visible while a row is updating (disabled via `UpdateActionEnabled`,
  which includes `!IsUpdating`); the progress affordance lives in the
  source-badge area, so the action cell never shifts during start/end of an
  update. No update: disabled, neutral download arrow, "Up to date" tooltip.
  Update available: enabled, accent-blue download arrow, with the tooltip
  distinguishing Premium install vs. open files page. The button's `IsVisible`
  binds to the row's `CanShowUpdateAction` (`IsNexusLatest`) and `IsEnabled` to
  `UpdateActionEnabled` (`UpdateAvailable && !IsUpdating && (!IsPremiumUser ||
  !AnyRowUpdating)`), both computed on the row so no parent-walk MultiBinding
  is needed.

## The app self-update UI

The shell and the Settings destination surface Curator's own self-update through
`IAppUpdateService` in Velopack-packaged builds (the Windows installer and Linux
AppImage). The check is fired once on
startup by `AppUpdateCheckRunner` and the result lands through the service's
`UpdateStateChanged` event; the UI reads `LastCheckResult`. Full detail on the
service, the update source, and the lifecycle is in
[app auto-update architecture](app-auto-update.md).

Two surfaces:

- **The shell status-strip pill.** A dismissible pill shown only when
  `ShowAppUpdateNotice` holds: self-update is supported, a check found an
  update (`LastCheckResult` non-null), and the user has not dismissed it this
  session. Clicking the pill runs the notice flow: a confirm ("vX is
  available, download and restart?"), then the download under the shared
  `ProgressDialog` spinner, then `ApplyUpdatesAndRestart` (which exits the
  process; Velopack relaunches). Cancel on the confirm leaves the pill
  visible; only the drawn close button dismisses it, and dismissal is
  session-only (not persisted, so a later update is not hidden).
- **The Settings "Updates" section.** Always rendered (so standalone, portable,
  and dev builds still see their version), with the current version, a "Check for
  Updates" button plus an inline indeterminate spinner and status line, and a
  "Download and Restart" button visible only when an update is available. The
  manual check calls `CheckForUpdatesAsync` off the UI thread; "Download and
  Restart" runs the same download-and-apply flow as the pill without the
  confirm (the user is already in the dedicated section).

Both view models subscribe to `UpdateStateChanged` and reflect any result that
already landed during construction. The event fires on a threadpool thread
(the service publishes from its background check), so both handlers marshal to
the UI thread through the same injected `Action<Action>` seam
(`Dispatcher.UIThread.Post` in production, a synchronous pass-through in
tests) that `ModListViewModel` uses for its `CheckCompleted` handler. The view
models use no `ConfigureAwait(false)` (the project rule); their network calls
run inside `Task.Run`. Download failures surface an alert and never proceed to
apply.

## First-run Welcome onboarding (`OnboardingService`)

The first-run Welcome coordinator shows a compact modal over the main window the
first time the app starts with `IAppStateStore.OnboardingCompleted` still
`false`. It explains that Nexus setup is optional, describes the update-check,
download-link, and Premium in-app update capabilities it enables, and summarizes
the sign-in/API-key plus download-link settings available in Integrations. It
offers two explicit actions: an accent "Set up Nexus" button and a secondary
"Continue without Nexus" button. ESC, the title-bar close button, and a window
close are all equivalent to Continue.

`ShowWelcomeIfFirstRunAsync` is a one-shot: it reads the persisted flag (and an
in-process guard) and no-ops when onboarding is already complete. On either
choice it persists completion BEFORE any further UI, so navigating away from
Nexus (or the navigation failing) can never cause Welcome to
repeat. On a "Set up Nexus" choice it navigates the shell to Nexus
(`ShellViewModel.NavigateToIntegrationsAsync`) after Welcome closes, so the
destination's auth refresh runs and leaving it later refreshes the shell's nxm
status.

The coordinator is wired after the main window is actually opened (Avalonia
modal dialogs require a shown owner): `App` subscribes to the main window's
`Opened` event once, resolves `OnboardingService`, and fires the call; a failure
inside onboarding is logged and swallowed so it never crashes startup. The
coordinator stays unit-testable through the `IDialogService.ShowWelcomeAsync`
seam (returns a typed `WelcomeChoice`) and the `IAppStateStore` flag.

## The DMF install prompt (`DmfPromptService`)

The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator
surfaces a modal on the main window when a new profile becomes active and DMF
is not already in it. There is one trigger:

1. **Each time a new profile is created and becomes active** (a fresh ask per
   profile, no persisted flag). A profile created while Darktide runs does not
   become active (the session gates it), so no prompt fires in that case.

Configuring Nexus auth no longer surfaces a DMF prompt on its own; the one-time
Nexus setup offer lives in the Welcome flow instead.

### The two cases

On a trigger, the coordinator looks up DMF by source
(`new NexusSource { ModId = DmfModId }`) and checks the active profile's mod
list:

1. **DMF in the repo but not in the profile**: a Yes/No confirm. On Yes,
   `IProfileService.AddMod` adds it instantly (no download).
2. **DMF not in the repo**: a Yes/No confirm (the message tailors to whether
   Curator owns the `nxm://` handler: the manager-download path when it does,
   manual-import guidance when it does not). On Yes, premium users get the
   in-app API download under a modal spinner (the Nexus `download_link`
   endpoint is premium-only) plus the add. Everyone else (no auth, regular, or
   unknown premium) gets the DMF Nexus files page opened in the default browser
   regardless of `nxm://` setup, so the user is never left at an informational
   dead-end. On a browser-open failure, a fallback alert carries the files-page
   URL.

Decline is respected: nothing opens, no Integrations prompt. DMF can be added
later via the normal add flow. Whenever an accepted path does call `AddMod`
with DMF, the profile add boundary's fresh-add rule places it first (rank 0)
and order-locked; the prompt itself carries no placement choreography (see
the [profiles reference](../reference/profiles.md)).

### Why the prompt is owned by the shell on Mods entry

The trigger signal (`IProfileService.ProfileCreated`) fires synchronously from
inside `ProfilesViewModel.Save`. The coordinator subscribes at construction
(resolved eagerly in the shell composition path, before `ShellViewModel`
itself, so the subscription exists before any profile can be created) and
records the signal as pending; the shell's `NavigateAsync` consumes the
pending trigger on the next real navigation into Mods, after setting
`CurrentDestination = Mods` first, so the DMF prompt runs as the topmost modal
with Mods already selected underneath. `ProcessPendingAsync` snapshots and
clears the pending trigger before processing it, so an exception in the prompt
does not leave it stuck pending for the next call. The prompt is wrapped in a
try/catch that logs and swallows non-cancellation exceptions, so a wiring
failure never blocks the shell's post-navigation return. Splitting ownership
this way keeps the coordinator narrowly focused on the DMF cases (subscribe,
record, run, fail-isolated) and the shell broadly owning cross-destination
sequencing (when to prompt, which destination should be underneath the modal,
when to reload the mod list), without the two being coupled through a
page-level interface.

## Dialogs, preferences, and i18n

### `IDialogService`

The testable true-modal seam. View models depend on this interface, not on
Avalonia `Window` construction, so their logic stays unit-testable: a test
injects a recording fake instead of a real window. The production
`DialogService` owns every real `Window` and `ShowDialog` wiring. Hosted
destinations (Profiles, Mods, Nexus, Preferences, Settings) are not modals and
live entirely on the shell's SplitView content region; the inline import card is
a hosted `UserControl`, not a modal.

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
(no longer modal) surface inline in the `ImportWorkflowView` card; the
linked-folder flow continues using `ShowAlertAsync` for its failures.

`ShowUnsavedChangesAsync` is the dedicated three-choice unsaved-changes prompt
(left to right: Cancel, Don't save, Save; Save is the accent button; the
`UnsavedChangesChoice` enum defaults to `Cancel` so ESC, the title-bar close,
and a window close all behave like the explicit Cancel button). When `canSave`
is `false` the Save button is disabled and a concise localized explanation
shows beneath the buttons so the disabled action is not mysterious; Cancel and
Don't save stay available. A dedicated modal is preferable to parameterizing
binary `ConfirmAsync` into a generic N-button dialog: the three choices have
distinct caller-side semantics (Save runs the caller's save core, Don't save
reloads authority, Cancel preserves state), and the optional disabled-Save
explanation is specific to this prompt. The `ProfilesViewModel` dirty-
transition core branches on the result; Save routes through the same
`TrySaveCore` the Save button uses.

`ShowProgressAsync<T>` runs the supplied work under a buttonless, non-closeable
spinner (the `DialogTitleBar.ShowClose` styled property is set to false on
the progress dialog, so the user cannot dismiss an in-flight operation whose
partial result would be useless). The spinner is closed in either case; the
work's exception (if any) propagates to the caller.

Per-profile launch settings are edited inline in the Profiles destination (the
reusable `LaunchSettingsEditorView`/`LaunchSettingsEditorViewModel`), not as a
separate modal. `DialogService`'s owner-disabling workaround is
reference-counted so the owner window re-enables only when the outermost modal
closes (an inner modal closing does not prematurely re-enable it while an outer
modal is still open); single-modal behavior is unchanged.

### `IPreferencesService`

The single authority for applying user-facing preferences (theme, font scale,
language) to the running app and persisting them to `CuratorConfig`. The
composition root applies the loaded config at startup (before the main window
shows, so the first paint already reflects the user's choices); the
Preferences destination calls `ApplyAndPersist` on each change. All three
concerns (theme variant, global font scale, UI culture) live behind one method
so the values stay consistent: nothing else in the UI touches
`RequestedThemeVariant`, the `AppFontSize` / `AppStatusFontSize` resources,
or `LocalizationService.Culture` directly.

The `PreferencesService` publishes the scaled font sizes as application
resources: `AppFontSize` (base 14px, bound by the Window style in
`App.axaml`) and `AppStatusFontSize` (base 12px, bound by the status-strip
`TextBlock`). Both scale by the user's font scale, so the status strip grows
with the body.

### `LocalizationService`

The single authority for resolving localized strings at runtime. A singleton
registered in DI, it holds the current UI culture, exposes a string indexer
used by every XAML binding, and raises `PropertyChanged` so bindings refresh
live when the culture changes.

The indexer property name is `Item[]`; raising `PropertyChanged` for
`"Item[]"` tells every Avalonia indexer binding (`{Binding [Key],
Source={StaticResource Loc}}`) to re-evaluate, so the whole UI updates the
moment the culture flips (no restart). The XAML uses `{ReflectionBinding
[Key], Source={StaticResource Loc}}` rather than a compiled binding because
the indexer-based dynamic-language path is not expressible as a compiled
binding.

A `ResourceManager` over the neutral `Strings.resx` resolves by culture. A
missing key returns the key itself (visible, never throws). The service
holds its own culture and resolves strings with it directly; it does not
mutate the thread's `CurrentUICulture`, so only the UI text follows the
chosen language.

`App.OnFrameworkInitializationCompleted` swaps the XAML resource placeholder
for the real DI singleton, so every view's `{Binding [Key],
Source={StaticResource Loc}}` resolves through the live service.

### The custom title bar

`DialogTitleBar.axaml` is the shared custom title bar reused by the modal
dialogs. The outer `Border` carries
`WindowDecorationProperties.ElementRole="TitleBar"`, so the OS handles native
drag and double-click-to-maximize over this region (the Avalonia 12.x
custom-chrome pattern). The dialog's own `Window.Title` is mirrored as the
visible header. The close button is a drawn X `<Path>` (no Unicode glyph)
whose `Click` closes the owning window. The button carries
`WindowDecorationProperties.ElementRole="User"` so it receives pointer input
even though it overlaps the chrome; on Windows without that role the whole
title bar is claimed for non-client drag handling (`HTCAPTION`) during
`WM_NCHITTEST`, and the button would never receive events.

`DialogTitleBar.ShowClose` (a styled property, default true) hides the close
button. The progress-spinner dialog sets it to false so the user cannot
dismiss an in-flight operation.

## See also

- [UI reference](../reference/ui.md): public surface, exact
  signatures, and DI registration for the UI layer.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level
  tie-together (component model, the Relay contract, profiles, launch).
- [mod acquisition](mod-acquisition.md): the `NxmModDownloadHandler` (in the
  UI assembly) that coordinates the nxm download flow, and the
  `IModAcquisitionService` the per-mod Update button calls.
- [Nexus authentication](nexus-authentication.md): the auth factory and
  orchestrator the Nexus destination drives, and the
  `AuthStateChanged` event the DMF prompt coordinator subscribes to.
- [Nexus API rate limiting](nexus-rate-limiting.md): how the update check's
  rate-limit signal becomes the mod-list "check incomplete" notice.
- [App auto-update architecture](app-auto-update.md): Curator's own
  self-update in Velopack-packaged builds behind `IAppUpdateService`, surfaced in the
  shell status-strip pill and the Settings "Updates" section.
