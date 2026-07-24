# AGENTS.md -- Modificus Curator

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans -- the human-facing entry point is `README.md`.

## What this is

**Modificus Curator** is the mod manager for Warhammer 40,000:
Darktide (.NET 10 + Avalonia 12). The app is user-usable. It launches the game
modded via
[Mod Relay](https://github.com/ModifAmorphic/darktide-mod-relay) (DLL
injection: no game-directory footprint, no bundle-database patching; the runtime
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

- **`main`** -- production. Modificus Curator includes the app shell + profile
  management, global Preferences + i18n, the mod-list UI + local import, the
  Launch flow + Settings window + discovery escape-hatch, the nxm:// scheme
  handler, Nexus auth + Integrations dialog, mod acquisition, the update-check
  service, the mod-list update UI, the DMF new-profile install prompt, the
  first-run Welcome onboarding, and in-app self-update for the Windows
  installer plus Linux AppImage (Velopack).
  The app is user-usable:
  create profiles, import mods (folder/archive, Nexus/Untracked) or link an
  external mod folder without copying it, manage
  the mod list (enable/disable/reorder/policy/remove), configure Settings
  (discovery paths + mod-repo location), and launch modded Darktide. Every
  Nexus Latest row shows a stable update-action button (disabled + neutral when
  no update, enabled + accent when flagged); a Premium click installs in-app,
  a regular/unknown click opens the mod's Nexus files page. Premium users can
  additionally opt into automatic flagged-update installation after each check.
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
                          (shell + profile management: dropdown switch,
                          persisted active profile, create/rename/delete dialog;
                          global Preferences (theme + font scale + language)
                          via `IPreferencesService` + the i18n infrastructure: `Strings.resx`
                          + `LocalizationService` for dynamic culture switching;
                          the mod-list UI;
                          per-profile launch settings (the
                          `LaunchSettingsViewModel`/`LaunchSettingsWindow` modal
                          opened from a per-row drawn tune action in Manage
                          Profiles, env-var + game-arg rows with inline localized
                          validation; `ShowLaunchSettingsAsync` over
                          `IDialogService`; editing unlocked while Darktide runs);
                          Launch wiring + Settings window +
                          discovery escape-hatch over the shared `Settings/DiscoveryField`
                          descriptor + `DiscoveryConfig`/`SteamService.Discover()` validate+heal+persist
                          (the Settings Browse buttons seed the picker at the row's current
                          value via `SuggestedStartLocation`; the Storage section has two
                          buttons that open the OS file manager at the Curator data root +
                          profiles root paths);
                          `AddNxm()` + `StartNxmServer` (single-instance via
                          `SingleInstanceGuard` process enumeration, separate from the `Modificus.Curator.Nxm`
                          pipe bind which degrades gracefully on IOException; a second Curator exits
                          via `NxmSingleInstanceException` -> `Environment.Exit(1)` before the
                          window shows);
                          the Integrations dialog (Nexus-only) + its
                          `OpenIntegrationsCommand` on the shell (left of the profiles button),
                          wired through `IDialogService.ShowIntegrationsAsync` -> `IntegrationsViewModel`
                          -> `INexusAuthService` (OAuth loopback + API-key validate + sign-out); auth
                          controls stay usable while Darktide runs (only launch + active-profile
                          changes are blocked); the Integrations dialog also owns the explicit
                          `nxm://` handler registration (a "Nexus download links" section over
                          `INxmHandlerRegistrar`: register confirms first since it is a system-wide
                          change that can affect other mod managers; unregister only releases
                          Curator's own registration);
                          `IModAcquisitionService` (download + extract + place
                          orchestrator in Integrations) + the real `NxmModDownloadHandler` (in UI,
                          coordinating IDialogService + IProfileSession + Dispatcher.UIThread) that
                          replaces the no-op default via DI last-registration-wins, registered after
                          AddNxm() in CuratorComposition;
                          `UpdateCheckRunner` (ui/Session/) the
                          UI-layer glue that fires `IUpdateCheckService.CheckAsync`
                          fire-and-forget on the three automatic triggers
                          (startup-with-restored-id + active-profile switch via
                          IProfileSession.PropertyChanged filtered to
                          ActiveProfileId + a periodic timer), all interval-gated
                          via a shared last-check persisted to
                          `IAppStateStore.LastUpdateCheckUtc` (so a close/reopen
                          loop does not fire a call per launch); the
                          `AutoUpdateCheckEnabled` toggle gates ONLY the periodic
                          timer, and the manual `CheckNowAsync` carries its own
                          sliding-window throttle (10 free/hour then 1/2min,
                          independent of the interval gate); registered + started
                          best-effort from CuratorComposition);
                          the mod-list update UI per-row update
                          signal + per-mod update action. `ModListViewModel` subscribes
                          to `IUpdateCheckService.CheckCompleted` and reads the
                          profile-scoped `IUpdateStateStore` (persisted in
                          `IAppStateStore.KnownUpdates` / app-state.json, so a
                          restart inside the interval gate shows prior flags
                          before any API call) for per-row `UpdateAvailable`
                          (matched by ContainerId) + the list-level `IsRateLimited`
                          notice (the latter still from the in-memory LastResult,
                          session-only), reads
                          `INexusAuthService.GetCurrentStateAsync` once at construction
                          for the per-row premium behavior (`IsPremiumUser` pushed
                          down to rows; no mid-session refresh),
                          and exposes an async `UpdateCommand(row)` that branches on
                          premium: Premium acquires the global `UpdateCoordinator`
                          (one install at a time, shared with the automatic updater)
                          then calls
                          `IModAcquisitionService.AcquireLatestNexusAsync` +
                          `AcknowledgeUpdateAndReload` (clears the persisted
                          known-update entry, no extra API check); regular/unknown
                          opens the mod's Nexus files page via a testable
                          external-launcher seam (fallback alert on failure).
                          `CheckForUpdatesNowCommand` awaits the runner's
                          thorough check (driving an `IsCheckingNow` spinner on
                          the header refresh button; the await now also covers the
                          chained automatic-update batch) and drives the manual
                          sliding-window throttle's countdown tooltip + disabled
                          button via the runner's `NextManualRefreshAllowedAt`).
                          The view's source badge
                          is a `HyperlinkButton` to the mod's remote page; the
                          stable update-action cell is a fixed-width `Panel`
                          reserved on every row holding a drawn download-arrow
                          button + indeterminate `ProgressBar` (toggled by
                          `IsUpdating`). The button shows for Nexus + Latest rows
                          regardless of tier (disabled + neutral when no update,
                          enabled + accent-blue arrow when flagged); Pinned/
                          Untracked rows keep the reserved cell but no button. The
                          rate-limit notice sits in the header. The Add split
                          button gains a third flyout item, "Link external folder"
                          (folder picker, no modal); `LinkModsCommand` peeks the
                          base name, runs the collision check (excluding a re-link),
                          then `LinkFolder` + `AddMod(LatestPolicy)`. A linked row's
                          badge cell is a two-state indicator: available shows an
                          "External" pill (`OpenFolderCommand` opens the OS file
                          manager at the external folder), broken shows a
                          non-clickable "Folder unavailable" text in the same cell
                          (caution brush; `IsExternalBroken` pushed from
                          `IsExternalAvailable` at Reload). The policy ComboBox is
                          disabled for linked rows + the update-action cell stays
                          empty (space preserved). `ModItemViewModel`
                          carries the INPC state + derived `SourceUrl`/`UpdatePageUrl`/
                          `IsNexusLatest`/`CanShowUpdateAction`/
                          `UpdateActionEnabled`/`UpdateActionTooltip`/`NexusModId`;
                          `IsPremiumUser` + `AnyRowUpdating` are pushed down so the
                          per-row enabled state + tooltip recompute without a parent
                          walk. The `UpdateCoordinator` (ui/Session/) is the
                          single one-install-at-a-time gate shared with the
                          `IAutomaticUpdateService` (ui/Session/), the opt-in
                          Premium automatic installer chained after each check from
                          `UpdateCheckRunner` (captures the exact result, gates on
                          authoritative Success + updates + AutomaticUpdatesEnabled
                          + active profile + a fresh Premium verify, installs
                          sequentially under the coordinator, isolates per-mod
                          failures into one summary alert, acknowledges on success,
                          stops on profile switch, raises UpdatesApplied so the list
                          VM reloads, raises ModUpdateProgress per mod so the
                          row-level spinner tracks the currently installing row).                           The check is split by trigger:
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
                          repo, prereleases included; a set value (a local dir or
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
                          + Velopack relaunches) + the Settings "Updates"
                          section (current version + Check for Updates +
                          startup-check toggle + inline result + Download and
                          Restart); the
                          `IAppUpdateService.UpdateStateChanged` event fires on a
                          threadpool thread and the shell/Settings handlers
                          marshal to the UI thread via the shared `Action<Action>`
                          seam;
                          the DMF (Darktide Mod Framework)
                          install-prompt coordinator `DmfPromptService`
                          (ui/Session/) + the modal `ProgressDialog`
                          (ui/Views/) used for its in-flight download. The
                          coordinator subscribes to
                          `IProfileService.ProfileCreated` (fires from inside
                          the ManageProfiles dialog's create), records it as a
                          pending trigger, and the shell calls
                          `ProcessPendingAsync` after that dialog closes so the
                          DMF prompt is the topmost modal at that point (no
                          dialog-on-dialog). The prompt fires for one trigger
                          when DMF is not in the active profile: every new
                          profile that becomes active (no persisted flag: a
                          fresh ask per profile). Two cases: DMF in the repo
                          but not the profile -> instant add (case 1); DMF not
                          in the repo -> a download confirm (the message
                          tailors to whether Curator owns the `nxm://` handler:
                          manager-download vs. manual-import guidance); on
                          confirm, premium users get the in-app API download
                          under a spinner + add, while everyone else (no auth,
                          regular, or unknown premium state) gets the DMF Nexus
                          files page opened in the browser regardless of nxm
                          setup (when Curator owns the handler, the user clicks
                          Download there + the handler picks up the URL + adds
                          DMF to the active profile via the standard nxm flow;
                          when Curator does not own it, the user downloads the
                          archive and imports it via the normal add flow; on a
                          browser-launch failure, a fallback alert carries the
                          files-page URL) (case 2).
                          Decline is respected; DMF can be added later via the
                          normal add flow. The DMF flow never opens Integrations
                          or stops at an informational dead-end. The first-run
                          `OnboardingService` (ui/Session/) owns the one-time
                          Nexus setup offer: it shows the `WelcomeWindow`
                          (ui/Views/) once on first startup (persisted via
                          `IAppStateStore.OnboardingCompleted`), and on a
                          "Set up Nexus" choice opens the shell's full
                          Integrations flow after Welcome closes (wired from
                          `App` after the main window opens, exception-safe).
                          `IDialogService.ShowProgressAsync<T>`
                          runs the supplied work under a non-closeable spinner +
                          closes it on completion; `DialogTitleBar.ShowClose`
                          (a new styled property) hides the spinner's close
                          button so the user cannot dismiss an in-flight
                          download). Modal dialogs close on ESC via the opt-in
                          attached behavior `EscapeClosesBehavior.IsEnabled`
                          (ui/Behaviors/, applied per-dialog; ESC calls
                          `Window.Close()`, the same path as the title-bar X so
                          result/cancel contracts are unchanged): applied to
                          ConfirmDialog, ImportModDialog,
                          DiscoveryEscapeHatchDialog, IntegrationsWindow,
                          ManageProfilesWindow, PreferencesWindow, SettingsWindow,
                          WelcomeWindow;
                          ProgressDialog (non-closeable) + the main window opt
                          out, so ESC never dismisses a spinner or exits the app.
                          The shell's `ManageProfiles` command
                          brackets its `Profiles = ...` swap + `SelectedProfile
                          = ResolveActive()` re-sync under `_syncing = true`:
                          replacing the dropdown's `ItemsSource` causes the
                          ComboBox to fire spurious `SelectedItem` events (null
                          then a value match against the new collection for the
                          previously-selected name) that would otherwise land in
                          `OnSelectedProfileChanged` with the stale value +
                          revert the session via `RequestActive` (undoing the
                          active change `CommitCreate` just made inside the
                          dialog). Bracketing the swap under `_syncing` makes
                          those events no-ops)
  general/              Modificus.Curator.General -- cross-cutting infra (logging bootstrap:
                        per-process datetime-named log-file rotation + retention, with the
                        resolved process-pinned path shared with Relay via --log-file,
                        config loader, app-state store (active profile id +
                        last update-check timestamp + manual-refresh throttle
                        window + profile-scoped known-update snapshots), AddGeneral() DI ext)
  config/               Modificus.Curator.Config -- the CuratorConfig schema + defaults (POCO),
                        including the NexusConfig slot under Integrations
                        (AuthMethod {None,OAuth,ApiKey}, ApiKey, OAuth tokens, base URLs,
                        AutomaticUpdatesEnabled opt-in Premium auto-install)
                        + the AppUpdatesConfig slot (CheckOnStartup, gates the
                        automatic startup self-update check)
  profiles/             Modificus.Curator.Profiles -- profile data model, persistence,
                          container-based staging (ProfileService.PrepareModRoot
                          discovers each enabled mod's base folder name inside the
                          resolved version folder + staging links (an NTFS junction
                          on Windows, a symlink on Linux) staged/mods/<baseName> ->
                          <versionFolder>/<baseName>/, then writes mods.lst; the
                          base name, not the container's display name, is the link
                          + mods.lst name; the StagingLinkCreator delegate selects
                          junction vs symlink per OS; a linked container stages
                          directly from its external folder, no version resolution) + SetModPolicy transitions + the
                        import-time base-name collision hard-block
                        (GetBaseNameCollision; two same-folder mods can't coexist
                        in a profile; resolves a linked mod's base name from the
                        external folder's own name) + per-profile launch settings
                        (EnvVar/LaunchSettings: ordered env-var entries + game
                        args + the EnableLuaLogs toggle (emits Relay's bare
                        --lua-logs flag, teeing Lua print output into the log
                        file); GetLaunchSettings/SetLaunchSettings validate at the
                        setter via the shared LaunchSettingsValidator
                        (LaunchSettingsValidationError: index + field + kind;
                        single source of truth consumed by both the service and
                        the UI) -- names non-empty/no =/no NUL, no NUL in values,
                        case-insensitive duplicate rejection, reserved-name block
                        of 13 Curator-owned OS/launch + Relay config env (adds
                        RELAY_LUA_LOGS); backward-
                        compat null/missing normalization to empty, mirroring Mods;
                        apply at launch) + the auto-sort seam
                        (IModOrderResolver/IdentityModOrderResolver, identity stub now;
                        real dependency-driven resolver later) + ModCleanup (the startup
                        prune orchestration; keeps a referenced linked container by
                        containerId sentinel, since a linked container has no versions)
  mods/          Modificus.Curator.Mods -- the unified mod repository
                        (IModRepository: UUID containers per (source, identity),
                        opaque-ID version subfolders, per-container container.json
                        manifests, in-memory index rebuilt from a scan,
                        RenameContainer (display-label rename; identity Id +
                        on-disk directory unchanged; keeps the untracked-name
                        index consistent for untracked containers), PruneUnreferenced
                        GC at startup, keeping a referenced linked container by
                        containerId sentinel) + the version-policy model (ModVersionPolicy:
                        PinnedPolicy/LatestPolicy; PinnedPolicy pins by VersionId, a foreign
                        key to ModVersion.Folder, so the repo is the sole source of truth for
                        version details) + the
                        mod-source provenance model (ModSource: UntrackedSource/
                        NexusSource/LinkedSource, the last carrying a normalized
                        ExternalPath for a no-copy external folder, + ModSourceParser
                        URL parsing) + the
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
                        RemoteUploadedAt from the re-acquired version's
                        remote-publish timestamp; LinkFolder records an external
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
                        AuthStateChanged on every persisted method change so
                        the shell's Integrations flow refreshes the nxm handler
                        status after the dialog closes; the DMF prompt is
                        profile-creation-only and does not subscribe); NexusOAuthTokenStore
                        owns the OidcClient + token persistence; LoopbackBrowser
                        the IBrowser impl with an HttpListener on an ephemeral
                        port; Duende.IdentityModel.OidcClient 7.1.0 for the
                        OAuth machinery; client_id is the build-time const
                        "modificus-curator";
                        IModAcquisitionService the download +
                        extract + place orchestrator over INexusClient +
                        IModImportService + a plain HttpClient for the CDN
                        download; AcquireFromNexusAsync resolves the download
                        links, fetches name + version metadata, downloads to
                        temp, then imports via IModImportService.Import;
                        AcquireLatestNexusAsync resolves the newest
                        non-archived MAIN file via ListModFilesAsync then forwards
                        to AcquireFromNexusAsync with null nxm tokens (premium
                        path); ModFile gains an `archived` bool for the filter;
                        IUpdateCheckService the Nexus-only
                        update-check service (1 v2 GraphQL `modsByUid` batch
                        query per check, 1 API call for all mods; computes UIDs
                        from game_id * 2^32 + mod_id, Darktide game_id = 4943;
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
                        `IAppStateStore.KnownUpdates`: Success replaces/clears,
                        NoNexusMods clears, no-auth/rate-limit/failed preserve,
                        hydration self-heals removed/pinned/source-changed/
                        version-changed entries, AcknowledgeInstall clears a
                        single entry on a successful version change);
                        LastResult + CheckCompleted event for the mod-list;
                        Integrations references Profiles, acyclic, for
                        IProfileService.GetModList)
  steam/                Modificus.Curator.Steam -- Steam + Darktide + Proton discovery
                        (multi-library + compatdata), IsGameRunning (WinProcessLookup
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
                        AppImage identity; a profile's EnableLuaLogs emits Relay's
                        bare --lua-logs flag appended after --log-file (a tee of
                        Lua print output into the log file, no value, not
                        Z:\-translated on Linux); game args append one bare -- then
                        each arg as its own ArgumentList entry (Relay's --
                        contract; no version preflight); the spawn seam IProcessLauncher takes
                        one immutable ProcessLaunchRequest with FilePath,
                        Arguments, EnvironmentOverrides, and
                        EnvironmentVariablesToRemove, applied by ProcessLauncher
                        as UseShellExecute=false + ArgumentList + remove-then-override
                        over the inherited environment; ResolveLauncherPath prefers the
                        configured RelayDir, then on both platforms falls back to the
                        app-local relay/ shipped inside a Velopack payload at
                        <BaseDirectory>/relay/, then uses the portable sibling fallback
                        on Windows only)
  launcher/             Modificus.Curator.Launcher -- stub (the Steam non-steam-shortcut
                          target placeholder)
  nxm/                  Modificus.Curator.Nxm -- the nxm:// scheme-handler plumbing:
                        NxmUrlParser (mod-download / oauth-callback /
                        collection URL types), NxmIpcFraming (length-prefixed UTF-8 frames),
                        SingleInstanceGuard (the process-enumeration single-instance check,
                        with an injectable enumerator seam), NxmIpcServer (the named-pipe
                        server; Bind runs two SEPARATE checks: SingleInstanceGuard first
                        (fatal NxmSingleInstanceException on collision), then the pipe bind
                        which degrades gracefully on IOException; accept loop Disconnects
                        between clients), INxmRouter + no-op INxmModDownloadHandler
                        default (the real handler is registered via AddSingleton
                        last-wins, in CuratorComposition after AddNxm()), the OS
                        scheme-handler registrar
                        (INxmHandlerRegistrar: WindowsNxmHandlerRegistrar writes
                        HKCU\Software\Classes\nxm; LinuxNxmHandlerRegistrar writes a .desktop
                        file + xdg-mime default; AppImage registration atomically copies the
                        handler to a durable per-user directory + creates a sibling symlink
                        to $APPIMAGE; startup maintenance refreshes those files only while
                        Curator owns the active association), + NxmHandlerRelay (the testable core the
                        handler exe calls: hot-path IPC delivery + cold-start launch+retry,
                        UseShellExecute=false on both OSes). AOT-friendly (IsAotCompatible;
                        only raw byte/UTF-8 IO in the handler path).
  nxm-handler/          Modificus.Curator.NxmHandler -- the OS-registered nxm:// scheme handler
                        (console exe, native AOT). Program.cs is one line: NxmHandlerRelay.RunAsync.
                        Forwards the raw URL to running Curator over the fixed pipe, or (cold start)
                        launches Curator (no args) + retries the pipe ~250ms/30s, then delivers.
  tests/
    Modificus.Curator.General.Tests/         xUnit tests for the general library
                                          (incl. the AppStateStore KnownUpdates round-trip +
                                          old-file-without-field compatibility)
    Modificus.Curator.Profiles.Tests/        xUnit tests for the profiles library (incl. staging
                                          + the launch-settings round-trip/normalization/validation)
    Modificus.Curator.Mods.Tests/      xUnit tests for the mod repository + import
                                        (incl. the linked-folder add + linked-container prune)
    Modificus.Curator.Integrations.Tests/    xUnit tests for the Nexus client
                                          (against a fake HttpMessageHandler),
                                          the auth factories (apikey / OAuth / None + selector),
                                          the OAuth flow scripted with a fake IBrowser + stub
                                          discovery+token endpoint (via the OidcClient backchannel
                                          seam), the LoopbackBrowser/HttpListener against an
                                          ephemeral port, the NexusConfig JSON round-trip, and the
                                          ModAcquisitionService (download + extract + place against
                                          a fake INexusClient + fake IModImportService + stub CDN)
                                          + the UpdateCheckService (Nexus-only
                                          update check against a fake INexusClient +
                                          fake IProfileService + fake IModRepository)
                                          + the UpdateStateStore (the profile-scoped
                                          known-update persistence rules: success
                                          replaces/clears, failed/no-auth/rate-limited
                                          preserve, no-Nexus-mods clears, acknowledge,
                                          + the hydration self-heal for removed/pinned/
                                          source-changed/version-changed entries)
    Modificus.Curator.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Modificus.Curator.RelayClient.Tests/ xUnit tests for the launch façade (dual-purpose:
                                            `dotnet test` = xUnit; `dotnet run` = composition smoke harness);
                                            covers RelayLaunchServiceTests (Windows + Linux arg
                                            assembly + DiscoveryIncomplete/StagingFailed/Error
                                            mapping + the Linux five-key AppImage-identity
                                            removal set + the Windows empty removals/overrides
                                            + the launch-settings merge: Linux profile env
                                            before Proton startup alongside the AppImage
                                            removals + STEAM_COMPAT_* overrides, Windows
                                            profile env as overrides, empty/legacy when no
                                            settings) + GameArgumentsTests (the bare-`--`
                                            contract via the pure BuildLauncherArgs seam),
                                            ProcessLauncherTests (the deterministic BuildStartInfo
                                            path: a requested inherited key is removed, an
                                            unrelated inherited key remains, an override is
                                            applied, an override wins after removal,
                                            UseShellExecute=false, arguments stay distinct with
                                            spaces + shell metacharacters), WinePathTests, + the
                                            AddRelayClient DI wiring
    Modificus.Curator.UI.Tests/              xUnit tests for the shell + manage-profiles
                                            view models (profile CRUD/switch, active-profile
                                            persist, switch-blocked-while-running; dialog via
                                            an injectable IDialogService seam; the per-row
                                            launch-settings action (opens for the selected row,
                                            not the active profile; unlocked while Darktide runs)
                                            + the LaunchSettingsViewModel (existing-settings
                                            load, add/remove rows, inline localized validation --
                                            empty/`=`/NUL name, NUL value, case-insensitive
                                            duplicate, reserved name -- + a Logging toggle
                                            (EnableLuaLogs emits Relay's bare --lua-logs flag);
                                            Save persists once via
                                            SetLaunchSettings + closes only on success, Cancel no
                                            change) + the
                                            NxmModDownloadHandler auth/profile gates + error
                                            wiring + the mod-list update flow: profile-scoped
                                            known-update persistence/hydration, the stable
                                            per-row update action (no-update disabled, flagged
                                            accent, Premium install, regular/unknown files-page
                                            open, launcher failure alert, unsupported rows),
                                            UpdateCommand premium/regular branches + one-at-a-time
                                            via the UpdateCoordinator + acknowledgement + the
                                            automatic-update setting + the AutomaticUpdateService
                                            gating/sequencing/isolation/concurrency/profile-switch
                                            + SourceUrl resolution; + the linked-folder flow
                                            (LinkModsCommand peek/collision-refusal/re-link +
                                            LatestPolicy add, the linked badge two-state available/
                                            broken, OpenFolderCommand launch + failure alert, the
                                            disabled policy + empty update-action cell for linked
                                            rows, IsExternalBroken on Reload);
                                            + the DmfPromptService (the two DMF
                                            cases: add existing / download + add or
                                            browser-open, the new-profile trigger, the
                                            decline path, the premium in-app download,
                                            the non-premium/unknown/no-auth browser-open
                                            regardless of the nxm registrar state, and the
                                            dialog-on-dialog avoidance)
                                            + the OnboardingService (already complete no-op,
                                            Continue persists + skips Integrations, Set up Nexus
                                            persists before opening Integrations once, close ==
                                            Continue, the in-process one-shot guard), against in-memory fakes)
    Modificus.Curator.Nxm.Tests/             xUnit tests for the nxm library (parser, framing,
                                            IPC server resilience, SingleInstanceGuard, router,
                                            relay helper, standalone + AppImage Linux registrar,
                                            owned-registration maintenance, AddNxm wiring;
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
                    purge is a complete Linux removal); tests/ contains the isolated
                    test-install.sh, test-uninstall.sh, and
                    test-uninstall-standalone.sh harnesses. Testing overrides:
                    INSTALL_ROOT / BIN_LINK / CURATOR_REPO / CURATOR_ARCHIVE (local tar.gz
                    in place of the download, for offline extraction tests) /
                    CURATOR_APPIMAGE (local AppImage) / VELOPACK_STATE_DIR.
.github/workflows/  curator-build (the PR gate: an Ubuntu-only format job
                    auto-commits `dotnet format` as `style: dotnet format [skip ci]`
                    for same-repo PRs, verify-only for fork PRs and workflow_dispatch;
                    build + test on a Windows/Ubuntu matrix and a separate Ubuntu 22.04
                    AppImage publish/pack/extract/feed/syntax-check/installer/uninstaller
                    smoke (shell syntax checks on all four production Linux scripts
                    install.sh, install-standalone.sh, uninstall.sh,
                    uninstall-standalone.sh; runs the AppImage installer + AppImage
                    uninstaller + standalone uninstaller harnesses; also asserts the
                    Velopack-generated internal desktop file carries
                    StartupWMClass=ModifAmorphic.ModificusCurator) depend on the format
                    job; no artifact upload; release-please-only PRs are ignored via
                    paths-ignore; there is intentionally no push trigger),
                    release (release-please cuts the release; each platform job resolves
                    the newest non-draft Relay prerelease and downloads its Windows x64
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
                    missing VT key, creates a GitHub issue with title "AV manual review
                    for release <tag>" when VT upload succeeds and returns analysis
                    links; deduplicates against existing open issues with the same
                    title; still post-release and non-gating for publication, but red
                    means scan signal invalid or VT upload failed)
.release-please-config.json   release-please config (release-type simple, include-component-in-tag false, prerelease true)
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
- The composition root is `src/ui/CuratorComposition.cs` (loads
  config → builds the Serilog logger → wires every `Add<Library>()` → runs the
  startup `ModCleanup.PruneUnreferenced` pass + the startup
  `ISteamService.Discover()` validate/heal/persist pass). The Avalonia
  `AppBuilder` is built in `src/ui/Program.cs`, which binds an explicit
  `X11PlatformOptions.WmClass = "ModifAmorphic.ModificusCurator"` (via
  `DesktopIdentityOptions`) so the running window's WM_CLASS matches the Velopack
  pack id and the AppImage / installed desktop entries' StartupWMClass.
- **Config** is `CuratorConfig` (`src/config/`) -- defaults under the
  OS local-app-data dir; loaded live from JSON by `general/ConfigLoader.cs`
  (consumers inject `IConfigLoader` and re-read per op, so runtime config
  changes via the Settings window take effect immediately; #31). Missing
  file/dir → defaults (first-run safe).
- **Logging** is Serilog (console + file) bridged into
  `Microsoft.Extensions.Logging`; honors `Logging:Level` + `Logging:LogFile`.
  Per-process datetime-named log-file rotation: each start resolves the
  `{DateTime}` token in `Logging:LogFile` to a timestamp, pins that path for the
  process lifetime, and prunes to `Logging:RetainedLogFileCount` (default 5). The
  resolved path is shared with Relay via `--log-file`.
- The backend libraries are all implemented: **Profiles** (profile data model +
  lifecycle; container-based staging, where `PrepareModRoot` discovers each
  enabled mod's base folder name inside the resolved version folder via
  `IModRepository` + staging links (an NTFS junction on Windows, a symlink on
  Linux) `staged/mods/<baseName>` -> `<versionFolder>/<baseName>/`,
  then writes `mods.lst`; the base name, not the container's display name, is the
  link + mods.lst name; no per-profile mod files) + the import-time base-name
  collision hard-block (`GetBaseNameCollision`; two same-folder mods can't
  coexist in a profile) + per-profile launch settings
  (`GetLaunchSettings`/`SetLaunchSettings`: ordered env-var entries + game args;
  validated at the setter, applied at launch),
  **Steam** (Steam + Darktide + Proton discovery + `IsGameRunning`),
  **Integrations** (the Nexus v1 client/auth +
  `IModAcquisitionService` the download + extract + place orchestrator +
  `IUpdateCheckService` the Nexus-only update-check service),
  **Relay-client** (the launch
  façade, reading per-profile launch settings + threading env vars + game args
  through the platform strategies; no version preflight), **Mods** (the unified `IModRepository`: UUID containers per
  (source, identity), opaque-ID version subfolders, per-container
  `container.json` manifests, in-memory index rebuilt from a scan,
  `PruneUnreferenced` GC; the version-policy model `ModVersionPolicy`; the
  mod-source provenance model `ModSource`
  (`UntrackedSource`/`NexusSource`/`LinkedSource`) + `ModSourceParser`; the
  local-import service `IModImportService`). **General** carries cross-cutting
  infra: logging, `ConfigLoader`, and `AppStateStore` (the active-profile id +
  last update-check timestamp + manual-refresh throttle window, persisted to
  `app-state.json`). The UI includes the shell + profile
  management (with an `IProfileSession` (ui/) as the single authority for the
  active profile, the switch-block gate, and the live running-state), global
  Preferences + i18n infrastructure, the mod-list UI (view mods with
  source/version badges, enable/disable, remove-with-confirm, reorder, per-mod
  Latest/Pinned policy, auto-sort identity stub, local folder/archive import
  via file picker + drag-and-drop, and linking an external mod folder without
  copying it, joined to containers via `IModRepository` by
  `ContainerId`), and Launch (`LaunchCommand` -> `IRelayLaunchService.Launch`
  -> branch on `LaunchResult.Status` (`Launched` -> an immediate
  `IsGameRunning` refresh (the session's `Refresh`) so the running indicator +
  launch/switch gates react at once; `DiscoveryIncomplete` -> the focused discovery
  escape-hatch modal over the shared `DiscoveryField` descriptor; `StagingFailed`
  -> a localized modal alert whose body appends the raised staging exception's
  message (a runtime/OS error) to the localized framing; `Error` -> modal alert) + a Settings window editing `CuratorConfig.Discovery` user
  overrides (per-field read-modify-save; the Browse buttons seed the picker at
                          the row's current value via `SuggestedStartLocation`) with a Storage section
                          of two buttons that open the OS file manager at the Curator data root +
                          profiles root, over the `DiscoveryConfig` +
  `SteamService.Discover()` validate+heal+persist pipeline). The DMF (Darktide
  Mod Framework) install-prompt coordinator `DmfPromptService` (ui/Session/)
  offers to add/download DMF every new profile that becomes active without DMF
  in it; the prompt is a modal on the main window, fired by the shell after the
  triggering ManageProfiles dialog closes so it never nests on top of one. The
  first-run `OnboardingService` (ui/Session/) owns the one-time Nexus setup
  offer: it shows the `WelcomeWindow` (ui/Views/) once on first startup
  (persisted via `IAppStateStore.OnboardingCompleted`), and on a "Set up Nexus"
  choice opens the shell's full Integrations flow after Welcome closes (wired
  from `App` after the main window opens, exception-safe). The **Launcher** is a stub. See
  `docs/architecture/MODIFICUS-CURATOR.md`.

## Key docs

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
