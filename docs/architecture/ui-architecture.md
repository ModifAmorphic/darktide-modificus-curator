# UI layer: architecture

The UI layer is the Avalonia 12 front end of Modificus Curator. It is the only
part of the codebase that talks to the user: the shell window (a SplitView with
five hosted destinations), profile management, the mod list, every modal
(Welcome, confirm, import, discovery escape-hatch, alert, progress), global
preferences (theme, font scale, language), and the dynamic-language
infrastructure. The UI never touches the filesystem, the network, or any OS
API directly. Every data operation flows through a backend library service;
the UI only presents state and orchestrates calls.

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
│  │ [Profiles] [Mods] [Nexus Integrations] [Preferences] [Settings]     │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Global header ─────────────────────────────────────────────────────┐ │
│  │ Current destination title ·························· Launch Darktide│ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Content area (one persistent UserControl per destination,          ┐ │
│  │  visibility-switched by Is*Visible projections)                      │ │
│  │ ProfilesView | ModListView (drag-and-drop) | IntegrationsView |      │ │
│  │   ModListView header: rate-limit notice · refresh ·                  │ │
│  │     auto-sort · Add split button (Nexus Mods + 3 pickers)            │ │
│  │   rows:   name · progress + source badge · enabled · policy ·        │ │
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
  │   │   └── ModItemViewModel  one row; carries state only (no service calls)
  │   │
  │   ├── IntegrationsViewModel  the Nexus Integrations destination (RefreshAsync
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
  │                             navigates the shell to Nexus Integrations on a
  │                             "Set up Nexus" choice
  │
  ├── DmfPromptService ──────── records the new-profile trigger from
  │                             IProfileService; ProfilesViewModel awaits
  │                             ProcessPendingAsync immediately after the
  │                             create + activation (no intervening dialog)

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
Launch availability derives directly from `IProfileSession.ActiveProfileId` +
`IsGameRunning`, and Launch resolves the active id from the session at
execution time rather than from a cached selection. Mods is selected
initially; the pane starts collapsed (compact icon rail); the hamburger
button toggles `IsPaneOpen`.

### The `NavigateAsync` lifecycle

A same-destination call is a strict no-op (no guards, effects, or config
reads). For a real destination change, `NavigateAsync` runs in order: (1) the
current destination's leave effects; (2) switch `CurrentDestination`; (3) the
target's enter effects. Leaving Profiles awaits the dirty-discard guard, and a
rejection keeps `CurrentDestination` and all target state unchanged. Leaving
Nexus Integrations calls `IntegrationsViewModel.Deactivate` (cancels the
in-flight auth), then the shell re-reads the nxm handler status and reloads
the mod list. Leaving Settings reloads the mod list, re-reads the
`CheckOnStartup` toggle, and refreshes the app-update notice. Enter effects:
Settings calls `SettingsViewModel.RefreshFromConfig` synchronously (so
escape-hatch / config changes are visible without a transient stale page);
Nexus Integrations awaits `IntegrationsViewModel.RefreshAsync` (paint-then-
resolve). The destination is switched before any enter await so it stays
active even if a refresh reports an error through its own behavior.

There is no shared `IPage` / `INavigationService` lifecycle interface:
Profiles, Settings, and Nexus Integrations have deliberately different
activation/deactivation capabilities, so the shell calls each concrete page
VM directly. The hosted page VMs are application-lifetime singletons;
navigation never calls an old Window-close final-cleanup (`Detach`) path.

### Launch + the result branches

`LaunchCommand` resolves the active id from `IProfileSession.ActiveProfileId`
at execution time and calls `IRelayLaunchService.Launch(activeProfileId)`,
then branches on `LaunchResult.Status`:

- **`Launched`**: an immediate `_session.Refresh()` so the indicator and
  launch-availability react at once, not on the next poll. Successful launch
  surfaces no status note or other confirmation; the running indicator is the
  durable signal.
- **`DiscoveryIncomplete`**: opens the focused escape-hatch dialog with the
  missing fields. No auto-retry: the user submits the paths, closes the
  dialog, and clicks Launch again. A loop here would trap the user if they
  could not get the paths right.
- **`StagingFailed`**: a localized modal alert. `Message` carries the raised
  exception's body (a runtime/OS error); the alert composes the localized
  framing + hint, then appends that body (mirroring the Update/Import failure
  alerts).
- **`Error`**: a modal alert with the result's message.

### The DMF install-prompt timing

The `DmfPromptService` subscribes to `IProfileService.ProfileCreated` at
construction (the `ProfilesViewModel` DI factory resolves `DmfPromptService`
eagerly so the subscription exists before any profile can be created). When
`ProfilesViewModel.SaveAsync` calls `CreateProfile`, the already-subscribed
coordinator records the trigger as pending; `ProfilesViewModel` then awaits
the captured `processPendingDmf` delegate immediately after the create +
activation. There is no intervening dialog to wait for, so the DMF prompt is
the topmost modal at that point. After the prompt, `ProfilesViewModel`
invokes the mod-list reload delegate so an accepted DMF install shows without
a profile switch.

`ProcessPendingAsync` snapshots and clears the pending trigger before
processing it, so an exception in the prompt does not leave it stuck pending
for the next call.

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
- **Reorder** (`MoveUp` / `MoveDown`): swaps with the predecessor or
  successor, persists the new container-id order through
  `IProfileService.SetModOrder`, then reloads so the persisted
  `ModListEntry.Order` fields drive the display.
- **Per-mod policy** (`SetPolicyLatest` / `SetPolicyPinned`): routes through
  `IProfileService.SetModPolicy`. The pin is a constrained dropdown of the
  container's actual versions (the dropdown exposes the readable tag, stores
  the opaque folder id, and the parent wraps it as
  `PinnedPolicy(versionId)`).
- **Remove** (`Remove`): a confirm gate, then `IProfileService.RemoveMod`.
  The repository copy survives; the confirm is about the profile edit, not
  data loss.
- **Auto-sort** (`AutoSort`): applies the `IModOrderResolver` and persists.
  The current resolver is the identity stub (a no-op); a real
  dependency-driven resolver is out of v1. The seam is DI-swappable, so the
  UI wires against the abstraction now.
- **Add** (`AddMods`): the Add split button's four flyout items are all modes
  that set themselves as the default on click (the face label tracks the mode):
  "Add Nexus Mods" (the default; opens the Darktide Nexus Mods games page in the
  browser via `AddNexusMods`), "Add Mod (archive)", "Add Mod (folder)", and
  "Link external folder". The archive + folder modes open their pickers and
  share an entry point with drag-and-drop; all reduce to `AddMods`, which
  processes paths sequentially. The "Link external folder" mode reduces to
  `LinkMods` instead (folder picker, no modal).

### The import flow

`AddMods` runs one import modal per path. For each path:

1. Peek the base folder name via `IModImportService.GetBaseName`. This
   validates the source structure (exactly one base dir with a matching
   `<base>.mod` descriptor) before any container or version is created. An
   invalid source throws here and aborts the remaining batch.
2. Hard-block a base-name collision via
   `IProfileService.GetBaseNameCollision`. Two mods with the same base
   folder name cannot coexist in one profile (the loader cannot tell them
   apart). The would-be container is excluded (a re-add of a mod already in
   the profile is not a collision). On a hit, the import is refused and the
   batch aborts; nothing is created.
3. `IModImportService.Import` (extract or copy into the repository), then
   `IProfileService.AddMod` (the profile reference). The modal's chosen
   policy drives the new entry: `LatestPolicy` (the default) tracks the
   container's newest release; `PinnedPolicy` freezes the entry to the
   version being imported (constructed from the version id the import just
   minted).

A cancelled modal, a failed peek or import, or a collision cancels the whole
remaining batch. Mods imported earlier in the batch stay imported.

The mod name is derived from each path (folder name or archive stem) and
pre-filled in the modal; the user may rename at import (the edited name
becomes the container's display name and the untracked dedup key).

### The link flow (`LinkMods`)

The "Link external folder" flyout adds an external mod directory to the
active profile **without copying it** (the folder is the user's; Curator
controls only load order and enabled/disabled). No import modal; the folder
picker hands the path straight to `LinkMods`, which processes each path
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
enabled, order, policy, and the per-row policy-edit state. The row never
talks to `IProfileService` directly; the parent owns every service call, and
the view routes row interactions (toggle, move, policy, remove, update)
through code-behind handlers calling the parent's commands with the row as
the `CommandParameter`. This per-row code-behind pattern keeps each row a
passive state holder while the parent owns the service boundary.

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
Nexus Integrations (or the navigation failing) can never cause Welcome to
repeat. On a "Set up Nexus" choice it navigates the shell to Nexus Integrations
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
later via the normal add flow.

### Why the prompt is owned by ProfilesViewModel

The trigger signal (`IProfileService.ProfileCreated`) fires synchronously from
inside `ProfilesViewModel.SaveAsync`. The coordinator subscribes at construction
(resolved eagerly by the `ProfilesViewModel` DI factory, so the subscription
exists before any profile can be created) and records the signal as pending;
`ProfilesViewModel` awaits `ProcessPendingAsync` immediately after the create +
activation, so the DMF prompt runs as the topmost modal with no intervening
destination. `ProcessPendingAsync` snapshots and clears the pending trigger
before processing it, so an exception in the prompt does not leave it stuck
pending for the next call. The prompt is wrapped in a try/catch that logs and
swallows non-cancellation exceptions, so a wiring failure never blocks the
return to the Profiles destination.

## Dialogs, preferences, and i18n

### `IDialogService`

The testable true-modal seam. View models depend on this interface, not on
Avalonia `Window` construction, so their logic stays unit-testable: a test
injects a recording fake instead of a real window. The production
`DialogService` owns every real `Window` and `ShowDialog` wiring. Hosted
destinations (Profiles, Mods, Nexus Integrations, Preferences, Settings) are
not modals and live entirely on the shell's SplitView content region.

```csharp
public interface IDialogService
{
    Task<WelcomeChoice> ShowWelcomeAsync();
    Task<bool> ConfirmAsync(string title, string message);
    Task<ImportModResult?> ShowImportModAsync(ImportModRequest request);
    Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields);
    Task ShowAlertAsync(string title, string message);
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
```

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
  orchestrator the Nexus Integrations destination drives, and the
  `AuthStateChanged` event the DMF prompt coordinator subscribes to.
- [Nexus API rate limiting](nexus-rate-limiting.md): how the update check's
  rate-limit signal becomes the mod-list "check incomplete" notice.
- [App auto-update architecture](app-auto-update.md): Curator's own
  self-update in Velopack-packaged builds behind `IAppUpdateService`, surfaced in the
  shell status-strip pill and the Settings "Updates" section.
