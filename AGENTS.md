# AGENTS.md -- Modificus Curator

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans -- the human-facing entry point is `README.md`.

## What this is

**Modificus Curator** is the mod manager for Warhammer 40,000:
Darktide (.NET 10 + Avalonia 12). The app is user-usable. It launches the game
modded via
[Mod Relay](https://github.com/ModifAmorphic/darktide-mod-relay) (DLL
injection: no patched game files, no copies, no bundle-database patching; the
one game-dir footprint is a self-identifying, opt-in mods link at
`<game>/mods` pointing at the active profile's staged tree, so mods that
resolve game-directory-relative paths work -- a foreign entry at that slot is
never claimed or deleted; the runtime
is a separate repo) and stays out of the way for vanilla play (launch from Steam
= unmodified game). See `docs/architecture/` for the architecture.

## Baseline (read before planning)

The POC (on the `poc` branch) is a capability proof and reference, **not** a
pre-release of production code. Production is built ground-up with
testability, review, and production-readiness as first-class goals. The POC
carries forward proof of feasibility only; it does not carry forward code.
Requirements, architecture, and technology choices are made fresh. (Runtime +
game-binary constraints now live with the runtime, in
[darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay).)

## Repository state

- **`main`** -- production. Modificus Curator includes the SplitView app shell
  (a compact-inline navigation rail with five hosted destinations: Profiles,
  Mods, Nexus, Preferences, Settings) + profile management, global
  Preferences + i18n, the mod-list UI + local import, the
  Launch flow + discovery escape-hatch, the nxm:// scheme
  handler, Nexus auth + Integrations destination, mod acquisition, the
  serial nxm download queue + its mod-list download rows, the update-check
  service, the mod-list update UI, the DMF new-profile install prompt, the
  first-run Welcome onboarding, and in-app self-update for the Windows
  installer plus Linux AppImage (Velopack).
  The app is user-usable:
  create profiles, import mods (folder/archive, Nexus/Untracked) or link an
  external mod folder without copying it, manage
  the mod list (enable/disable/reorder/policy/remove), configure Settings
  (discovery paths + mod-repo location), download Nexus mods ("Mod manager
  download" links, premium update installs, opt-in automatic updates, the DMF
  prompt) through one download queue that renders each download as a row in
  the mod list (in place on the target mod's row, or appended below the list
  for new mods; cancel/retry/dismiss inline; a head-file download tracks
  latest, an old-file download pins to that version), and launch modded
  Darktide. The mod
  list has a persisted Compact/Detailed row density (Detailed is the default,
  with a Nexus summary and a cached thumbnail per row; Compact is the one-line
  variant, surviving only when persisted or selected, and
  absent/unknown normalizes to Detailed). Every
  Nexus Latest row shows a stable update-action button (disabled + neutral when
  no update and not version-unknown, enabled + accent when flagged OR
  version-unknown); a Premium click enqueues an
  in-app install that renders as the row's download morph (for a version-unknown
  row, the resolution install carrying an empty ExpectedVersion that the
  dequeue-time revalidation matches against the empty installed tag),
   a regular/unknown click opens the mod's Nexus files page. Every row also
   carries an edit-import-details action (a drawn-pencil button first in the
   shared action strip, between the source badge and the Enabled checkbox,
   shown only while the row is editable + hidden otherwise with its layout
   slot preserved so the strip geometry never shifts: linked,
   download-morphed, and downloaded rows (any version carries a FileId or a
   RemoteUploadedAt), the update-action-cell pattern) starting the import
   card's edit mode, the universal correction surface for a container's
   name, source association, and release tag, applied through the repository's
   EditImportDetails primitive and reloaded on save. Premium users can
  additionally opt into automatic flagged-update installation after each check
  (each flagged mod is enqueued onto the same download queue; version-unknown
  rows are excluded from the batch, manual click only).
  The first app startup shows a one-time Welcome modal introducing Curator and
  offering to set up Nexus. Whenever a new profile is created + set active
  without DMF (Darktide Mod Framework, Nexus mod 8) in it, a modal prompt
  offers to add/download it. The Launcher is a
  stub. Backend libraries: Profiles,
  Mods (the unified mod repository), Steam, Integrations, Relay-client,
  General. Mod Relay is a separate repo
  ([darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay));
  this repo holds Modificus Curator only.
- **`poc`** -- historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
src/        Modificus Curator -- the mod manager app (.NET 10 + Avalonia 12)
  modificus-curator.sln   solution root (classic .sln)
  Directory.Build.props  shared MSBuild props (net10.0, nullable, implicit usings)
  ui/                   Modificus.Curator.UI -- the Avalonia executable + DI composition root
                          (the SplitView shell: a CompactInline pane, 48px compact
                          rail, open pane starts at 200px and grows to fit the
                          widest localized nav label at the current font scale
                          (clamped to [200, 360], ellipsizing only beyond the
                          cap, full text retained in the tooltip/auto-name),
                          pane starts collapsed; five hosted
                          destinations in nav-rail order: Profiles, Mods,
                          NexusIntegrations (user-facing name "Nexus"),
                          Preferences, Settings (the
                          `ShellDestination` enum), Mods selected initially,
                          hamburger toggles `IsPaneOpen`; the content area holds
                          one persistent `UserControl` per destination,
                          visibility-switched by `Is*Visible` projections; a
                          global header shows the current destination title +
                          Launch Darktide (a branded iron-and-rust primary action
                          via `Button.launchAction`: dark gunmetal face, off-white
                          Quantico Bold display text from the embedded
                          `Assets/fonts/Quantico-Bold.ttf` (SIL OFL 1.1),
                          rust lower edge, a drawn play-arrow icon, the uppercase
                          `Launch_ButtonDisplay` visible label while the
                          accessible name + tooltip stay `Launch_Button`,
                          explicit normal/hover/pressed/disabled states, a Curator
                          cyan `:focus-visible` border at the same thickness so
                          focus adds no layout shift, MinHeight 44 DIP); the
                          status strip carries the running /
                          pending / nxm-handler / app-update indicators; the
                          window content is a root Grid hosting the shell
                          SplitView plus a full-client launch overlay as its
                          final top child: while
                          `ShellViewModel.IsLaunchAttemptInProgress` is true
                          the SplitView disables (IsEnabled bound to the
                          inverse state) and a hit-testable scrim panel
                          (ZIndex-top, semi-opaque `CuratorLaunchOverlayScrimBrush`,
                          theme-dependent opacity) blocks pointer input over
                          the whole client area, with a centered iron-and-rust
                          progress card (localized Launch_OverlayTitle /
                          Launch_OverlayMessage + an ordinary indeterminate
                          ProgressBar on app-owned `CuratorLaunchOverlay*`
                          brushes, declarative AutomationProperties names +
                          a polite live setting, no Cancel control, native
                          window chrome untouched, failure dialogs above it
                          as OS-owned windows; layout-stable sibling layer,
                          visibility bound straight to the attempt state);
                          the
                          pane's `Auto,*,Auto` grid anchors a drawn-icon Exit
                          button at the bottom (row 2, not a destination, no
                          selected state, Click -> `MainWindow.Close()` matching
                          the title-bar close). `MainWindow` keeps a public
                          parameterless constructor (the Avalonia XAML
                          runtime/designer loader path; loads XAML + safe
                          in-memory defaults, no store, AVLN3001 clean) and an
                          internal production constructor that supplies
                          `IMainWindowStatePersistence` (resolved via an explicit singleton
                          factory in CuratorComposition, no service locator). It
                          persists its last unmaximized client size + whether the
                          last meaningful state was maximized under
                          `IMainWindowStatePersistence.MainWindowState` (the
                          geometry state machine lives on the plain
                          headless-testable `WindowGeometryTracker` (ui/Views/,
                          fed ObserveResize(Size, ResizeReason) +
                          ObserveWindowState + NotifyOpened through an
                          injectable post seam, queried for the seed, the
                          close snapshot, + the CorrectionRequested reapply):
                          validated + clamped
                          to the XAML minimums + the primary work area in DIP,
                          applied before first Show, maximized on first open
                          when flagged, tracked through deferred coalesced
                          reason-aware resize observation where
                          `WindowResizeReason.Layout` is never authoritative for
                          the persisted size + the meaningful-state policy that
                          ignores Minimized/FullScreen, written once through the
                          close path, no window position; a narrow post-open
                          correction works around Avalonia #19431 where a
                          Maximized->Normal transition emits a stale maximized
                          Layout resize after the correct Unspecified one).
                          `ShellViewModel` owns navigation (guarded
                          `CurrentDestination`, the `NavigateCommand` taking the
                          destination as its parameter, `NavigateAsync` lifecycle:
                          same-destination is a strict no-op; a real change runs
                          the current destination's leave effects first (leaving
                          Profiles awaits the unsaved-changes three-choice
                          guard, Cancel/Save-failure keeps the destination
                           unchanged; leaving Nexus
                           calls `IntegrationsViewModel.Deactivate` which cancels
                           the in-flight auth + the shell reloads the mod list
                           (no nxm probe on leave; the registration state
                           refreshes at Nexus ENTER, its deliberate probe
                           point); leaving Settings reloads the mod
                           list + re-reads the startup-check toggle + refreshes the
                           app-update notice), then switches the destination, then
                          runs the target's enter effects (Settings calls
                          `SettingsViewModel.RefreshFromConfig` synchronously;
                          Nexus awaits `IntegrationsViewModel.RefreshAsync`
                          so the page paints then resolves auth state), then
                          drains the shell-owned modal queue for the entered
                          destination), the
                          global Launch (resolves the active id from
                          `IProfileSession.ActiveProfileId` at execution time, not a
                          cached selection; sets the shell-owned
                          `IsLaunchAttemptInProgress` before anything else +
                          yields once to the Avalonia dispatcher at Loaded
                          priority so the freshly-disabled button paints, then
                          runs the synchronous launch on the UI thread;
                            branches on `LaunchResult.Status`, keeping the
                            attempt state through failure-dialog handling
                            (incl. the GameDirConflict consent modal + its
                            one-shot retry); after
                            `Launched` + the eager refresh the attempt state
                           stays set until BOTH the session's running-state
                           signal observes Darktide AND the spawned Relay
                           process exits (the exit task carried on the result:
                           Relay directly on Windows, the Proton wrapper on
                           Linux, whose exit follows Relay's under proton run;
                           Darktide's process appears before Relay finishes
                           injecting, so the overlay must outlive the detector
                           signal), or a 30-second timeout elapses releasing
                           the whole combined wait (the UI still holds no
                           process handle; the façade observes + disposes the
                           spawn; the state clears in all completion/exception
                           paths)), and
                          the global status strip (running + pending + nxm-handler
                          + app-update notice; the nxm indicator mirrors the
                          shared `INxmRegistrationState`, seeded by its one
                          startup probe + updated on each publish). The hosted page VMs are
                          application-lifetime singletons; navigation never calls an
                          old Window-close Detach path. The active profile is owned
                          by `IProfileSession`; launch availability derives directly
                          from `ActiveProfileId` + `IsGameRunning` +
                          `IsLaunchAttemptInProgress`;
                          the Profiles destination (`ProfilesViewModel` +
                          `ProfilesView`): edits the active profile only (name +
                          120-char description + inline launch settings via the
                          reusable `LaunchSettingsEditorView`/`LaunchSettingsEditorViewModel`);
                          a flat banner button styled as a profile card hosts an
                          Avalonia Flyout listing every persisted profile (first-
                          letter box + name + description); selecting asks
                          `IProfileSession.RequestActive` (disabled while Darktide
                          runs) + reloads from the authoritative active id; a
                          staged draft is edited in place (the persisted profile is
                          not mutated until Save); Add Profile starts a blank draft;
                          Save existing calls the atomic `UpdateProfile`, Save new
                          calls `CreateProfile(name, description, launchSettings)` +
                          requests it active; Save is disabled while metadata or
                          launch-settings validation fails (inline localized
                          reasons); Cancel reloads the persisted active profile;
                          navigating away / switching / starting another draft while
                          dirty asks the unsaved-changes three-choice prompt
                          (Cancel/ESC/X preserves the draft; Save tries the same
                          atomic write as the Save button and proceeds only on
                          success; Don't save reloads authority and proceeds);
                          running-state gates
                          disable switching/Add/Delete while Darktide runs (active-
                          profile metadata + launch-settings edits stay enabled);
                          a draft hides the shared Add/Delete action row AND
                          disables Add at the command level (defense in depth so
                          a programmatic call cannot start a second draft);
                          after a successful create + activation,
                          `ProfilesViewModel` does NO DMF or mod-list work;
                          the DMF coordinator enqueues its prompt onto the
                          shell-owned modal queue, which the shell drains on the
                          next real navigation into Mods (after
                          `CurrentDestination = Mods` + the enter effects) so an
                          accepted DMF install shows immediately (the
                          coordinator's drained delegate reloads the list
                          itself); the avatar palette is deterministic from the
                          profile Guid so a profile keeps its color across reloads,
                          sorting, and app restarts;
                           the Mods destination (`ModListViewModel` + `ModListView`):
                           the active profile's mod list (the dominant content area),
                            with its own toolbar (refresh, rate-limit notice, the
                            search box, the Compact/Detailed density selector with the
                            hide-disabled + updates-only filter toggles, the Add split
                            button) shown
                            only on Mods, and the
                           inline import card (`ImportWorkflowViewModel` +
                           `ImportWorkflowView`, an application-lifetime singleton
                           child VM registered before `ModListViewModel`) directly
                           below the toolbar: the card owns two exclusive modes
                           over one editing form, and the two modes render in
                           two places. The batch mode (the top card below the
                           toolbar, gated by `IsBatchActive`) owns the state
                           machine (editing, processing, terminal failure), the
                           per-item editing form (name + source + conditional
                           Nexus version/URL/policy + live validation), and the
                           per-item import orchestration (only `GetBaseName` +
                           `Import` run on `Task.Run`; `FindExistingContainer` +
                           the collision check + `AddMod` run on the captured UI
                           context). The edit mode (started by a row's pencil
                           button through `StartEdit`) reuses the same fields
                           as the per-container correction surface but renders
                           as an IN-ROW BAND: a leading section inside the
                           edited row's template (the row list's items cannot
                           host injected elements between items, so the band
                           lives in the row markup; one shared definition
                           leading both density roots). The parent tracks the
                           edit target by CONTAINER ID (`EditTargetContainerId`
                           through the shared child subscription, the
                           IsListToolingEnabled propagation shape) + assigns
                           each row's `IsEditTarget` + band context (the
                           ActiveDownload morph pattern, so the form
                           instantiates only on the editing row) on activation
                           + on every Reload: a mid-edit reload re-attaches the
                           band to the rebuilt row instance. Opening the band
                           brings its row into view (posted at Loaded
                           priority). While a row's band is open the row is
                           ANCHORED like a locked row (grip not hit-testable +
                           move commands refuse; the enabled toggle, policy,
                           lock, and remove stay live), and a download morph
                           arriving on the edited container CLOSES the edit
                           automatically (the container became downloaded =
                           not editable; the morph is the visible
                           explanation). The Add split button +
                           drag-and-drop forward paths to the workflow's
                           `StartBatchCommand`; while the workflow is active
                           (either mode) the Add button disables and drops are
                           rejected, and the toolbar's projection-touching
                           controls (the search box, the hide-disabled +
                           updates-only filter toggles, the density selector,
                           the check-now refresh; `IsListToolingEnabled`) also
                           disable so no filter change can hide the row being
                           edited under its open band (row-level controls
                           stay live); the batch, the edit, + the load-order
                           card are mutually exclusive through the shared
                           `ModCardsGate` (each card VM reports its activity +
                           refuses to start while any other card is open; the
                           gate is also the one any-card source behind
                           `IsAddEnabled`/`IsListToolingEnabled` + the view's
                           picker/drop guards). The load-order import card
                           (`LoadOrderImportViewModel` +
                           `LoadOrderImportView`, the child-VM pattern, always
                           the top-below-toolbar card, never an in-row band)
                           sits directly below the import card: the fifth Add
                           mode's txt picker feeds `StartImport(path)`, which
                           reads + parses the file (`ModLoadOrderParser`),
                           reconciles it against the active profile + repo
                           (`ILoadOrderReconciler`), + opens the review table:
                           one compact ordinary-controls row per file line
                           (folder name | match | localized outcome "will be
                           reordered"/"can be added"/"not found" | include
                           checkbox with reorder-default-checked, add-default-
                           unchecked, unresolved disabled+unchecked | reserved
                           mod-id/version columns for the resolver tiers,
                           fixed widths in the shared header+row layout so
                           activating them never reshuffles the table |
                           open-on-Nexus link on unresolved rows, the folder
                           name as the search keyword, IExternalLauncher with
                           the fallback alert). Unmatched names are fully
                           visible, never dropped. The resolver tier: after
                           the repo tier resolves what it can, a SERIAL
                           human-paced search queue (one row at a time, table
                           order, no retries; failures are logged + leave the
                           row unresolved) fires the anonymous
                           SearchModsAsync with the folder name normalized
                           into search terms (lowercase, underscores/hyphens
                           to spaces, whitespace collapsed), + each
                           unresolved row gets an identification workspace:
                           the TOP candidate inline (name + mod id + a
                           one-click Accept), an expand affordance revealing
                           the alternates (each with its own accept), + the
                           manual id/URL entry in the reserved cells (the
                           shared ImportSourceValidator parse; a bare id or a
                           nexusmods.com URL both accepted). Accepted or
                           manually entered identification marks the row
                           identified (the id cell shows the fact + the
                           version cell activates, empty by default,
                           validated non-empty-when-Nexus like the import
                           form; the rung-4 apply decides what the version
                           means per path). Identification never checks the
                           include checkbox (the identified default stays
                           excluded; identification is a correction, not
                           consent). Cancel stops the queue; arrived
                           candidates stay on their rows. Apply (enabled when >= 1
                           line is included; an empty/comment-only file shows
                           the localized notice + refuses) performs ONE
                           SetModOrder over every matched container in file
                           order (included or not; the checkboxes gate only
                           adds) + AddMod(LatestPolicy) for each included
                           library add in file order, marks pending, raises
                           `OrderApplied` (the parent reloads), + deactivates
                           (runApply sequencing: SetModOrder first, adds
                           append; positioning refinement lands with the
                           resolver tiers). Cancel + a profile switch reset
                           with no writes. Copied
                           local-import failures surface inline (not via modal
                           alert); the linked-folder flow keeps its modal alerts.
                           The toolbar's density selector is two drawn-icon buttons
                          (view_headline for Compact, view_agenda for Detailed)
                          bound to `DetailedModRowsViewModel.SetDensityCommand`;
                           the active one carries the `selected` class (the shell's
                           conditional-class pattern, not a ToggleButton). Detailed is
                           the default; absent/unknown normalizes to Detailed, and
                           Compact survives only when persisted or selected.
                            The toolbar also carries the view-projection controls: a
                            fixed-width search box (keystroke-live TwoWay
                            `SearchText`, case-insensitive ordinal substring on the
                            row name, with an inner clear button built from the
                            Fluent theme's own text-box clear-button chrome), a
                            hide-disabled visibility toggle (drawn
                            visibility/visibility_off paths, `selected` while
                            hiding), and an updates-only toggle (one stable drawn
                            Material update glyph + `selected` while filtering,
                            keeping only rows flagged `UpdateAvailable`). All
                            compose with AND and drive the VM's `VisibleMods`
                            projection
                            of the authoritative `Mods` list (rebuilt by one
                            `RebuildVisibleMods` at the end of every Reload (after
                            the known-update-flag hydration, so the updates-only
                            filter sees hydrated flags), on
                            every filter/search change, and after an enable
                            toggle under an active filter; a landed check also
                            reprojects, since it can change the flags); the state is
                            session-transient (never persisted, survives reloads
                            + navigation, cleared on an active-profile change).
                           `DetailedModRowsViewModel.SetRowsAsync` keeps
                           receiving the FULL snapshot, so thumbnails + metadata
                           hydrate regardless of visibility and a filter change
                           never re-triggers hydration. An active profile with a
                           non-empty full list but an empty projection shows the
                           localized no-matches message, exclusive with the
                           no-mods/add-hints empty state (the hints gate on
                           `!IsFilterOrSearchActive` too).
                           Detailed renders a rounded card per row laid
                          out as one adaptive Grid (the card root carries
                          `Container.Name="detailedModRow"` +
                          `Container.Sizing="Width"`, so a `ContainerQuery
                          max-width:680` in `UserControl.Styles` swaps the layout
                          at the 680-DIP card-width breakpoint): column 0 is the
                          drag-reorder grip, column 1 is the
                          thumbnail/placeholder slot, column 2 holds the name +
                          source badge (row 0) + a two-line plain-text summary
                          (row 1, `MaxLines=2`, `Wrap`, `CharacterEllipsis`; the
                          full text is retained in the tooltip when non-null, and
                          the automation name always carries the displayed
                          summary/fallback), and row 2
                          is a single `WrapPanel` action strip. Wide (card width
                          greater than 680 DIP): a 112-DIP rounded `UniformToFill`
                          thumbnail spans all three rows (column 1, `RowSpan=3`)
                          and the action strip occupies only the content column.
                          Constrained (card width at or below 680 DIP): the
                          thumbnail shrinks to 72 DIP spanning only name +
                          summary (`RowSpan=2`) and the same action strip moves to
                          a full-width row beneath all three columns
                          (`Grid.ColumnSpan=3`, via the
                          `ContentControl.detailedActions` styles). Width,
                          height, row span, action
                          column, and action column span are driven by styles
                          (default wide styles + the container-query overrides),
                          not local values, so the breakpoint can change them;
                          the placeholder geometry scales with the slot (36 DIP
                          wide, 28 DIP constrained) through the same styles. The
                          action strip right-aligns every wrapped line
                          (`WrapPanel.ItemsAlignment=End`) and wraps at the edge
                          in both states (no horizontal scrolling). The drag
                          grip, the badge cluster, and the action strip are ONE
                          shared definition each (DataTemplate resources in
                          ModListView hosted by both row roots through
                          `ContentControl.ContentTemplate`; the page styles +
                          container query reach the realized template content,
                          and the handlers resolve against the page code-behind
                          unchanged), so no behavior can fork between modes; the
                          Compact row keeps its single-line spacing through
                          `Grid.compactRow`-scoped styles and the Enabled label
                          is the row's density-aware `EnabledLabel` (null in
                          Compact). `DetailedModRowsViewModel`
                          (ui/ViewModels/, an application-lifetime singleton child VM
                          registered before `ModListViewModel`, analogous to
                          `ImportWorkflowViewModel`) owns the persisted density
                          selection, the metadata-backfill invocation, and the
                          thumbnail-hydration lifecycle. It reads + writes
                          `CuratorConfig.Preferences.ModRowDensity` through its own
                          focused read-modify-save (not `IPreferencesService.ApplyAndPersist`),
                          so it does not widen that method. `ModListViewModel.Reload`
                          joins each row's `ModDisplayMetadata` from the container +
                          hands the final row snapshot to the child via
                          `SetRowsAsync` (fire-and-forget; the task absorbs every
                          failure). A generation counter cancels the prior
                          generation on every new snapshot: thumbnail + metadata
                          results are applied only when the generation is still
                          current, the mode is still Detailed, and the exact row is
                          still in the snapshot with the same `ThumbnailUrl`, so a
                          profile switch, a Compact toggle, or a superseding reload
                          prevents stale assignment without aborting the thumbnail
                          service's shared cache load. In Detailed mode the child
                          starts known-thumbnail hydration for eligible rows
                          (Detailed + Nexus + non-null metadata + not adult + a
                          non-empty `ThumbnailUrl`) + invokes
                          `INexusModMetadataService.BackfillMissingAsync` with the
                          current row container ids as priority; a backfilled row's
                          metadata is re-read from the repository as authoritative
                          before it is applied, then its thumbnail is hydrated. An
                          adult-content flag is only a persisted boolean; the child
                          skips the thumbnail for it (the row shows the ordinary
                          placeholder), and no badge, filter, warning, or setting
                          hangs off it. `IModThumbnailService`
                          (ui/, a UI-layer singleton) is the one focused UI-owned
                          presentation-media service: it returns an Avalonia
                          `IImage`, accepts HTTPS URLs only, keys cached bytes by
                          the lowercase SHA-256 of the URL under
                          `AppPaths.ModThumbnailCacheDir` (`<app-data>/cache/mod-thumbnails`),
                          caps a download at 8 MiB, writes via an atomic sibling-temp
                          move, bounds distinct concurrent loads to four, coalesces
                          same-URL loads into one shared uncancellable task
                          (per-caller cancellation via `WaitAsync(ct)`), retries a
                          corrupt disk entry exactly once, decodes successful loads
                          into an app-lifetime in-memory image cache, and prunes
                          cache files older than 90 days best-effort.
                          The mod-row reorder surface: a dedicated drag grip at
                          the left edge of every Compact + Detailed row (a 32-DIP
                          transparent, hit-testable Border with a drawn Material
                          drag-handle Path) is the only place a pointer gesture
                          may initiate row reordering; dragging anywhere else in
                          a row stays ordinary touch scrolling (important on the
                          Steam Deck touch list). A press on an unlocked grip
                          calls `PreventGestureRecognition`, marks handled, and
                          captures the pointer to the grip; a reorder starts only
                          after an 8-DIP movement threshold (a tap is inert).
                           While dragging, the target rank is computed among the
                           other VISIBLE unlocked rows only (locked rows are
                           never destinations; filter-hidden rows are not
                           realized so they cannot be destinations either; an
                           unlocked row may cross locks and hidden rows), a 2-DIP
                           accent insertion line renders before/after the target
                           row (non-hit-testable), the realized item container (the
                           full-width actual row) is lifted via a `RenderTransform`
                           `TranslateTransform` + `ZIndex` so it follows the pointer
                           while its layout slot stays reserved (rows do not jump),
                           and a `DispatcherTimer` edge-band auto-scrolls the
                           ScrollViewer, keeping the lifted row under the pointer +
                           recomputing the target/marker per step. Every mutated
                           container property is restored from a snapshot on each
                           finish/cancel path (before VM Reload on a valid drop).
                           A release inside the viewport recomputes the target
                           from the final release position (closing the one-tick
                           auto-scroll/layout lag), releases capture, then commits
                           through
                           `ModListViewModel.CommitReorderCommand` (one immutable
                           `ReorderRequest` of source ContainerId + target rank
                           among the visible unlocked OTHER rows; the pure
                           `ModReorderPlanner` builds the legal full order by
                           remove+insert within the non-locked stream, anchored to
                           visible-unlocked rows, so locked rows keep their exact
                           indices, hidden rows never anchor, shift at most one
                           slot, and keep their relative order, and an all-visible
                           input reproduces the pure lock projection; the planner
                           rejects same-order / out-of-range / locked-source /
                           hidden-source / missing-source requests without a
                           service call, so a no-op persists nothing). Escape,
                           `PointerCaptureLost`,
                           view detachment, a release outside the viewport, or an
                           invalid target all cancel without persistence + restore
                           the lifted container. Capture
                           is released before the VM command runs because Reload
                           rebuilds row containers. The gesture is single-pointer:
                           a second grip press while a row gesture is armed is
                           ignored before it can claim the gesture, and Move /
                           Release / CaptureLost process only the active captured
                           pointer (by reference), so a simultaneous second
                           pointer cannot move, commit, cancel, or release the
                           active gesture. The gesture is custom pointer
                           handling, structurally separate from the outer Grid's
                           native external file/folder `DragDrop.DoDragDropAsync`
                           handlers (which are unchanged); native drag is rejected
                           for reorder because Avalonia 12.1 X11 lacks Escape
                           cancel and its platform modal loops make the touch
                           threshold/marker/auto-scroll less dependable. Move Up /
                           Move Down move an unlocked row one VISIBLE unlocked
                           rank, crossing locked + hidden rows (`CanMoveUp` /
                           `CanMoveDown` follow visible unlocked neighbors, so a
                           row with only hidden or locked rows above it cannot
                           move up); the lock toggle
                          (`ToggleOrderLockCommand` -> `SetModOrderLocked`)
                          flips lock metadata only and does NOT set
                          `HasPendingChanges` (lock metadata alone does not
                          change the staged mod tree or `mods.lst`). A locked
                          row's grip has `IsHitTestVisible=False` (bound to
                          `IsGripEnabled`) so its area falls through to touch
                          scrolling; both move buttons disable for a locked row;
                          the lock button's locked state reads through BOTH shape
                          (closed vs. open padlock) and color (a caution-yellow
                          `CuratorCautionBackgroundBrush` fill + caution-yellow
                          closed-padlock foreground, preserved on hover) + a
                          dynamic localized lock/unlock tooltip/automation name.
                          Both row roots expose the same
                          grip/lock/move behavior and route to the same
                          handlers/commands. `OrderLocked` is threaded from each
                          `ModListEntry` through `Reload` into `ModItemViewModel`;
                          the pure gesture math (`ReorderGestureMath`: threshold,
                          target rank, marker direction, lift translation, edge
                          auto-scroll + clamp) is unit-tested separately. No
                          `ConfigureAwait(false)` anywhere in the gesture path.
                          the Nexus destination
                          (`IntegrationsViewModel` + `IntegrationsView`,
                          Nexus-only): OAuth + developer-gated API-key + nxm handler
                          registration + automatic-update setting; the OAuth block
                          is a single dual-state button ("Sign in to Nexus" when not
                          signed in via OAuth vs "Clear Nexus sign-in" when signed
                          in via OAuth, so there is no re-login-over-existing); the
                          API-key block is gated behind the `ApiKeyAuthEnabled`
                          developer config flag, default off, so OAuth is the sole
                          sign-in path unless a developer opts in; auth controls stay
                          usable while Darktide runs (only launch + active-profile
                          changes are blocked); the destination also owns the
                          explicit `nxm://` handler registration (a "Nexus download
                          links" section over `INxmHandlerRegistrar` for the
                          mutations + the shared `INxmRegistrationState` for the
                          status: register confirms first since it is a system-wide
                          change that can affect other mod managers; unregister
                          delegates straight to the self-guarded registrar, which
                          releases only Curator's own registration; after either
                          action one refresh publishes the state to every
                          consumer); entering the destination
                          refreshes auth state (one registration probe per
                          enter), leaving cancels in-flight auth via
                          `Deactivate`;
                           the Preferences destination (`PreferencesViewModel` +
                           `PreferencesView`): theme + font scale + language + the
                           show-Relay-console toggle (hidden by default; Windows-only,
                           shown checked + disabled on Linux as a display-only
                           reflection of the console that always shows under Proton
                           until a Relay-side GUI-subsystem fix) + the experimental
                           external-mod-hosting toggle (labeled "Load mods from
                           Curator's profile directory (experimental)" with the
                           hint "May experience issues with mods that require
                           absolute paths"; the preference is set only here,
                           never from the conflict flow; persisted through its
                           own focused read-modify-save + read live per launch)
                           via
                           `IPreferencesService` + the i18n infrastructure
                          (`Strings.resx` + `LocalizationService` for dynamic
                          culture switching; localized VMs derive from the
                          small `LocalizedViewModel` base (ui/ViewModels/)
                          which re-fires each VM's registered localized
                          property names on a culture change, with a
                          source-scan test failing when a localized property
                          getter is not registered; each change applies +
                          persists immediately;
                          the theme mapping honors Gaming Mode: `ThemeMode.System`
                          applies Dark as the effective runtime theme while gaming
                          (the Gaming Mode session reports no desktop appearance
                          preference; the pure `ResolveThemeVariant` mapping is the
                          policy seam) + the stored preference stays `System`, while
                          explicit Light/Dark stay authoritative everywhere;
                          the Settings destination (`SettingsViewModel` +
                          `SettingsView`): discovery write-through over the shared
                          `Settings/DiscoveryField` descriptor + the global
                          `OverrideAutomaticDiscovery` mode + Discover button
                          (automatic mode: rows read-only + Browse-disabled, the
                          discoverer owns the snapshot; manual mode: rows
                          editable + Browse-enabled, stored paths validated as-is;
                          turning override off persists false + runs an ordinary
                          `ISteamService.Discover` (automatic) + refreshes the
                          rows; turning it on persists true + enables editing;
                          the Discover button forces `ISteamService.Rediscover`
                          in either mode, replacing the snapshot without changing
                          the mode; a manual-mode row edit writes through
                          immediately via a read-modify-save; the Browse buttons
                          seed the picker at the row's current value via
                          `SuggestedStartLocation`; the Storage section has two
                          buttons that open the OS file manager at the Curator
                          data root + profiles root paths; in Gaming Mode the
                          Browse + open-folder buttons disable (Desktop Mode
                          tooltip via `ToolTip.ShowOnDisabled` + an inline
                          per-section hint; row `IsBrowseEnabled` + command +
                          code-behind guards launch nothing) while manual
                          discovery-path entry + submission stay available) +
                          the app-update "Updates" section (current version + Check
                          for Updates + startup-check toggle + inline result +
                          Download and Restart); `RefreshFromConfig` is the enter
                          operation (rehydrates discovery rows + the startup-check
                          toggle from the live config so escape-hatch changes are
                          visible on a later visit); leaving Settings reloads the mod
                          list + re-reads the startup-check toggle + refreshes the
                          app-update notice;
                           `IDialogService` is narrowed to true modals only (the
                           seven methods: `ShowWelcomeAsync`, `ConfirmAsync`,
                           `ShowDiscoveryEscapeHatchAsync`, `ShowAlertAsync`,
                           `ShowUnsavedChangesAsync`,
                           `ShowGameDirConflictAsync`, `ShowProgressAsync<T>`;
                           the escape-hatch dialog VM is built by the narrow
                           per-dialog `IDiscoveryEscapeHatchFactory`, so
                           DialogService carries no Steam/config/gaming
                           dependencies + constructs no view models);
                          hosted
                          destinations are not modals and never flow through it;
                          the inline import card is a hosted `UserControl`
                          (`ImportWorkflowViewModel`), not a modal;
                          `AddNxm()` + `StartNxmServer` (single-instance via
                          `SingleInstanceGuard` process enumeration, separate from the `Modificus.Curator.Nxm`
                          pipe bind which degrades gracefully on IOException; a second Curator exits
                          via `NxmSingleInstanceException` -> `Environment.Exit(1)` before the
                          window shows);
                           `IModAcquisitionService` (download + extract + place
                           orchestrator in Integrations, returning
                           `NexusAcquisitionResult` with per-file byte progress
                           + `ResolveLatestNexusAsync` for head-file resolution
                           without a download) + the serial `IModDownloadQueue`
                           (ui/Session/ModDownloadQueue: one worker, FIFO,
                           deduped by game domain + mod id + file id with
                           join+pulse; dequeue-time auth recheck +
                           `UpdateEligibility` revalidation + the exact-FileId
                           repository hit check (a hit registers with no
                           network); head file -> LatestPolicy, non-head ->
                           PinnedPolicy to the clicked version, applied via
                           SetModPolicy when the container is already in the
                           profile; ProfileAdd vs UpdateInstall completions;
                           token-authoritative cancel) + the real
                           `NxmModDownloadHandler` (in UI, the enqueue adapter
                           in front of the queue: gates each link (game domain,
                           auth, active profile; gate failures keep the
                           modal-alert path since there is no row to host
                           them), peeks the repository for a row name, enqueues
                           onto the queue, and returns within milliseconds so
                           the nxm IPC accept loop never blocks on a download;
                           the queue owns the acquisition, the profile
                           registration, the acknowledge, and the reload) that
                           replaces the no-op default via DI
                           last-registration-wins, registered after
                           AddNxm() in CuratorComposition;
                          the shared `INxmRegistrationState` (ui/Session/, an
                          application-lifetime singleton): the last-known OS
                          `nxm://` registration for every UI surface (shell
                          status strip, Mods empty-state hint, Nexus page, DMF
                          prompt wording). `RefreshFromOs` is its only writer
                          + the UI's only probe: one seed at shell
                          construction, one per Nexus enter, one after each
                          register/release action; each publishes `Changed`
                          (marshaled to the UI thread) so every surface
                          updates together. All other consumers read
                          last-known + accept staleness (the OS association is
                          racy by nature); `ModListViewModel.Reload()` and all
                          navigation-leave effects perform zero probes;
                          `DmfPromptService` reads the state + never probes;
                          only `IntegrationsViewModel` still injects the
                          registrar (for the register/release mutations);
                          the shared `IGamingModeState` (ui/Session/, an
                          application-lifetime singleton): the one source of
                          truth for whether Curator is running in a Steam Deck
                          Gaming Mode session. `GamingModeState` captures
                          `GamingModeDetector.IsGamingMode()` (steam lib, the
                          complete env signature) once at construction +
                          nothing else in the UI reads the environment; the
                          value is process-immutable. Consumers: the
                          Preferences theme mapping (System applies Dark while
                          gaming), the picker/file-manager gating (Add split
                          button, Settings + escape-hatch Browse, open-folder
                          buttons + the linked-row badge; disabled controls
                          with Desktop Mode tooltips via ShowOnDisabled +
                          inline hints + code-level guards), and the
                          browser-flow gating (Add Nexus Mods, regular/
                          unverified update action, non-Premium DMF prompt,
                          the Mods empty-state Nexus hint swaps to Desktop
                          Mode guidance);
                           `UpdateCheckRunner` (ui/Session/) the
                           UI-layer glue that fires `IUpdateCheckService.CheckAsync`
                           fire-and-forget on the three automatic triggers
                           (startup-with-restored-id + active-profile switch via
                           IProfileSession.PropertyChanged filtered to
                           ActiveProfileId + a periodic timer), all interval-gated
                           via a shared last-check persisted to
                           `IUpdateCheckScheduleState.LastUpdateCheckUtc` (so a close/reopen
                           loop does not fire a call per launch); owns the
                           candidate pull: each fire reads the profile's mod
                           list through IProfileService inside its thread-pool
                           task + maps the entries to `ModListCandidate`s at
                           the call site (one small internal UI extension), so
                           Integrations holds no Profiles reference + a pull
                           failure (a deleted/unreadable profile) is logged +
                           skipped without mutating LastResult; the runner owns
                           + exposes the `UpdateRefreshGate` (fed by every
                           captured check result; the mod-list VM renders its
                           state); the
                           `AutoUpdateCheckEnabled` toggle gates ONLY the periodic
                           timer, and the manual `CheckNowAsync` carries its own
                           sliding-window throttle (10 free/hour then 1/2min,
                           independent of the interval gate); registered + started
                           best-effort from CuratorComposition);
                           the mod-list update UI per-row update
                           signal + per-mod update action. `ModListViewModel`
                           subscribes to `UpdateCheckRunner.CheckCompleted`
                           (the runner re-raises the check completion on the
                           UI thread; the VM holds no update service; install
                           completions are not re-raised by the runner) + to
                           the download queue's `UpdatesApplied` (raised by
                           the queue itself after a successful UpdateInstall
                           completion; on it the VM flags HasPendingChanges +
                           reloads) and
                           reads the
                           profile-scoped `IUpdateStateStore` (persisted in
                           `IKnownUpdateState.KnownUpdates` / app-state.json, so a
                           restart inside the interval gate shows prior flags
                           before any API call) for per-row `UpdateAvailable`
                           (matched by ContainerId; the VM passes the entries
                           its last Reload loaded as the hydration candidates),
                           while the refresh-gate policy lives in the
                           runner-owned `UpdateRefreshGate` (ui/Session/): the
                           rate-limit tracking fed by every captured check
                           result, the effective-reset computation (server
                           reset governs, 1-minute fallback when silent), the
                           manual-throttle read, the shared 1-second countdown
                           timer lifecycle, + the IsRateLimitActive /
                           IsManualThrottled / IsRefreshEnabled decisions; the
                           VM keeps only the localized rendering (tooltip
                           priority rate-limit > throttle > normal,
                           BuildThrottleTooltip, FormatRemaining, IsCheckingNow)
                           driven by the gate's marshaled StateChanged. A
                           rate-limited check disables the refresh button until
                           the server-reported reset in `UpdateCheckResult.RateLimitResetsAt`
                           elapses (1-minute client-side fallback when Nexus
                           sent no reset), and the pill reads "Refresh disabled
                           due to rate-limiting" exactly while the button is
                           rate-limit-blocked, distinct from the client-side
                           manual fire-count throttle which remains. Downloads
                           render as rows in the mod list through one shared
                           status template + three hosts (the Compact morph
                           slot, the Detailed morph slot, the appended row): a
                           download whose container is referenced by the
                           active profile's current row set AND realized in
                           VisibleMods morphs that row in place
                           (`ModItemViewModel.ActiveDownload`, assigned
                           exclusively by the parent's re-derived hosting
                           projection; while morphed the summary area + action
                           strip swap to the download content, the policy
                           editor + update-action cell suppress, and the
                           structural controls stay functional), and everything
                           else (fresh mods, cross-profile targets,
                           filtered-hidden targets) appends below the list in
                           a dedicated ItemsControl labeled with the target
                           profile. The projection is structural: the
                           appended collection never intersects VisibleMods or
                           the reorder machinery, download rows can never enter
                           the reorder planner's inputs, the drag gesture's
                           container math, or the move commands. The two
                           row-affecting globals live on one shared observable
                           `ModRowContext` (ui/ViewModels/, created in
                           composition before the list VM, passed once to every
                           row): the one-shot construction-time premium read
                           (no mid-session refresh) + the constant gaming
                           flag; install-busy state is not a context member
                           (an update in flight is a queue item rendered as
                           the row morph). Rows keep their public names
                           as context-forwarding reads, + the list VM's single
                           context subscription fans change notifications into
                           the live rows (re-firing exactly the derived
                           properties the former per-flag pushes re-fired; no
                           per-row subscription, so rows dropped by a reload
                           cannot leak). The VM exposes an async
                           `UpdateCommand(row)` that branches on
                           premium: Premium resolves the head file + enqueues
                           one UpdateInstall item through `ModUpdateEnqueuer`
                           (ui/Session/, the enqueue front over
                           ResolveLatestNexusAsync + the queue shared by the
                           manual action + the automatic batch + the DMF
                           download; the queue's serial worker is the gate:
                           dequeue-time eligibility revalidation, acquire +
                           acknowledge-on-success, UpdatesApplied reload; a
                           resolve failure (no row yet) surfaces the localized
                           alert, a stale flag is a silent no-op, and the row
                           morph is the busy surface); regular/unknown opens the
                           mod's Nexus files page via the shared
                           IExternalLauncher (fallback alert on failure).
                           `CheckForUpdatesNowCommand` awaits the runner's
                           thorough check (driving an `IsCheckingNow` spinner on
                           the Mods toolbar refresh button; the await also covers the
                           chained automatic-update enqueue batch) and drives the manual
                           sliding-window throttle's countdown tooltip + disabled
                           button via the runner's `NextManualRefreshAllowedAt`,
                           sharing one countdown timer with the rate-limit gate so
                           either cause keeps the button disabled and the rate-limit
                           reason takes tooltip precedence when both are active).
                            The view's source badge
                            is a `HyperlinkButton` to the mod's remote page; the
                            stable update-action cell is a fixed-width `Panel`
                            reserved on every row holding a drawn download-arrow
                            button. The button shows for Nexus + Latest rows
                            regardless of tier (disabled + neutral when no update
                            and not version-unknown, enabled + accent-blue arrow
                            when flagged OR version-unknown); Pinned/
                            Untracked rows keep the reserved cell but no button;
                            a morphed row does not render the cell at all (the
                            morph's progress owns the row's progress surface +
                            hiding the button is the double-click guard).
                            "Nexus, version unknown" is a derived row state (a
                            NexusSource container whose resolved latest version
                            carries an empty VersionString; no storage): the
                            badge stays the plain "Nexus #id" (the empty
                            ActualVersion never appends a dangling separator),
                            the update action enables with the version-unknown
                            tooltip variants (gaming guidance still wins for
                            non-Premium rows), the Premium click enqueues the
                            existing UpdateInstall path with an EMPTY
                            ExpectedVersion (the queue's dequeue-time
                            UpdateEligibility revalidation matches empty vs
                            the empty installed tag), regular/unknown opens the
                            files page, the updates-only filter keeps unknown
                            rows, the pin dropdown is suppressed (nothing to
                            pin to; the Pinned choice is disabled), and the
                            automatic-update batch excludes them (manual click
                            only). Every row also carries an
                            edit-import-details action (a drawn-pencil button
                            FIRST in the shared action strip, between the
                            source badge cell and the Enabled checkbox, ONE
                            definition in the shared templates so Compact +
                            Detailed behave identically; the button shows
                            only while the row is editable + hides otherwise
                            inside its always-laid-out slot (the pencil's
                            footprint) so the strip geometry never shifts,
                            linked + download-morphed + downloaded rows, the
                            update-action-cell
                            pattern) starting the import card's EDIT MODE
                            (an in-row band on the edited row, a hosted view
                            rather than a modal): the per-container
                            correction surface for name, source association,
                            and release tag. The band activates in place
                            titled "Edit import details", prefilled from the
                            container (name, source choice, the bare mod id,
                            the latest version's tag); the policy picker
                            hides; the primary button reads Save and applies
                            the repository's EditImportDetails primitive with
                            the same validation the batch form enforces (the
                            shared ImportSourceValidator; a version is
                            required when saving as Nexus so the edit can
                            never create an unknown state). Downloaded mods
                            are not editable: a version carrying a FileId OR
                            a RemoteUploadedAt grounds the container (the
                            timestamp widens the evidence to pre-FileId
                            downloads), the row's pencil is hidden (its slot
                            preserved), and
                            both StartEdit and the primitive refuse (defense
                            in depth; no degraded fields, no card). The name
                            field is editable only for the Untracked choice
                            (the name is the identity for an untracked
                            container; a Nexus mod's name comes from Nexus +
                            the update check's name-sync would revert a
                            user-typed name), while the id, version, and
                            source switch stay editable; an identity change
                            on a multi-version
                            container swaps the form for an inline
                            plain-language removal confirm (never a nested
                            modal; the save-time state refresh + the typed
                            RemovalConfirmationRequiredException recover path
                            cover a version landing while the card is open);
                            refused saves + disk failures surface inline with
                            the form still editable; a successful save
                            deactivates the band + raises
                            ImportDetailsEdited, the mod list's reload
                            signal. The edit mode + the batch are mutually
                            exclusive (both entries check the shared inactive
                            gate) and the active card gates Add + drops for
                            either mode.
                            rate-limit notice sits in the Mods toolbar. While
                           the active profile has an enabled alternate mod
                           manager mod, a full-width non-dismissible caution
                           banner (a drawn swap-vert icon +
                           `ModManagerBannerText` carrying the manager mod's
                           display name, the same `GetActiveModManager`
                           derivation the launch flag consumes, read at every
                           Reload + the enable-toggle path) sits between the
                           import card + the row list, gating nothing
                           (reorder/lock controls stay fully functional). The Add split
                            button has five flyout items, all sticky modes that
                            set the default on click (the face label tracks the
                            mode): "Add Nexus Mods" (the default; opens the
                            Darktide Nexus Mods games page in the browser), "Add
                            Mod (archive)", "Add Mod (folder)", "Link
                            external folder" (folder picker, no modal; the
                            link flow lives on the `LinkedModsViewModel` child,
                            the ImportWorkflowViewModel pattern, exposed as
                            `vm.LinkedMods` and raised via its `ModsLinked`
                            event for the parent's reload), + "Import load
                            order" (txt picker -> the load-order review card
                            below);
                            `LinkedMods.LinkModsCommand` peeks the base name,
                            runs the collision check (excluding a re-link),
                            then `LinkFolder` + `AddMod(LatestPolicy)`. In Gaming
                            Mode the Add button disables entirely (every mode is
                            desktop-dependent; `IsAddEnabled` + a Desktop Mode
                            tooltip on the disabled button + an inline toolbar
                            hint + early-returns in the picker paths +
                            `AddNexusModsCommand` shows Desktop Mode guidance
                            instead of launching the browser). A linked row's
                            badge cell is a two-state indicator: available shows an
                            "External" pill (`OpenFolderCommand` opens the OS file
                            manager at the external folder; disabled with a
                            Desktop Mode tooltip while gaming), broken shows a
                            non-clickable "Folder unavailable" text in the same cell
                            (caution brush; `IsExternalBroken` pushed from
                            `IsExternalAvailable` at Reload). The policy ComboBox is
                            disabled for linked rows + the update-action cell stays
                            empty (space preserved). A Nexus + Latest row's source
                            badge appends the installed release tag inline
                            (e.g. `Nexus #8 · 1.0`, the `ActualVersion` joined from the
                            repo); Pinned exposes its version in the pin dropdown +
                            Untracked isn't Nexus-sourced, so neither appends it to the
                            badge. `ModItemViewModel`
                            carries the INPC state + derived `SourceUrl`/`UpdatePageUrl`/
                            `IsNexusLatest`/`CanShowUpdateAction`/
                            `UpdateActionEnabled`/`UpdateActionTooltip`/`NexusModId`;
                            `IsPremiumUser` + `IsGamingMode`
                            are context-forwarding reads off the shared
                            `ModRowContext`, so the per-row enabled state +
                            tooltip recompute from one source when the context
                            flips (while gaming, a
                            regular/unverified flagged row's update action +
                            tooltip carry Desktop Mode guidance instead of the
                            files-page launch; Premium rows keep the in-app
                            install path). The `IAutomaticUpdateService`
                            (ui/Session/) is the opt-in Premium automatic batch
                            chained after each check from `UpdateCheckRunner`
                            (captures the exact result, gates on authoritative
                            Success + updates + AutomaticUpdatesEnabled + active
                            profile + a fresh Premium verify, then runs the
                            sequential enqueue batch through
                            `ModUpdateEnqueuer` with per-iteration
                            active-profile re-check (a switch stops scheduling
                            + cancels the still-queued items admitted for the
                            left profile; an item the worker already started
                            completes under its own rules), isolates per-mod
                            RESOLVE failures into one summary alert (download
                            failures render on their rows), and adds nothing
                            else: the queue's UpdatesApplied is the reload
                            signal the list VM consumes).
                           The check is split by trigger:
                           `IUpdateCheckService.CheckAsync` (the v2 GraphQL
                           `modsByUid` batch query, 1 API call for all mods)
                           fires on profile load + the periodic timer, both
                           interval-gated; `IUpdateCheckService.CheckThoroughAsync`
                           (same v2 batch query; the two differ only in the result's
                           `Thorough` flag) fires on the manual "check now" button
                           under its own sliding-window throttle; both record their
                           authoritative outcome through the `IUpdateStateStore`
                          (Success replaces/clears, NoNexusMods clears, no-auth/
                          rate-limit/failed preserve) + share `LastResult`/
                          `CheckCompleted`, distinguished by the result's
                          `Thorough` + `Outcome` flags);
                          the app self-update service
                          `IAppUpdateService` (ui/AppUpdate/) with its
                          conditional `VelopackAppUpdateService` (real impl,
                          `#if CURATOR_VELOPACK`, wraps a Velopack `UpdateManager`
                          whose source is config-driven: null
                          `CuratorConfig.AppUpdates.SourceOverride` (the default)
                          builds the production anonymous
                          `Velopack.Sources.GithubSource` pointing at the Curator
                          repo, stable releases only; a set value (a local dir or
                          URL) builds the manager from `UpdateManager`'s
                          urlOrPath overload for local testing / self-hosted feeds,
                          read once at construction via the injected
                          `IConfigLoader`)
                          vs `NoopAppUpdateService` (default, IsUpdateSupported
                          false, registered in standalone Linux, portable
                          Windows, and dev builds)
                          split, registered conditionally in CuratorComposition;
                          `AppUpdateCheckRunner` (ui/Session/) fires ONE
                          availability check on startup (fire-and-forget,
                          best-effort, profile-independent, no periodic timer,
                          unlike the mod-update UpdateCheckRunner; gated on
                          `CuratorConfig.AppUpdates.CheckOnStartup`, read live
                          on startup; the manual Settings "Check for Updates"
                          calls the service directly + is never gated); the
                          shell status-strip dismissible update pill
                          (`ShowAppUpdateNotice`, session-only dismiss via the
                          dismiss button OR cancel-on-confirm, the notice-click
                          flow is confirm then download-under-ProgressDialog
                          then ApplyUpdatesAndRestart which exits the process
                          + Velopack relaunches; the pill, its link
                          (`HyperlinkButton.updateNoticeLink`), and its dismiss
                          (`Button.updateNoticeDismiss`) draw only from app-owned
                          per-theme `CuratorUpdateNotice*` brushes, scoped over
                          the Fluent `ContentPresenter` for normal/hover/pressed/
                          disabled/focus-visible so a low-contrast SteamOS accent
                          (issue #181) cannot make the notice illegible) + the
                          Settings destination "Updates" section; the
                          `IAppUpdateService.UpdateStateChanged` event fires on a
                          threadpool thread and the shell/Settings handlers
                          marshal to the UI thread via the shared `Action<Action>`
                          seam;
                           the DMF (Darktide Mod Framework)
                           install-prompt coordinator `DmfPromptService`
                           (ui/Session/), all
                           routed through the shell-owned modal queue
                           `IShellModalQueue` (ui/Session/:
                           `Enqueue(owner, showOn, modal)` + `DrainAsync`; an
                           owner's newer enqueue replaces its unconsumed entry,
                           different owners queue independently, the drain
                           consumes before running so a thrown modal cannot
                           re-fire). The coordinator subscribes to
                           `IProfileService.ProfileCreated` at construction
                           (nothing depends on it, so composition resolves it
                           once at startup to establish the subscription); when
                           `ProfilesViewModel.Save` calls `CreateProfile`, the
                           already-subscribed coordinator enqueues its prompt
                           for the Mods destination, and
                           `ShellViewModel.NavigateAsync` drains the queue after
                           the destination switch + enter effects, so the DMF
                           prompt runs as the topmost modal with Mods already
                           selected underneath; the drained delegate reloads
                           the mod list itself so an accepted existing-DMF add
                           is visible immediately afterward (an accepted
                           premium download needs no reload here: the download
                           queue's completion owns the add + reloads). A queued
                           entry survives visits to other destinations and runs
                           only on a real Mods entry; the shell no longer knows
                           DMF exists.
                           The prompt fires for one trigger
                           when DMF is not in the active profile: every new
                           profile that becomes active (no persisted flag: a
                           fresh ask per profile). Two cases: DMF in the repo
                           but not the profile -> instant add (case 1); DMF not
                            in the repo -> a download confirm (the message
                            tailors to whether Curator owns the `nxm://` handler,
                            read from the shared `INxmRegistrationState` with no
                            probe:
                            manager-download vs. manual-import guidance); on
                           confirm, premium users get the download enqueued
                           onto the shared download queue (DMF's head file is
                           resolved first so the queue's dedupe key is real +
                           the download fetches the exact file offered at
                           confirm; the download row owns progress + the
                           queue's completion owns the add + reload; a resolve
                           failure (no row yet) surfaces the localized alert),
                           while everyone else (no auth,
                           regular, or unknown premium state) gets the DMF Nexus
                           files page opened in the browser regardless of nxm
                           setup (when Curator owns the handler, the user clicks
                           Download there + the handler picks up the URL + adds
                           DMF to the active profile via the standard nxm flow;
                            when Curator does not own it, the user downloads the
                            archive and imports it via the normal add flow; on a
                            browser-launch failure, a fallback alert carries the
                            files-page URL) (case 2).
                            In Gaming Mode, case 2 resolves the Premium state
                            first: premium users get the same confirm + enqueued
                            in-app download, while everyone else (no auth, regular, or
                            unknown) gets an informational Desktop Mode alert
                            (no confirm, no browser launch, no acquisition
                            call; no nxm probe).
                           Decline is respected; DMF can be added later via the
                           normal add flow. The DMF flow never opens Nexus
                           Integrations or stops at an informational dead-end.
                          The first-run `OnboardingService` (ui/Session/) owns
                          the one-time Nexus setup offer: it shows the
                          `WelcomeWindow` (ui/Views/) once on first startup
                          (persisted via `IOnboardingState.OnboardingCompleted`),
                          and on a "Set up Nexus" choice persists completion
                          first, then navigates the shell to Nexus through
                          `IShellNavigation` (ui/Session/, implemented by
                          ShellViewModel + forwarded by the composition root as
                          a plain interface forward; wired from `App` after the
                          main window opens, exception-safe).
                          `IDialogService.ShowProgressAsync<T>`
                          runs the supplied work under a non-closeable spinner +
                          closes it on completion; `DialogTitleBar.ShowClose`
                          (a styled property) hides the spinner's close
                          button so the user cannot dismiss an in-flight
                          download). Modal dialogs close on ESC via the opt-in
                          attached behavior `EscapeClosesBehavior.IsEnabled`
                          (ui/Behaviors/, applied per-dialog; ESC calls
                          `Window.Close()`, the same path as the title-bar X so
                          result/cancel contracts are unchanged): applied to
                          ConfirmDialog,
                          DiscoveryEscapeHatchDialog, WelcomeWindow;
                          ProgressDialog (non-closeable) + the main window opt
                          out, so ESC never dismisses a spinner or exits the app)
  general/              Modificus.Curator.General -- cross-cutting infra (logging bootstrap:
                        Serilog day-rolling log (RollingInterval.Day writes
                        curator-<yyyyMMdd>.log, appended across starts within a day,
                        rolled at midnight, pruned to RetainedLogFileCount),
                        config loader, the shared OS shell-open launcher
                        (IExternalLauncher/ShellExternalLauncher: browser for a
                        URL, file manager for a folder, narrow failure filter),
                        the NexusGameIdentity constants (the Darktide game
                        domain + game id),
                         app-state store (active profile id +
                         last update-check timestamp + manual-refresh throttle
                         window + profile-scoped known-update snapshots +
                         last Nexus display-metadata backfill timestamp +
                         the main window's persisted geometry as the atomic
                         `AppWindowState` record under `MainWindowState` +
                         the game-dir takeover receipts as `RenamedModsFolder`
                         records under `RenamedModsFolders` via the
                         `IRenamedModsFoldersState` role), AddGeneral() DI ext)
  config/               Modificus.Curator.Config -- the CuratorConfig schema + defaults (POCO),
                        including the NexusConfig slot under Integrations
                        (AuthMethod {None,OAuth,ApiKey}, ApiKey, OAuth tokens, base URLs,
                        AutomaticUpdatesEnabled opt-in Premium auto-install)
                        + the AppUpdatesConfig slot (CheckOnStartup, gates the
                        automatic startup self-update check)
                        + the DiscoveryConfig slot (OverrideAutomaticDiscovery +
                        the neutral SteamInstallPath/DarktideGameBinaryPath/
                        CompatdataPath/ProtonBinaryPath snapshot fields; automatic
                        mode rewrites the active-platform fields from the discoverer,
                        manual mode validates the stored paths as-is)
                         + the Preferences.ModRowDensity slot (Detailed default,
                         Compact the one-line variant; absent/unknown normalizes to
                         Detailed) + the Preferences.ExternalModHosting slot
                         (the experimental staging-only launch opt-out; default
                         false = game-dir hosting; read live per launch) + the
                         AppPaths.ModThumbnailCacheDir root
                         (<app-data>/cache/mod-thumbnails)
   profiles/             Modificus.Curator.Profiles -- profile data model, persistence,
                          container-based staging (ProfileService.PrepareModRoot
                          discovers each enabled mod's base folder name inside the
                          resolved version folder + staging links (an NTFS junction
                          on Windows, a symlink on Linux) staged/mods/<baseName> ->
                          <versionFolder>/<baseName>/, then writes mods.lst + the
                          staging ownership marker (.curator.json inside the
                          staged mods/, rewritten every pass with schema +
                          profile id/name + projection timestamp via the shared
                          StagingOwnership.MarkerFileName contract, so a game-dir
                          hosting link aimed at the tree can prove Curator owns
                          it; relay-client reads only the file's presence); the
                          base name, not the container's display name, is the link
                          + mods.lst name; the StagingLinkCreator delegate selects
                          junction vs symlink per OS; a linked container stages
                          directly from its external folder, no version
                          resolution; the focused ProfilesRoot read feeds the
                          game-dir ownership prefix check) + SetModPolicy transitions + the
                        profile-scoped load-order lock (ModListEntry.OrderLocked:
                        a locked entry keeps its exact zero-based index across
                        SetModOrder, so a reorder projects the requested ordering
                        onto the unlocked slots only; toggled via
                        SetModOrderLocked, metadata-only so it implies no staged
                        change; AddMod inserts a fresh DMF add (Nexus mod 8 by
                        source, or the canonical lower-case dmf base folder
                        containing dmf.mod) at rank 0 + OrderLocked true,
                        shifting survivors down one rank, while every other add
                        appends unlocked + compacts Order dense,
                        RemoveMod drops the entry + compacts survivors so a
                        surviving lock's new dense index is the new baseline) + the
                        import-time base-name collision hard-block
                        (GetBaseNameCollision; two same-folder mods can't coexist
                        in a profile; resolves a linked mod's base name from the
                        external folder's own name) + the load-order import
                        family (ModLoadOrderParser: pure DML-exact
                        mod_load_order.txt parsing, per line trim + skip empty +
                        skip -- comments after trim, first-wins dedupe, BOM
                        tolerance, no #/// /inline-comment support;
                        LoadOrderPlanner: pure reconciliation of parsed names
                        against caller-resolved data (profile mods + repo
                        candidates keyed by base name) into the immutable
                        LoadOrderPlan (per-line outcomes Reorder/LibraryAdd/
                        Unresolved, OrderedContainerIds for SetModOrder,
                        LibraryAdds, UnmatchedNames), case-insensitive ordinal
                        matching, Nexus-sourced ambiguity preference with
                        remaining ties reported unmatched, no lock reasoning
                        (SetModOrder's own projection keeps locked slots);
                        ILoadOrderReconciler: the resolution glue resolving both
                        sides' base names through the shared internal ModBaseNames
                        helper, the same resolution staging uses) + per-profile
                        launch settings
                        (EnvVar/LaunchSettings: ordered env-var entries + game
                        args + the EnableLuaLogs toggle (emits Relay's bare
                        --log-lua flag, teeing Lua print output into the log
                        file) + the SkipSplash toggle (emits Relay's bare
                        --skip-splash flag, skipping Darktide's intro splash
                        state); CreateProfile(name, description, launchSettings) +
                        the atomic UpdateProfile(id, name, description, launchSettings)
                        (the single editable-profile write) are the launch-settings
                        persistence boundary + validate up front via the shared
                        LaunchSettingsValidator
                        (LaunchSettingsValidationError: index + field + kind;
                        single source of truth consumed by both the service and
                        the UI) -- names non-empty/no =/no NUL, no NUL in values,
                        case-insensitive duplicate rejection, reserved-name block
                        of 15 Curator-owned OS/launch + Relay config env (adds
                        RELAY_LUA_LOGS + RELAY_SKIP_SPLASH +
                        RELAY_MOD_MANAGER, the last owned by the manager-mod
                        detection behind Relay's --mod-manager flag);
                        backward-
                        compat null/missing normalization to empty, mirroring Mods;
                        GetLaunchSettings is the focused read the launch path uses;
                        apply at launch) + the alternate-mod-manager derivation
                        (GetActiveModManager: the focused read returning the
                        ActiveModManager record -- the enabled mod whose
                        resolved staging target is a base folder containing
                        mod_manager.lua, plus the staged manager file path;
                        content-based + manager-agnostic (no Nexus id, no
                        base.mod involvement, no special AddMod behavior),
                        shares the staging resolver so the answer matches what
                        PrepareModRoot stages, yields null when the manager
                        file is missing from the resolved target (never a path
                        Relay would hard-refuse), first-in-order-wins over a
                        hand-shaped duplicate; the manager mod stages +
                        lists like any ordinary mod; one derivation consumed
                        by both the launch flag + the mod-list banner) + ModCleanup (the startup
                        prune orchestration; keeps a referenced linked container by
                        containerId sentinel, since a linked container has no
                        versions, and keeps every referenced container's CURRENT
                        latest version folder unconditionally, regardless of the
                        entry's own policy, so a pinned entry can never let the
                        prune delete the container's newest version)
  mods/          Modificus.Curator.Mods -- the unified mod repository
                        (IModRepository: UUID containers per (source, identity),
                        opaque-ID version subfolders, per-container container.json
                        manifests, in-memory index rebuilt from a scan,
                        RenameContainer (display-label rename; identity Id +
                        on-disk directory unchanged; keeps the untracked-name
                        index consistent for untracked containers),
                        EditImportDetails (the name + source + latest-tag
                         correction primitive in one atomic manifest write:
                         same-identity edits retag without removal,
                         the initial Untracked->Nexus association records
                         identity + tag with no remote facts, Nexus->Untracked
                         or a Nexus id change resets remote claims + keeps only
                         the latest version's local facts behind an explicit
                         removeOlderVersions confirm flag (refused with the
                         typed RemovalConfirmationRequiredException, an
                         InvalidOperationException subclass), a downloaded
                         container (any version carries a FileId OR a
                         RemoteUploadedAt; only the download path records
                         either) refuses every edit, name-only included, a
                         name change is allowed only for an Untracked
                         destination (a Nexus name is Nexus-owned), a
                         duplicate
                         Nexus identity + a non-empty tag with an Untracked
                         destination are rejected, untracked-name + source
                         indexes stay coherent, + the container Id never moves
                         so every profile reference survives), PruneUnreferenced
                         GC at startup, keeping a referenced linked container by
                        containerId sentinel) + the version-policy model (ModVersionPolicy:
                        PinnedPolicy/LatestPolicy; PinnedPolicy pins by VersionId, a foreign
                        key to ModVersion.Folder, so the repo is the sole source of truth for
                        version details) + the
                        mod-source provenance model (ModSource: UntrackedSource/
                        NexusSource/LinkedSource, the last carrying a normalized
                        ExternalPath for a no-copy external folder, + ModSourceParser
                        URL parsing) + the source-agnostic display-metadata model
                        (ModDisplayMetadata: summary + thumbnail URL + adult flag; a
                        null ModContainer.DisplayMetadata means not-fetched, a non-null
                        object with empty fields is an authoritative fetched result with
                        no content, and the two stay distinct; backward compatible on
                        disk since an older manifest deserializes the field to null) + the
                        local-import service (IModImportService: folder/archive ->
                        container/version; content-based archive detection via
                        SharpCompress (zip/7z/rar/...) not extension, traversal-safe
                        per-entry extraction with AssertSafePath guard; AddVersion
                        stages extraction into a temp dir + atomically swaps on
                        success so failed re-imports are non-destructive; validates the
                        source has exactly one base dir with a matching <base>.mod +
                        preserves the base folder under <versionFolder>/<base>/;
                         exposes GetBaseName + FindExistingContainer peeks for the
                         collision block; AddVersion dedup refreshes
                         RemoteUploadedAt + FileId from the re-acquired version's
                         remote facts + takes an optional ModDisplayMetadata
                         that replaces the container's DisplayMetadata in the same
                         manifest update, null preserving the prior value so a manual
                         re-import never erases a prior acquisition or backfill;
                         ModVersion persists the Nexus FileId (nullable, the
                         RemoteUploadedAt precedent; recorded on both the
                         new-version + dedup-reuse branches, so legacy entries
                         self-heal by attrition) + the IsLatest contract keys on
                         the most recent ARRIVAL (the newest ImportedAt decides
                         the clock: a manual import with the newest arrival is
                         latest, otherwise the newest downloaded version by
                         RemoteUploadedAt with the arrival stamp breaking exact
                         ties), re-evaluated at every
                         AddVersion/RemoveVersion, so importing an older remote
                         file never flips latest while a download arriving
                         after a manual import correctly takes the flag
                         (issues #232 + its mixed-container incomplete fix);
                         TryInitializeDisplayMetadata is the atomic missing-only
                         initialization seam (writes + persists under the repo lock,
                         returns false if DisplayMetadata is already non-null); LinkFolder records an external
                         folder as a metadata-only LinkedSource container with no
                         copy, + IsExternalAvailable reports a linked container's
                         transient external-folder availability).
  integrations/         Modificus.Curator.Integrations -- the Nexus Mods v1
                        client + auth
                        (INexusClient over the v1 REST endpoints with per-request
                        auth via INexusAuthMessageFactory selector -- ApiKey /
                        OAuth / None factories, the latter doing 401-reactive
                         refresh; NexusAuthService the OAuth loopback + API-key
                         validate + sign-out orchestrator (raises
                         AuthStateChanged on every persisted method change; the
                         DMF prompt is
                         profile-creation-only and does not subscribe); NexusOAuthTokenStore
                        owns the OidcClient + token persistence; LoopbackBrowser
                        the IBrowser impl with an HttpListener on an ephemeral
                        port; Duende.IdentityModel.OidcClient 7.1.0 for the
                        OAuth machinery; client_id "modificus_curator" is a
                        build-time const; no client secret (Nexus accepts this
                        client as a public client; PKCE S256 protects the flow);
                        client_id is posted in the token body; scope "openid";
                         IModAcquisitionService the download +
                         extract + place orchestrator over INexusClient +
                         IModImportService + a plain HttpClient for the CDN
                         download; AcquireFromNexusAsync resolves the download
                         links, fetches name + version metadata, downloads to
                         temp, then imports via IModImportService.Import,
                         returning NexusAcquisitionResult (container + version
                         ids, the release tag, + IsHeadFile, whether the file
                         is the mod's newest non-archived MAIN file, computed
                         from the listing the acquisition already reads at
                         zero extra API calls) with per-file byte progress
                         (cumulative received + the Content-Length total when
                         sent, null without one, no separate HEAD call);
                         the same GetModInfoAsync call that resolves the name also
                         supplies the display metadata (summary + thumbnail URL +
                         adult flag), normalized once through the shared internal
                         ModDisplayMetadataMapper + forwarded through Import so it
                         lands on the container with no extra Nexus call;
                         AcquireLatestNexusAsync resolves the newest
                         non-archived MAIN file then forwards
                         to AcquireFromNexusAsync with null nxm tokens (premium
                         path); ResolveLatestNexusAsync resolves the head file
                         id + tag without a download (one ListModFilesAsync
                         call) for callers that need a concrete file id before
                         any item exists (the queue's dedupe key, resolved by
                         the enqueue fronts); ModFile gains an `archived` bool for the filter;
                        INexusModMetadataService the stable-v1 display-metadata
                        backfill (Nexus-only, missing-only, active-profile-prioritized,
                        one GetModInfoAsync per candidate, at most 25 attempted
                        per pass, at most one real pass per persisted 24-hour
                        window, serializing semaphore, zero/hard-rate-limit stop,
                        best-effort never-throws; persists through the repository's
                        atomic TryInitializeDisplayMetadata); IUpdateCheckService the Nexus-only
                        update-check service (1 v2 GraphQL `modsByUid` batch
                        query per check, 1 API call for all mods; computes UIDs
                        from game_id * 2^32 + mod_id, the Darktide game id in
                        General's NexusGameIdentity.DarktideGameId;
                        the server-computed `viewerUpdateAvailable` field
                        replaces the v1 Month-endpoint intersect, timestamp
                        tolerance, per-mod reconciliation, + reconciliation
                        pinning;                         `viewerUpdateAvailable == true` flags a mod,
                        `false` or `null` (server has no download record for
                        the user, e.g. a manually imported mod) does not;
                        a version-string comparison supplements this: if the
                        server's latest `version` differs from the installed
                        `VersionString` the mod is also flagged (catches older-
                        version-installed, multi-PC, + manual-import cases the
                        server's per-user download tracking misses);
                        a tier-3 latest-file-version confirmation refines
                        tier-2-only flags: it resolves the newest non-archived
                        MAIN file via NexusModFiles.LatestMain (the same filter
                        the download path uses) + clears the flag when that file
                        version equals the installed version (the page-header
                        version can lag the latest file), is best-effort +
                        cached per (mod id, page version, updated-at) with a 24h
                        TTL (in-memory, session-scoped), + only ever removes
                        flags (tier-1 viewerUpdateAvailable is authoritative +
                        untouched);
                        the batch covers EVERY NexusSource mod (Latest AND Pinned),
                        but Pinned mods are never flagged (the tier flag logic is
                        Latest-only); linked mods are excluded entirely (they have
                        no Nexus identity + no versions, so they never enter the
                        check); the same batch query also returns the current
                        Nexus mod `name` for every id sent, so a name-sync pass
                        after the tier logic renames each container whose stored
                        Name has drifted to match its current Nexus name at zero
                        extra API cost (the Nexus name wins; identity Id unchanged;
                        UpdateCheckResult.NamesChanged signals the UI to refresh row
                        names in place);
                        rate-limit-aware with the all-zero Unknown guard +
                        NexusRateLimitException surfacing; carries an explicit
                        `CheckOutcome` (Success/NoAuth/NoNexusMods/RateLimited/
                        Failed) so authoritative success is distinguishable +
                        records each result through the `IUpdateStateStore`
                        (the profile-scoped known-update persistence rules over
                        `IKnownUpdateState.KnownUpdates`: Success replaces/clears,
                        NoNexusMods clears, no-auth/rate-limit/failed preserve,
                        hydration self-heals removed/pinned/source-changed/
                        version-changed entries, AcknowledgeInstall clears a
                        single entry on a successful version change;
                        LastResult + CheckCompleted event for the mod-list;
                        the update family takes the profile's mod list as
                         caller-mapped `ModListCandidate` records
                         (ContainerId + Policy), so Integrations holds no
                         Profiles reference; + the pure static
                          `UpdateEligibility` evaluator, the one source of the
                          four known-update eligibility rules (member /
                          LatestPolicy / NexusSource same ModId / ordinal-ignore-
                          case version match, with an EMPTY expected version
                          matching an empty installed tag so an
                          unknown-resolution install is never dropped as stale),
                          shared by the store's hydration
                          self-heal + the download queue's dequeue-time
                          revalidation (UI); + INexusClient.SearchModsAsync:
                          the ANONYMOUS v2 GraphQL mods search (no auth header;
                          the request routes around the auth factory with only
                          the app-identification headers, works signed out,
                          sits behind Cloudflare not the API key budget, no
                          x-rl headers expected but parsed if present), run
                          TWICE + unioned by mod id preserving search order
                          (name-leg hits first): once with
                          name:[{op:WILDCARD,value:"*terms*"}] (surrounding
                          wildcards) + once with
                          nameStemmed:[{op:WILDCARD,value:"terms"}] (bare;
                          the stemmed index matches stemmed words so a wildcard
                          breaks it), both against
                          gameId:[{op:EQUALS,value:"4943"}] with
                          sort:{relevance:{direction:DESC}} + count, returning
                          NexusSearchResult (modId + name + uid); GraphQL-level
                          errors in a 200 OK body surface as NexusApiException;
                          callers stay serial + human-paced, no retries)
  steam/                Modificus.Curator.Steam -- Steam + Darktide + Proton discovery
                        (multi-library + compatdata; Linux Proton resolves from Steam's
                        CompatToolMapping in config.vdf, app-specific entry first then
                        the global "0" entry, to a custom compatibilitytool.vdf
                        or a Valve-managed appinfo/appmanifest install, never a
                        directory-name guess; with neither mapping, Darktide's appinfo
                        recommended_runtime (the steam_deck_compatibility metadata) is
                        Steam's non-user default on any Linux host (one appinfo.vdf
                        scan collects both the compat_tools registry + the
                        recommendation; an invalid/unreadable mapping or a
                        native/missing recommendation fails unresolved without falling
                         through); Steam Deck identity (SteamDeckDetector +
                         SteamDiscoveryOptions.IsSteamDeck, detected from OS release
                         metadata ID=steamos + VARIANT_ID=steamdeck, host file first)
                         is a generic platform identity input, not Proton policy;
                         Gaming Mode session identity (GamingModeDetector, public
                         static: the complete environment signature SteamOS=1 +
                         SteamGamepadUI=1 + XDG_CURRENT_DESKTOP=gamescope, all three
                         exact values required, independent of Deck hardware
                         identity); the UI captures it once via GamingModeState;
                         Steam text KV1 parsing centralized through SteamTextVdf with
                         HasEscapeSequences always on, ValveKeyValue 0.70.0.499), the
                        ISteamService.Discover automatic/manual mode policy + Rediscover
                        forced-automatic surface, IsGameRunning (WinProcessLookup
                        via process comm on Windows; LinuxProcessLookup via /proc
                        argv[0] under Proton -- selected once by DI), injectable seams
  relay-client/         Modificus.Curator.RelayClient -- the v1 launch façade
                        (IRelayLaunchService.Launch → LaunchResult; reads the
                        profile's GetLaunchSettings per launch + threads them
                        through the strategy; Windows: direct
                        launcher Process.Start with profile env as overrides;
                        Linux: proton run with both STEAM_COMPAT_*
                        env + Z:\-translated paths + profile env merged
                        inherited -> AppImage removals -> profile env ->
                        Curator-owned STEAM_COMPAT_* last, scrubbing the five
                        AppImage/desktop-identity variables APPDIR, APPIMAGE,
                        ARGV0, OWD, BAMF_DESKTOP_FILE_HINT from the inherited
                        environment so Darktide does not inherit Curator's
                        AppImage identity; Relay writes its own per-day log
                        (relay-<yyyyMMdd>.log next to Curator's Serilog
                        curator-<yyyyMMdd>.log, resolved at launch from the
                        configured Logging.RelayLogFile stem by RelayLog, which
                        inserts the day stamp before the extension, + pruned
                        to the same RetainedLogFileCount, best-effort, before the
                        spawn) passed as --log-file, followed by an unconditional
                        bare --log-append (Relay's per-day file is shared across
                        launches, so it appends, no value, not Z:\-translated on
                        Linux); a profile's EnableLuaLogs emits Relay's bare
                        --log-lua flag appended after --log-append (a tee of
                        Lua print output into the log file, no value, not
                        Z:\-translated on Linux); a profile's SkipSplash emits
                        Relay's bare --skip-splash flag appended after --log-lua
                        (skips Darktide's intro splash state, no value, not
                        Z:\-translated on Linux); when the profile has an
                        enabled alternate mod manager (derived right after the
                        staging pass via GetActiveModManager, which locates +
                        verifies the manager in the staged tree), the pair
                        --mod-manager + the ABSOLUTE projected manager file lands
                        immediately after the --mod-path value pair (verbatim
                        on Windows, Z:\-translated on Linux; the launch path
                        projects the flag value onto the effective mod root --
                        the game dir under default hosting, the staged root
                        under the external preference -- formulated where
                        --mod-path is; null means Relay's built-in manager
                        and no flag, and a manager file missing from the
                        resolved target never gets one); game args append one bare -- then
                        each arg as its own ArgumentList entry (Relay's --
                         contract; no version preflight); the spawn seam IProcessLauncher takes
                         one immutable ProcessLaunchRequest with FilePath,
                         Arguments, EnvironmentOverrides, EnvironmentVariablesToRemove,
                         and CreateNoWindow, applied by ProcessLauncher
                         as UseShellExecute=false + CreateNoWindow + ArgumentList +
                         remove-then-override over the inherited environment,
                         and returns an ISpawnedProcess? observation handle
                         (null = could not start; WaitForExitAsync + Dispose,
                         nothing else) whose exit the launch service tracks as
                         the bare fault-free RelayExited task on a Launched
                         result, disposing the handle itself;
                         CreateNoWindow hides the Relay console window unless the
                        global ShowRelayConsole preference opts in (read live from
                        config at launch; a harmless no-op on Linux, where no
                        console appears regardless); ResolveLauncherPath prefers the
                        configured RelayDir, then on both platforms falls back to the
                        app-local relay/ shipped inside a Velopack payload at
                        <BaseDirectory>/relay/, then uses the portable sibling fallback
                         on Windows only; the game-dir mod host step between
                         staging + spawn: IGameDirModsHost/GameDirModsHost owns
                         the <game>/mods ownership ladder (claims proven by the
                         staging marker inside the link's target or a target
                         under the profiles root, never reparse-ness alone;
                         absent -> create silently, ours -> re-point silently
                         via delete+recreate of the link only, foreign ->
                         LaunchStatus.GameDirConflict with the detected path on
                         Message + the game dir on GameDirPath before any
                         mutation; TakeOver performs the consented rename-aside
                         takeover (returning the renamed entry's path, null
                         when nothing was renamed): mods_<yyyyMMdd-HHmm> with
                         numeric bump on collision + a receipt through
                         IRenamedModsFoldersState + a best-effort README.txt
                         inside the renamed folder (folder case only, a
                         failure logged never surfaced);
                         RemoveOwnedLink is the
                         best-effort external-mode cleanup that never touches a
                         foreign entry; link creation reuses the Profiles
                         StagingLinkCreator primitive; registered in the
                         composition root after AddProfiles + AddGeneral).
                         Hosting is the default: GAME_DIR =
                         dirname(dirname(DarktideGameBinaryPath)) validated to
                         exist, --mod-path = GAME_DIR (the Linux strategy
                         Z:\-translates it exactly as before); the
                         Preferences.ExternalModHosting opt-out (read live per
                         launch) restores --mod-path = staged root + the
                         best-effort owned-link removal; link IO/Win32 failures
                         map to LaunchStatus.Error with the exception message)
  nxm/                  Modificus.Curator.Nxm -- the nxm:// scheme-handler plumbing:
                        NxmUrlParser (mod-download / oauth-callback /
                        collection URL types), NxmIpcFraming (length-prefixed UTF-8 frames),
                         SingleInstanceGuard (the process-enumeration single-instance check,
                         with an injectable enumerator seam + the non-throwing
                         IsAnotherInstanceRunning query over the same enumeration, which
                         the handler relay reuses for its cold-start pre-check), NxmIpcServer (the named-pipe
                         server; Bind runs two SEPARATE checks: SingleInstanceGuard first
                         (fatal NxmSingleInstanceException on collision), then the pipe bind
                         which degrades gracefully on IOException; accept loop Disconnects
                         between clients, resting on the explicit invariant that
                         INxmRouter.RouteAsync returns promptly: request processing is the
                         routed handler's responsibility (enqueue-or-refuse), never inline
                         on the accept loop; the RouteAsync + HandleAsync docs state the
                         prompt-return contract), INxmRouter + no-op INxmModDownloadHandler
                        default (the real handler is registered via AddSingleton
                        last-wins, in CuratorComposition after AddNxm()), the OS
                        scheme-handler registrar
                         (INxmHandlerRegistrar: WindowsNxmHandlerRegistrar writes
                         HKCU\Software\Classes\nxm; LinuxNxmHandlerRegistrar writes a .desktop
                         file + xdg-mime default; every xdg-mime invocation runs sanitized:
                         the child's env is the parent's with ONLY LD_PRELOAD removed
                         (Steam's overlay preload slows host utilities ~10x; Curator's own
                         env untouched), while the wait stays plain + synchronous (a hung
                         desktop helper hangs the probe rather than being masked:
                         deliberate, fail loud); AppImage registration atomically copies the
                         handler to a durable per-user directory + creates a sibling symlink
                         to $APPIMAGE; startup maintenance refreshes those files only while
                         Curator owns the active association; Unregister is self-guarded on
                         both platforms: it never removes another program's registration +
                         touches only Curator's own registration files (a no-op or a
                         removal of Curator's own files depending on platform state), so
                          callers never pre-check), + NxmHandlerRelay (the testable core the
                          handler exe calls: hot-path IPC delivery + cold-start launch+retry with an
                          advisory process pre-check (a detected Curator process skips the
                          duplicate launch and goes straight to the retry loop, the burst
                          case; distinct stderr lines per branch; the default reuses the
                          SingleInstanceGuard enumeration; 1s default connect timeout),
                          UseShellExecute=false on both OSes). AOT-friendly (IsAotCompatible;
                        only raw byte/UTF-8 IO in the handler path).
  nxm-handler/          Modificus.Curator.NxmHandler -- the OS-registered nxm:// scheme handler
                        (console exe, native AOT). Program.cs is one line: NxmHandlerRelay.RunAsync.
                        Forwards the raw URL to running Curator over the fixed pipe, or (cold start)
                        launches Curator (no args) + retries the pipe ~250ms/30s, then delivers.
  tests/
    Modificus.Curator.General.Tests/         xUnit tests for the general library
                                          (incl. the AppStateStore KnownUpdates round-trip +
                                          old-file-without-field compatibility + the
                                          atomic MainWindowState record round-trip + the
                                          RenamedModsFolders receipts round-trip/no-clobber/
                                          old-file compatibility)
    Modificus.Curator.Profiles.Tests/        xUnit tests for the profiles library (incl. staging
                                          + the staging ownership marker (written/rewritten
                                          per pass, profile identity + timestamp, the
                                          renamed-profile refresh) + ProfilesRoot
                                          + the launch-settings round-trip/normalization/validation
                                          + DmfAddTests: the DMF fresh-add rule -- Nexus mod 8 +
                                          canonical dmf/dmf.mod recognition (untracked + linked),
                                          prepend-at-rank-0-locked with survivor metadata intact,
                                          lookalikes/unknown ids ordinary, idempotent re-add,
                                          remove-then-re-add)
                                          + ModManagerDetectionTests: the alternate-manager
                                          derivation -- base/mod_manager.lua recognition (untracked +
                                          Nexus + linked, the exact staged manager path), ordinary
                                          staging/mods.lst behavior for the manager mod, null for
                                          disabled/unresolvable/missing-file/capitalized-Base shapes,
                                          the unknown-profile throw, first-in-order-wins
                                          + ModLoadOrderParserTests: the DML-exact
                                          reader (trim/blank/comment-after-trim,
                                          dedupe first-wins, BOM, the rejected
                                          #/// /inline tolerances, empty + comment-only)
                                          + LoadOrderPlannerTests: the pure
                                          matching table (profile/library/unmatched,
                                          case-insensitive, the Nexus ambiguity
                                          preference + remaining ties unmatched,
                                          ordered ids over file order, unlisted
                                          not appended, empty plan) + the real
                                          reconciler over the fixture
                                          (policy-resolved profile base names,
                                          latest/linked repo candidates, the
                                          unresolvable-entry unmatched report)
                                          + ModCleanupTests: the startup prune -- the linked
                                          keep/unreferenced-drop pair + the managed latest-keep
                                          (a pinned entry keeps the pinned folder AND the
                                          container's current latest, a Latest entry on a mixed
                                          container keeps the resolved latest, unreferenced
                                          superseded versions still dropped, empty-container
                                          removal unchanged)
    Modificus.Curator.Mods.Tests/      xUnit tests for the mod repository + import
                                        (incl. the linked-folder add + linked-container prune,
                                        + the display-metadata AddVersion/Import pass-through
                                        + the IsLatest arrival-rule contract (the decision
                                        table: a download arriving after a manual import takes
                                        latest, a later manual import lands as latest, an
                                        older remote file never flips latest, manuals-only by
                                        ImportedAt, downloads-only by RemoteUploadedAt; the
                                        dedup branch is not a new arrival; RemoveVersion
                                        promotion under the rule)
                                        + FileId persistence on both AddVersion branches
                                        + TryInitializeDisplayMetadata atomic missing-only init
                                        + EditImportDetailsTests: every primitive branch (the
                                        Untracked-only name rule incl. the rename-with-switch +
                                        the Nexus name refusal, same-identity retag,
                                        Untracked->Nexus incl. the empty-tag
                                        association path, the Nexus-unknown retag, the identity
                                        reset with older-version removal, Nexus->Untracked), the
                                        downloaded-not-editable refusal (a FileId OR a
                                        RemoteUploadedAt on any version refuses every edit
                                        name-only included, both evidence shapes + the
                                        older-version-grounded case), the removeOlderVersions
                                        guard, the
                                        tag-collision throw, the duplicate-identity guard, + the
                                        untracked-name index coherence
                                        + the manager archive shape: base/mod_manager.lua with an
                                        empty base.mod validates with base name base)
    Modificus.Curator.Integrations.Tests/    xUnit tests for the Nexus client
                                          (against a fake HttpMessageHandler),
                                          the auth factories (apikey / OAuth / None + selector),
                                          the OAuth flow scripted with a fake IBrowser + stub
                                          discovery+token endpoint (via the OidcClient backchannel
                                          seam), the LoopbackBrowser/HttpListener against an
                                          ephemeral port, the NexusConfig JSON round-trip, and the
                                          ModAcquisitionService (download + extract + place against
                                          a fake INexusClient + fake IModImportService + stub CDN,
                                          incl. the display-metadata capture from the shared mapper,
                                          the progress tuple (Content-Length total + null total),
                                          the remoteFileId forward, the IsHeadFile computation,
                                          + ResolveLatestNexusAsync's no-download head resolution)
                                          + the UpdateCheckService (Nexus-only
                                          update check against a fake INexusClient +
                                          caller-built ModListCandidate batches +
                                          fake IModRepository)
                                          + the UpdateStateStore (the profile-scoped
                                          known-update persistence rules: success
                                          replaces/clears, failed/no-auth/rate-limited
                                          preserve, no-Nexus-mods clears, acknowledge,
                                          + the hydration self-heal for removed/pinned/
                                          source-changed/version-changed entries)
                                          + the UpdateEligibility evaluator (the four
                                          rules + every rejection reason + the
                                          case-insensitive version match, incl.
                                          the empty-expected/empty-installed
                                          unknown-resolution match + the
                                          empty-vs-nonempty mismatches; the
                                          update-install revalidation that
                                          consumes it runs in the UI-layer
                                          download queue, covered by
                                          ModDownloadQueueTests)
                                          + SearchModsAsync (the two-query union
                                          by mod id with name-leg hits first,
                                          the wildcard placement per leg, the
                                          no-auth-header + works-signed-out
                                          assertions, empty-on-both, the
                                          GraphQL-error + non-2xx
                                          NexusApiException paths, rate-limit
                                          header forwarding, the non-Darktide
                                          domain rejection)
                                          + the NexusModMetadataService (stable-v1
                                          display-metadata backfill: the 24-hour gate,
                                          the 25-attempt cap, active-profile-priority
                                          ordering, the zero/hard-rate-limit stop, the
                                          atomic missing-only persistence) + the
                                          ModDisplayMetadataMapper normalization
    Modificus.Curator.Steam.Tests/           xUnit tests for discovery + IsGameRunning
                                            (incl. the Proton selection precedence: app-specific,
                                            global, invalid-entry no-fall-through, + the appinfo
                                            recommended-runtime fallback end-to-end, identical
                                             regardless of Deck identity; the appinfo reader
                                             one-pass snapshot against a realistic multi-entry
                                             fixture matching the live shape; + the OS-release
                                             Deck detector + the Gaming Mode session detector
                                             (the complete three-variable signature, wrong/
                                             missing values rejected))
    Modificus.Curator.RelayClient.Tests/ xUnit tests for the launch façade (dual-purpose:
                                            `dotnet test` = xUnit; `dotnet run` = composition smoke harness);
                                            covers RelayLaunchServiceTests (Windows + Linux arg
                                            assembly + DiscoveryIncomplete/StagingFailed/Error
                                            mapping + the game-dir hosting step: the --mod-path
                                            switch to GAME_DIR under hosting + back to the staged
                                            root under the external preference read live per
                                            launch, the owned-link removal call, the
                                            GameDirConflict result shape, the link-failure +
                                            underivable-game-dir Error mappings, the Linux Z:\
                                            translation of GAME_DIR, + a real-host end-to-end
                                            launch from the temp game dir
                                            + the --mod-manager handoff: the flag pair carries
                                            the manager file projected onto the effective mod
                                            root (the game-dir mods link path under default
                                            hosting on both strategies, Z:\-translated on
                                            Linux; the staged path under the external
                                            preference), no flag + one derivation call per
                                            launch reaching staging, zero calls on a
                                            discovery-incomplete launch
                                            + the RelayExited exit tracking over the
                                            fake ISpawnedProcess: completes when the fake exits,
                                            completes + disposes when exit observation throws,
                                            null on every non-Launched result + the Linux five-key AppImage-identity
                                            removal set + the Windows empty removals/overrides
                                            + the launch-settings merge: Linux profile env
                                            before Proton startup alongside the AppImage
                                            removals + STEAM_COMPAT_* overrides, Windows
                                            profile env as overrides, empty/legacy when no
                                            settings) + GameDirModsHostTests (every claim-ladder
                                            row against the real platform link primitive + the
                                            real receipts store: absent creates, ours-by-marker
                                            left in place, ours re-pointed with the old target
                                            surviving, ours by marker outside the profiles root,
                                            a dead link under the profiles root silently
                                            recreated, foreign real dir/file/link/dead-link
                                            conflicts reported untouched, the takeover rename +
                                            receipt + best-effort README incl. a README-write
                                            failure still recording the receipt, collision bump +
                                            file case + no-op cases + the host-through-ladder
                                            retry, + the
                                            best-effort removal semantics) + GameArgumentsTests (the bare-`--`
                                            contract via the pure BuildLauncherArgs seam, incl.
                                            the --mod-manager pair immediately after --mod-path
                                            when a manager file is passed: verbatim Windows /
                                            Z:\-translated Linux / absent when null),
                                            ProcessLauncherTests (the deterministic BuildStartInfo
                                            path: a requested inherited key is removed, an
                                            unrelated inherited key remains, an override is
                                            applied, an override wins after removal,
                                            UseShellExecute=false, arguments stay distinct with
                                            spaces + shell metacharacters), WinePathTests, + the
                                            AddRelayClient DI wiring
    Modificus.Curator.UI.Tests/              xUnit tests for shell navigation (all five
                                            destinations, default Mods selection, compact-pane
                                            toggle, same-destination no-op, leave/enter lifecycle,
                                            dirty-Profiles-draft navigation cancellation, entering
                                            Settings rehydrates + leaving Settings runs the mod-list
                                            + app-update refresh, entering Integrations refreshes +
                                            leaving cancels auth + reloads the mod list with zero
                                            registration refreshes on any leave, exactly one seed
                                            refresh at shell construction + the strip following a
                                            shared-state publish, Launch
                                            CanExecute + execution following
                                            IProfileSession.ActiveProfileId directly) + the
                                            NxmRegistrationStateTests (the production shared-state
                                            contract: unavailable-without-registrar publishes, the
                                            registrar read on refresh, a probe throw treated as
                                            not-registered, Changed marshaled through the UI seam)
                                            + the
                                            ShellLaunchAttemptTests (the launch-attempt state via
                                            deterministic yield + timeout + relay-exit seams: attempt set +
                                            CanExecute false before the launch service runs, false
                                            eager/polling state never re-enables while waiting,
                                            IsRunning=true completes the handoff with Launch still
                                            disabled by the running gate, a held relay exit keeps
                                            the attempt set after Darktide is observed + when the
                                            session was already running at handoff entry until
                                            the exit lands, timeout clears the attempt
                                            for retry when the combined wait stays unresolved, failure results keep the attempt through the
                                            dialog then clear, exception path clears, direct
                                            concurrent execution rejected) + the
                                            ShellGameDirConflictTests (the game-dir consent
                                            flow: both choices, the retry-once guard with a
                                            second conflict surfacing the error alert, the
                                            rename notice + its before-the-retry ordering +
                                            the null-return skip, the takeover-failure alert,
                                            the attempt state held through the modal, + the
                                            malformed-result
                                            degradation) + the LaunchOverlayTests
                                            (the full-client launch overlay as XML source tests:
                                            overlay bound to the attempt state + SplitView disabled,
                                            top-layered hit-testable scrim, localized card + stock
                                            indeterminate ProgressBar, no interactive controls,
                                            accessibility metadata, unchanged Launch button, palette
                                            contrast, no suppressed window chrome) + the
                                            ProfilesViewModelTests (profile create/save/cancel/
                                            delete/switch, no-active states, running-state gates,
                                            dirty navigation, banner/picker, inline launch-settings
                                            validation + atomic save, DMF prompt timing after create)
                                            + the LaunchSettingsEditorViewModelTests (existing-
                                            settings load, add/remove rows, inline localized
                                            validation -- empty/`=`/NUL name, NUL value,
                                            case-insensitive duplicate, reserved name -- + a Logging
                                            toggle (EnableLuaLogs emits Relay's bare --log-lua flag) +
                                            a SkipSplash toggle (SkipSplash emits Relay's bare
                                             --skip-splash flag)) + the
                                             NxmModDownloadHandler gates + peek + enqueue +
                                             error wiring + the mod-list update flow: profile-scoped
                                             known-update persistence/hydration, the
                                             UpdateCheckRunner candidate pull + the
                                             unreadable-profile skip, the
                                             UpdateRefreshGate (server reset
                                             governs, fallback cooldown, timer
                                             lifecycle, marshaled StateChanged,
                                             throttle coupling), the stable
                                             per-row update action (no-update disabled, flagged
                                             accent, Premium resolve + enqueue, regular/unknown
                                             files-page open, launcher failure alert, unsupported
                                             rows),
                                             UpdateCommand premium/regular branches
                                             + the stale-flag silent no-op + the resolve-failure
                                             alert + the automatic-update setting
                                              + the AutomaticUpdateService
                                              gating/sequencing/resolve-failure isolation/profile-
                                              switch/mid-batch-profile-deletion/cancellation +
                                              the version-unknown-row exclusion (manual click only);
                                              + the download queue (ModDownloadQueueTests:
                                              serial FIFO worker, dedupe join + pulse,
                                              dequeue-time auth recheck + eligibility
                                              revalidation (incl. the empty-ExpectedVersion
                                              unknown-resolution install passing over an
                                              unknown-version container),
                                              the exact-FileId repository hit,
                                              head/non-head policy on both paths, ProfileAdd vs
                                              UpdateInstall completions, token-authoritative
                                              cancel (queued drop + active interrupt),
                                              retry/dismiss, mixed nxm + update clicks sharing
                                              the worker, the admission-event ordering on a
                                              fast hit)
                                             + the download rows (ModListDownloadRowsTests: the
                                             morph-in-place vs appended hosting projection incl.
                                             filter-hidden targets + rehosting on profile switch
                                             + reload, the morph's affordance suppression with
                                             structural controls kept, the empty-state
                                             suppression while a download is active, the failed
                                             corpse + fresh attempt coexisting, cancel/dismiss/
                                             retry forwarding, the join pulse, every phase/byte
                                             render state; DownloadRowXamlTests: the one shared
                                             status template hosted by all three surfaces, the
                                             appended section separate with no reorder
                                             affordances, the morph suppression bindings, the
                                             drawn-geometry icons + resx keys);
                                             + SourceUrl resolution; + the
                                             linked-folder flow
                                            (LinkModsCommand peek/collision-refusal/re-link +
                                            LatestPolicy add, the linked badge two-state available/
                                            broken, OpenFolderCommand launch + failure alert, the
                                            disabled policy + empty update-action cell for linked
                                            rows, IsExternalBroken on Reload);
                                            + the mod-list manager banner
                                            (ModListModManagerBannerTests: IsModManagerActive +
                                            ModManagerBannerText follow the GetActiveModManager
                                            result at every Reload + on the enable-toggle path,
                                            flipping off when the result clears, the row/repo/
                                            "base" name fallback chain);
                                            + the profile-scoped load-order lock + drag-reorder
                                            surface (the lock-aware FakeProfileService projection,
                                            OrderLocked carried on Reload + move/grip availability,
                                            ToggleOrderLock persists without HasPendingChanges,
                                            locked-row move/drag no-ops, Move Up/Down skip locked
                                            rows with locked-first-stays-first, drag CommitReorder to
                                            first/middle/last unlocked rank with multiple locks +
                                            one SetModOrder call + exact final order, same-rank /
                                            invalid-rank / missing-source / locked-source /
                                            no-active-profile rejection, no-lock move regression)
                                            + the pure reorder math (ReorderGestureMath threshold
                                            inclusive at 8 DIP, target unlocked rank over others
                                            only, marker before/after/none, lift translation
                                            (pointer delta + scroll-offset delta, both directions),
                                            edge-band auto-scroll + offset clamp);
                                            + the filter/search projection
                                            (ModListFilterTests: hide-filter/search/combined
                                            narrowing, clearing restores, projection survives
                                            reload + clears on profile switch, ToggleEnabled
                                            under the hide-filter, the no-matches vs add-hint
                                            exclusivity, the updates-only filter (flagged-rows
                                            projection incl. the reload hydration-ordering
                                            regression, AND-composition with the other filters,
                                            a landed check reprojecting the live filter,
                                            session-transient lifecycle, empty-state
                                            exclusivity, tooltip locality), move availability
                                            over visible unlocked
                                            neighbors, reorder-through-filter via Move Up/Down +
                                            CommitReorder with hidden rows keeping relative order +
                                            one SetModOrder call, locked rows keeping indices,
                                            no-op/hidden-source/out-of-range rejections)
                                            + the pure visibility-aware planner
                                            (ModReorderPlannerTests: all-visible parity with the
                                            lock projection, move up/down across hidden rows
                                            landing the source adjacent in the stored order,
                                            drop-at-end with trailing hidden rows, single visible
                                            row, locked-interleaved, hidden/locked/missing source
                                            + rank-range rejections);
                                             + the DmfPromptService (the two DMF
                                             cases: add existing / download + add or
                                             browser-open, the new-profile trigger
                                             (enqueue on create, prompt on the modal
                                             queue's Mods drain, reload after the
                                             prompt), the decline path, the premium
                                             enqueued download (head resolved at
                                             enqueue; resolve failure alerts),
                                             the non-premium/unknown/no-auth browser-open
                                             regardless of the registration state (the
                                             confirm wording follows the shared state
                                             with zero probes), the Gaming Mode branch
                                             (non-premium tiers get the Desktop Mode
                                             alert with zero browser/acquisition calls;
                                             premium keeps the in-app download; case 1
                                             unchanged), and the
                                             prompt-timing-after-create)
                                            + the Gaming Mode gating (ModListGamingModeTests:
                                            Add-button availability, the gaming guidance
                                            alerts for AddNexusMods + regular-tier
                                            updates with zero launcher calls, the
                                            empty-state hint matrix, the context
                                            push-down; GamingModeGatingXamlTests:
                                            XAML-source
                                            assertions on the disabled bindings +
                                            ShowOnDisabled + inline hints + the resx
                                            keys (the shared-badge assertion reads the
                                            one template definition);
                                            ModRowSharedTemplatesTests: the
                                            single-definition contract for the shared
                                            row markup (every shared control exists
                                            once, both roots host the templates, the
                                            compact spacing styles, the 680-DIP
                                             breakpoint); + the edit-import-details
                                             surface (ImportWorkflowEditModeTests,
                                             the import card's edit mode:
                                             activation + prefill, the batch/edit
                                             mutual exclusion incl. the
                                             mid-processing refusal + the Add
                                              gating, the validation matrix over
                                              the shared ImportSourceValidator
                                              (Untracked/Nexus, version required,
                                              bare id or URL parse), the
                                              downloaded-not-editable StartEdit
                                              refusal (both grounding shapes:
                                              FileId + RemoteUploadedAt-only),
                                              the name field's choice-following
                                              editability incl. the programmatic
                                              Nexus-name refusal (defense in
                                              depth via the primitive), the inline
                                              identity-removal confirm step +
                                              Back + both recover paths (the
                                              save-time refresh + the typed
                                              RemovalConfirmationRequiredException),
                                              refused-save + disk-failure inline
                                              surfacing + correction, the
                                              untracked-name conflict, the save +
                                              ImportDetailsEdited reload signal,
                                              the unknown/linked screening;
                                              the in-row band's target
                                              tracking (ModListViewModelTests:
                                              IsEditTarget + band-context
                                              assignment on start, re-attachment
                                              after a mid-edit reload, save +
                                              cancel clearing, the morph-close
                                              rule, the editing row's anchored
                                              grip/moves while others still
                                              move, the batch keeping the top
                                              card + no band);
                                             ImportSourceValidatorTests: the shared
                                             parse + remote-field rules both card
                                             modes consume; the load-order card
                                             (LoadOrderImportViewModelTests:
                                             activation + the checkbox defaults,
                                             the batch/edit/load-order mutual
                                             exclusion through the shared card
                                             gate, apply = one SetModOrder over
                                             every matched container + AddMod
                                             only for included adds + reload +
                                             deactivate, the no-include + empty-
                                             file refusals, cancel + profile
                                             switch, the search URL + launcher
                                             failure alert;
                                             the resolver tier: candidate
                                             arrival fills the top slot + the
                                             cap, accept (top + alternate)
                                             marks identified with the include
                                             default preserved, manual id/URL
                                             entry + parse, a failed search
                                             leaves the row unresolved, cancel
                                             stops the queue, resolved rows
                                             never search, the terms
                                             normalization;
                                             LoadOrderXamlTests: the fifth Add
                                             flyout item, the card hosted below
                                             the import card, the shared
                                             header+row column layout with the
                                             identification cells activating
                                             inside the fixed columns, the
                                             candidate workspace + expand
                                             affordance) + the derived
                                             version-unknown
                                             row state (ModItemVersionUnknownTests:
                                             the truth + its negatives, the badge's
                                             no-dangling-separator guard, the
                                             update-action enable + every tooltip
                                             variant with gaming precedence, the pin
                                             suppression, the edit action's linked/
                                             morphed suppression; the filter tests
                                             cover unknown rows kept under
                                             updates-only); Settings/escape-hatch browse gating
                                            with manual submission preserved; the
                                            PreferencesService theme mapping
                                            ResolveThemeVariant + the stored-System
                                            guarantee under gaming)
                                             + the ModRowContext (premium/gaming
                                             flips re-fire exactly the row + list
                                             properties the per-flag pushes re-fired,
                                             dropped rows
                                             receive no notifications, the premium-read
                                             failure stays false) + the ShellModalQueue
                                            (run-once after the drain, newest-wins per
                                            owner, independent owners in enqueue order,
                                            other destinations stay queued, a thrown
                                            modal is consumed)
                                            + the OnboardingService (already complete no-op,
                                            Continue persists + skips Integrations, Set up Nexus
                                            persists before navigating to Integrations once, close
                                            == Continue, the in-process one-shot guard)
                                            + the DetailedModRowsViewModel (persisted Compact/
                                            Detailed density selection + normalization, the
                                            generation-based stale-result protection across
                                            profile switch/Compact toggle/superseding reload, the
                                            backfill-driven thumbnail hydration, adult-content
                                            thumbnail skip) + the ModThumbnailService (HTTPS-only
                                            validation, lowercase SHA-256 cache key, 8 MiB cap,
                                            atomic sibling-temp move, four-slot load bound, same-
                                            URL coalescing with per-caller cancellation, corrupt-
                                            disk retry once, app-lifetime in-memory image cache,
                                            90-day prune), against in-memory fakes)
                                            + the WindowGeometryTracker (the pure
                                            geometry policies + the state machine fed
                                            headless through the post seam: deferred/
                                            coalesced applies, Layout never
                                            authoritative, the end-to-end #19431
                                            correction with no recursion, the
                                            close path; MainWindowStateTests keeps the
                                            window constants + the screen-conversion
                                            seam) + the LocalizedViewModelRegistrationTests
                                            (the source scan: every localized property
                                            getter must be in its VM's registered
                                            refresh list, and every class with
                                            localized getters must be in the known VM
                                            set)
    Modificus.Curator.Nxm.Tests/             xUnit tests for the nxm library (parser, framing,
                                            IPC server resilience, SingleInstanceGuard
                                            (incl. SingleInstanceGuardTests: the
                                            IsAnotherInstanceRunning query's true/false
                                            answers over the injected enumeration + the
                                            name/pid pass-through), router,
                                            relay helper (incl. the cold-start pre-check
                                            split: a reported running Curator skips the
                                            launch, prints the waiting stderr line, still
                                            delivers or times out without launching),
                                            standalone + AppImage Linux registrar
                                            (incl. the child-env sanitizer dropping exactly
                                            LD_PRELOAD), owned-registration
                                            maintenance, the Windows registrar's self-guarded
                                            unregister (absent no-op / foreign preserved / own
                                            deleted, via the base-key seam over a temp subkey;
                                            Windows-gated), AddNxm wiring;
                                            serialized via DisableTestParallelization since
                                            real named pipes are an OS-level shared resource)
docs/               architecture/ + reference/ (src/ per-library API refs + the release strategy reference)
scripts/            release.env: the install manifest (standalone RELEASE_URL /
                    PRE_RELEASE_URL plus APPIMAGE_RELEASE_URL /
                    APPIMAGE_PRE_RELEASE_URL; Windows is not tracked here), written by the
                    release workflow's update-manifest job; install.sh: the recommended
                    self-contained AppImage installer (stable/prerelease manifest selection,
                    structural extraction validation, atomic replacement, desktop entry + icon,
                    same command symlink, no root, preserves standalone + shared data);
                    install-standalone.sh: the standalone tarball installer
                    served from raw/main (stable by default, prerelease opt-in via
                    --prerelease or CURATOR_PRERELEASE=1; resolves the archive from
                    scripts/release.env rather than querying the GitHub API; installs
                    into ${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/;
                    replaces only app/ + relay/, never the user-data root; symlinks the
                    UI into ~/.local/bin/modificus-curator);
                    uninstall.sh: the default per-user AppImage uninstaller
                    (default removes AppImage/integration + Velopack pending/cache state while
                    preserving user data + standalone; explicit --purge-data removes the whole
                    strictly-validated Linux Curator data root);
                    uninstall-standalone.sh: the per-user standalone uninstaller
                    (default removes standalone app/ + relay/ + the exact standalone
                    command link + the exact standalone NXM desktop while preserving
                    user data + the AppImage distribution + Velopack state; explicit
                    --purge-data mirrors uninstall.sh --purge-data so either
                     purge is a complete Linux removal); build-appimage.sh:
                     the local AppImage builder mirroring the release
                     build-linux recipe (output under the gitignored publish/
                     root; VERSION / RELAY_ZIP / PUBLISH_DIR overrides;
                     requires mksquashfs for the vpk pack); tests/ contains
                     the isolated
                    test-install.sh, test-uninstall.sh, and
                    test-uninstall-standalone.sh harnesses. Testing overrides:
                    INSTALL_ROOT / BIN_LINK / CURATOR_REPO / CURATOR_ARCHIVE (local tar.gz
                    in place of the download, for offline extraction tests) /
                    CURATOR_APPIMAGE (local AppImage) / VELOPACK_STATE_DIR.
.github/workflows/  curator-build (the PR gate: an Ubuntu-only format job
                     checks out the PR head branch, runs `dotnet format`,
                     and auto-commits with `[skip ci]` on pull requests (format runs
                     without committing on workflow_dispatch); build + test on a
                     Windows/Ubuntu matrix and a separate Ubuntu 22.04
                     AppImage publish/pack/extract/feed/syntax-check/installer/uninstaller
                     smoke (shell syntax checks on all four production Linux scripts
                     install.sh, install-standalone.sh, uninstall.sh,
                     uninstall-standalone.sh; runs the AppImage installer + AppImage
                     uninstaller + standalone uninstaller harnesses; also asserts the
                     Velopack-generated internal desktop file carries
                     StartupWMClass=ModifAmorphic.ModificusCurator) depend on the format
                     job; no artifact upload; release-please-only PRs are ignored via
                     paths-ignore; runs on PRs into any branch; there is intentionally
                     no push trigger),
                    release (release-please cuts the release; each platform job resolves
                    the newest non-draft stable Relay release and downloads its
                    Windows x64
                    asset, then per-target jobs publish unsigned assets that diverge by
                    platform: build-windows produces two
                    Windows artifacts: (1) the Velopack installer from the Curator UI
                    published with -p:CuratorUseVelopack=true (adds the Velopack reference
                    + the CURATOR_VELOPACK symbol that wires VelopackApp.Build().Run()
                    in Program.cs), stages Relay app-local under stage/app/relay, runs
                    vpk pack (Velopack 1.2.0, packId ModifAmorphic.ModificusCurator,
                    --framework net10.0-x64-runtime so the installer bootstraps .NET 10),
                    renames Setup.exe to modificus-curator-setup.exe, uploads the
                    installer + the full.nupkg + releases.win.json, and attests the
                    installer + the nupkg; (2) the portable ZIP from the Curator UI
                    published without CuratorUseVelopack (framework-dependent, uses
                    NoopAppUpdateService, no in-app self-update), the NXM handler
                    (native-AOT win-x64), and Relay staged under relay/ at the top
                    level, creating curator-<tag>-windows-x64.zip with app/ + relay/
                    roots via PowerShell Compress-Archive, uploading + attesting it;
                    build-linux publishes two permanent distributions on ubuntu-22.04:
                    (1) the existing framework-dependent curator-<tag>-linux-x64.tar.gz
                    with a top-level app/ + relay/ layout; (2) a self-contained Velopack
                    AppImage from the Curator UI published with CuratorUseVelopack=true,
                    the native-AOT handler + Relay app-local, packed with vpk 1.2.0 on
                    channel/runtime linux-x64; the generated AppImage is renamed to
                    ModificusCurator-linux-x64.AppImage for the public asset while the
                    ModifAmorphic.ModificusCurator pack/nupkg identity stays unchanged,
                    yielding the AppImage, full nupkg, optional
                    delta, and releases.linux-x64.json; it seeds the newest prior feed +
                    full package across stable/prerelease releases for delta generation,
                    uploads only current assets, and attests the AppImage/nupkgs; portable
                    legs target win-x64 / linux-x64 RIDs with --self-contained false, and an
                    AfterTargets=Publish target strips all .pdb files; then
                    repository_dispatch the post-release workflow; an update-manifest
                    job (after build-linux, gated on releases_created + build-linux
                    success) rewrites the matching standalone + AppImage vars in
                    scripts/release.env (stable or prerelease selected by the release flag;
                    tarball resolved independently by content type, AppImage by exact name) and
                    commits it as "chore(release): update install manifest [skip ci]"),
                    and curator-post-release-av (repository_dispatch event_type
                    curator-release-assets-published, or manual workflow_dispatch;
                    scans the published Windows installer bytes
                    (modificus-curator-setup.exe) with PowerShell Start-MpScan Defender
                    scan and VirusTotal, classifies Defender results explicitly as
                    clean/detection/tool_error, submits to VirusTotal via the pinned
                    crazy-max/ghaction-virustotal@936d8c5c00afe97d3d9a1af26d017cfdf26800a2
                    action with request_rate 4, requires VIRUSTOTAL_API_KEY,
                    fails on Defender tool errors, missing Defender, VT errors, or
                    missing VT key, creates a GitHub issue (labeled
                    `virus-scan`) with title "AV manual review
                    for release <tag>" when VT upload succeeds and returns analysis
                    links; deduplicates against existing open issues with the same
                    title; still post-release and non-gating for publication, but red
                    means scan signal invalid or VT upload failed)
.release-please-config.json   release-please config (release-type simple, include-component-in-tag false, prerelease false; flip prerelease to true to cut prerelease-marked releases again)
.release-please-manifest.json release-please version manifest (the source-of-truth version; no csproj Version metadata)
.gitignore          ignores .NET bin/obj, build artifacts, _local/
```
## Modificus Curator ops

Build + test the mod-manager app -- run from the repo root (.NET 10 SDK required):
```sh
dotnet build src/modificus-curator.sln --configuration Release
dotnet test  src/modificus-curator.sln --configuration Release
dotnet run   --project src/ui --configuration Release   # app shell window
```
- Local test AppImage from any branch, mirroring the release build:
  `sh scripts/build-appimage.sh`.
- The composition root is `src/ui/CuratorComposition.cs` (loads
  config → builds the Serilog logger → wires every `Add<Library>()` → runs the
  startup `ModCleanup.PruneUnreferenced` pass + an ordinary startup
  `ISteamService.Discover()` pass, so automatic mode re-runs the platform
  discoverer and replaces the active-platform snapshot up front). The Avalonia
  `AppBuilder` is built in `src/ui/Program.cs`, which binds an explicit
  `X11PlatformOptions.WmClass = "ModifAmorphic.ModificusCurator"` (via
  `DesktopIdentityOptions`) so the running window's WM_CLASS matches the Velopack
  pack id and the AppImage / installed desktop entries' StartupWMClass.
- **Config** is `CuratorConfig` (`src/config/`) -- defaults under the
  OS local-app-data dir; loaded live from JSON by `general/ConfigLoader.cs`
  (consumers inject `IConfigLoader` and re-read per op, so runtime config
  changes via the Settings destination take effect immediately; #31). Missing
  file/dir → defaults (first-run safe).
- **Logging** is Serilog (console + file) bridged into
  `Microsoft.Extensions.Logging`; honors `Logging:Level` + `Logging:LogFile`.
  Day-rolling log file (Serilog `RollingInterval.Day` writes
  `curator-<yyyyMMdd>.log`, appended across starts within a day, rolled at
  midnight, pruned to `Logging:RetainedLogFileCount` default 5; Serilog owns
  the day-naming, midnight rolling, and pruning). Relay has its own parallel
  `Logging:RelayLogFile` stem (defaults to `relay-<yyyyMMdd>.log` next to
  Curator's); relay-client inserts the day stamp before the extension, resolves
  and prunes it to the same retained count at launch, and passes it as
  `--log-file`.
- The backend libraries are all implemented: **Profiles** (profile data model +
  lifecycle; container-based staging, where `PrepareModRoot` discovers each
  enabled mod's base folder name inside the resolved version folder via
  `IModRepository` + staging links (an NTFS junction on Windows, a symlink on
  Linux) `staged/mods/<baseName>` -> `<versionFolder>/<baseName>/`,
  then writes `mods.lst`; the base name, not the container's display name, is the
  link + mods.lst name; no per-profile mod files) + the import-time base-name
  collision hard-block (`GetBaseNameCollision`; two same-folder mods can't
  coexist in a profile) + per-profile launch settings
  (atomic `CreateProfile(name, description, launchSettings)` +
  `UpdateProfile(id, name, description, launchSettings)`: the editable write
  boundary; ordered env-var entries + game args; validated up front via the
  shared `LaunchSettingsValidator`, applied at launch; `GetLaunchSettings` is the
  focused read the launch path uses),
  **Steam** (Steam + Darktide + Proton discovery via Steam's CompatToolMapping with the appinfo recommended-runtime fallback + the automatic/manual mode policy + `Rediscover` + `IsGameRunning`),
  **Integrations** (the Nexus v1 client/auth +
  `IModAcquisitionService` the download + extract + place orchestrator +
  `IUpdateCheckService` the Nexus-only update-check service +
  `INexusModMetadataService` the stable-v1 missing-only display-metadata backfill),
  **Relay-client** (the launch
  façade, reading per-profile launch settings + threading env vars + game args
  through the platform strategies; no version preflight), **Mods** (the unified `IModRepository`: UUID containers per
  (source, identity), opaque-ID version subfolders, per-container
  `container.json` manifests, in-memory index rebuilt from a scan,
  `PruneUnreferenced` GC; the version-policy model `ModVersionPolicy`; the
  mod-source provenance model `ModSource`
  (`UntrackedSource`/`NexusSource`/`LinkedSource`) + `ModSourceParser`; the
  source-agnostic `ModDisplayMetadata` model (summary + thumbnail URL + adult
  flag); the local-import service `IModImportService`). **General** carries cross-cutting
  infra: logging, `ConfigLoader`, and `AppStateStore` (the active-profile id +
  last update-check timestamp + manual-refresh throttle window + last Nexus
  display-metadata backfill timestamp + the main window's persisted geometry
  as the atomic `AppWindowState` record, persisted to `app-state.json`). The UI includes the shell + profile
  management (with an `IProfileSession` (ui/) as the single authority for the
  active profile, the switch-block gate, and the live running-state, plus a
  session-scoped `HasPendingChanges` flag the mod-list edits set and Launch
  clears, surfaced as a yellow "changes pending" status dot while the game
  runs), global
   Preferences + i18n infrastructure, the mod-list UI (view mods with
   source/version badges, enable/disable, remove-with-confirm, reorder (drag the
   per-row grip at the left edge, or Move Up / Move Down buttons;
   profile-scoped per-row order locks keep a row's exact zero-based position
   across any reorder, toggled by a lock button beside Move Up /
   Move Down; the grip is the only surface that initiates a drag so the rest of
   every row stays touch-scrolling surface), per-mod
   Latest/Pinned policy, local folder/archive import
   via file picker + drag-and-drop, and linking an external mod folder without
   copying it, joined to containers via `IModRepository` by
   `ContainerId`; Nexus downloads (nxm links, premium update installs,
   automatic updates, the DMF prompt) all run through one serial download
   queue (`IModDownloadQueue`, ui/Session/) whose items render as rows in the
   mod list (in place on the target row, appended below for new mods) with
   cancel/retry inline; a persisted Compact/Detailed row density with cached
   thumbnails + a stable-v1 display-metadata backfill, owned by the
    `DetailedModRowsViewModel` child + the UI-layer `IModThumbnailService`), and Launch (`LaunchCommand` -> `IRelayLaunchService.Launch`
   -> branch on `LaunchResult.Status` (`Launched` -> an immediate
   `IsGameRunning` refresh (the session's `Refresh`) so the running indicator +
   launch/switch gates react at once, and clears `HasPendingChanges` since the
   successful stage re-staged the profile; `DiscoveryIncomplete` -> the focused discovery
   escape-hatch modal over the shared `DiscoveryField` descriptor; `GameDirConflict`
   -> the two-choice game-dir conflict modal (`ShowGameDirConflictAsync`,
   the UnsavedChangesDialog pattern incl. EscapeClosesBehavior, Cancel the enum
   default so ESC/X/close abort): Rename performs the consented
   `IGameDirModsHost.TakeOver(result.GameDirPath)` (returning the renamed
   path), shows the one-line rename notice carrying it, then retries the
   launch once; Cancel aborts; a
   second conflict in the same attempt chain surfaces the standard error alert
   (no loop; a takeover failure surfaces an alert with no retry; the
   launch-attempt overlay state holds through the modal + notice + retry
   exactly like the failure dialogs); `StagingFailed`
   -> a localized modal alert whose body appends the raised staging exception's
   message (a runtime/OS error) to the localized framing; `Error` -> modal alert) + a Settings destination editing `CuratorConfig.Discovery` (the global
  `OverrideAutomaticDiscovery` mode + Discover button over the shared
  `DiscoveryField` descriptor; automatic mode keeps the rows read-only with the
  discoverer owning the snapshot, manual mode makes them editable + validates the
  stored paths as-is; turning override off persists false + runs an ordinary
  `ISteamService.Discover` (automatic) + refreshes the rows, turning it on
  persists true + enables editing; the Discover button forces
                          `ISteamService.Rediscover` in either mode, replacing the
                          snapshot without changing the mode; the Browse buttons seed the picker at
                          the row's current value via `SuggestedStartLocation`) with a Storage section
                          of two buttons that open the OS file manager at the Curator data root +
                          profiles root, over the `DiscoveryConfig` +
  `SteamService.Discover()`/`Rediscover()` automatic/manual mode policy). The DMF (Darktide
  Mod Framework) install-prompt coordinator `DmfPromptService` (ui/Session/)
  offers to add/download DMF every new profile that becomes active without DMF
  in it; the prompt is enqueued on the shell-owned modal queue at the
  ProfileCreated event + runs as the topmost modal on the next real navigation
  into Mods (the coordinator's drained delegate reloads the list itself). The
  first-run `OnboardingService` (ui/Session/) owns the one-time Nexus setup
  offer: it shows the `WelcomeWindow` (ui/Views/) once on first startup
  (persisted via `IOnboardingState.OnboardingCompleted`), and on a "Set up Nexus"
  choice persists completion first, then navigates the shell to Nexus
  Integrations through `IShellNavigation` (ui/Session/, implemented by
  ShellViewModel + forwarded by the composition root; wired from `App` after
  the main window opens, exception-safe). See
  `docs/architecture/MODIFICUS-CURATOR.md`.

## Key docs

- `STEAMDECK.md` (root) -- the user-facing Steam Deck setup and SteamOS Gaming
  Mode workflow guide.
- `docs/architecture/` -- the Modificus Curator architecture (component model,
  the Relay contract Curator consumes, profiles, launch).
- `docs/reference/` -- per-library API reference for the Modificus
  Curator backend libraries.
- [darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay) --
  Mod Relay (architecture, build, game-binary reference, mod
  loader).

## Conventions

- **Conventional Commits** (`type(scope): subject`); commit freely on feature
  branches. Branch + PR flow; no unreviewed merges to `main`.
- Don't commit secrets, the game binary, or anything under `_local/`.
- **Label GitHub issues.** Every issue carries at least one fitting label:
  `enhancement` (new-capability asks), `bug` (something broken),
  `documentation`, plus the specific ones where they apply (`steam-deck` for
  Steam Deck / SteamOS Gaming Mode compatibility, `virus-scan` for the
  post-release AV manual-review issue).
- **Do not trust training data for framework/library version-specific APIs.** The
  project uses Avalonia 12.x + .NET 10, which postdate the model's training data.
  Before deciding an approach or delegating UI/framework work: determine the exact
  version in use, assess whether you are current on it, and if not, READ THE CURRENT
  DOCS (e.g. docs.avaloniaui.net) before proposing or implementing. Stale knowledge
  has bitten this project (the WPF-era `SizeToContent` toggle, `NoChrome`, and
  `CanMinimize` hiding were all wrong for Avalonia 12.x).
- **Discuss non-trivial or hacky UI/approach decisions before implementing.** Do not
  delegate or commit a workaround without surfacing it first.
- **Do not commit a change as a "fix" before the operator verifies it.** Leave fixes
  uncommitted (or clearly WIP/pending) until the operator confirms; they test on
  their own machine.
- **Be consultative on UI.** Propose UI approaches and discuss, especially
  non-obvious ones, rather than implementing unilaterally. The operator is the UI
  authority.
- **UI icons + decorative markers are drawn geometry, not Unicode glyphs.** In the
  Avalonia UI, icons are `<Path Data="…">` (standard Material/Fluent-style path
  data, dependency-free, themed via foreground) and dots/markers are `<Ellipse>`,
  never `✏`/`🗑`/`⚙`/`●` symbol/emoji glyphs (which render unreliably across
  fonts/platforms). Scoped to icons/markers; prose punctuation is covered by the
  writing convention below.
- **No em-dashes in prose** (code comments, docs, commits, chat). Em-dashes read
  as an AI-generated tell; use a comma, colon, parentheses, semicolon, or period
  instead.
- **No `ConfigureAwait(false)` in UI-layer code.** It hops async continuations
  to the threadpool, breaking UI-thread affinity for `Window.ShowDialog`,
  `ObservableCollection` mutations, and `INotifyPropertyChanged` setters. The UI
  layer's convention is to stay on the captured UI context (no
  `ConfigureAwait(false)`). Only explicit background-task code uses it (e.g.
  `UpdateCheckRunner` inside a `Task.Run`), and only inside that block. This has
  bitten the project repeatedly (the Update command, LoadPremiumStateAsync, the
  CheckCompleted handler, and the DmfPromptService all shipped with it + had to
  be caught at review).
- **PR descriptions describe ONLY what was done.** Never include an "Out of
  scope" section or any list of things the PR did not do. A PR description is a
  record of the change that landed, not a contrast against everything that could
  have; listing non-actions is noise that does not help a reviewer evaluate the
  diff. State what changed + why; stop there.
- **Don't surface implementation-detail questions to the operator without
  context.** When asking the operator to weigh in, lead with the actual problem
  in plain terms (what user-visible or operational behavior is at stake), not a
  narrow implementation detail. Decide internal plumbing yourself (anything
  where the options give identical UX) and flag it in your report; do not ask.
  Reserve surfaced questions for genuine forks: irreversible choices, UI design
  shape, external dependencies, or trade-offs with real user-facing
  consequences. Test: if you cannot write a one-sentence, non-plumbing "why I am
  asking the operator this specific question," do not ask it.
- **AGENTS.md (this file) tweaks ride in the current PR.** Convention
  fine-tuning does not need its own docs PR; update AGENTS.md as part of
  whatever work is in flight.

## Naming convention

Keep the established thematic name, **Curator** (the app), for user-facing UI
surfaces (Modificus Curator). Use plain, descriptive names for code components
(libraries, modules, types, functions). Reserve Warhammer 40k / Adeptus
Mechanicus flavor for the UI; docs and code read as plain engineering
documentation.

- **Folders/filenames:** lowercase.
- **Prose/docs:** "Modificus Curator" is the app's public name; "Mod Relay"
  / "Relay" refers to the separate runtime repo
  ([darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay)).
- Don't obscure: names should be descriptive and accessible, not cryptic.

## README pattern

Docs follow a two-tier README pattern:

- **Root `README.md`** -- audience is the **general / end user**: what Curator is,
  its components, and how to get it running. **No build internals.**
- **Component-dir `README.md`** (e.g. `src/README.md`) -- audience is
  **developers / power users**: build instructions, sub-component details,
  testing, links to the architecture specs.

The **root README links to** the component READMEs -- it does **not** duplicate
their content. When a component gets (or changes) a README, ensure the root
links to it and that the split holds (user-facing up top, dev detail under the
component).

## Before opening a PR: keep docs current

Docs must reflect the code in the PR. Before opening a PR for any change that
affects repo structure, build, architecture, or ops, update:
- **`AGENTS.md`** (this file): directory structure, ops, architecture pointers,
  to reflect the change.
- **`README.md`** (root): if the **user-facing** structure/status changed.
  Keep it user-facing (see [README pattern](#readme-pattern)); dev/build detail
  goes in the relevant component README, and the root must link to it.
- **Component-dir `README.md`** (e.g. `src/README.md`): for
  build/dev detail under that component; ensure the root links to it.
- **`docs/architecture/`** for any architecture change.
- **`docs/reference/`**: per-library API reference. When a Modificus
  Curator library's public surface, key types, or DI registration changes,
  update its `docs/reference/<library>.md` in the same PR.

Then ensure the Modificus Curator build + tests pass
(`dotnet build`/`dotnet test src/modificus-curator.sln`). **Outdated
docs in a PR are a review blocker**, including this file.

**No project phase/stage labels in committed docs or code comments.** Docs,
reference + architecture material, and code comments describe the current system
as it is, not how or when it got built. Do not write things like
"(Phase 4 Stage 4)", "new in Phase 3", or "Stage 5 adds..." in any committed
prose or comment. Those are project-management milestones, meaningless to a
reader of the current state and quickly stale to us too once the phase ships.
Describe the feature/architecture directly. If a phasing concept is genuinely
architectural (e.g. a phase *of a process* the code performs, like "the
discovery phase of launch"), that's fine; what's not fine is referencing the
build's project phases/stages. Planning history lives in `_local/` + the git
log, not in the docs or the code.
