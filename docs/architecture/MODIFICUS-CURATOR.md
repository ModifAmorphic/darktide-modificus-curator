# Modificus Curator -- architecture

**Modificus Curator** is the user-facing mod manager app --
the second of the project's two components, sitting on top of Mod Relay.
It owns everything user-facing: profile management, mod staging, load
order, dependency resolution, mod-source integrations (Nexus
Mods, Steam), and the "Launch Darktide" button that invokes the Relay
launcher. Relay does the injection + mod loading; Modificus Curator owns the
management experience around it.

## In scope for this document

- The component's role, technology choices, and project layout.
- The domain-library breakdown and each library's responsibilities.
- The Relay contract Curator consumes (the stable surface it builds against).
- The profiles model, mod storage, mod sources, and the launch flow on Windows and Linux.
- The v1 scope cut.

## Out of scope (handled elsewhere)

- The Mod Relay internals: see the
  [darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay) repo.
- The mod loader ↔ DMF integration: darktide-mod-relay.
- The load-order file contract (`mods.lst`): Curator authors `mods.lst` and
  Relay consumes it; the contract is specified in darktide-mod-relay.

## Technology

- **C# / .NET 10 (LTS)** + **Avalonia 12** for a native cross-platform UI.
- **DI** via `Microsoft.Extensions.DependencyInjection`; **logging** via
  `Microsoft.Extensions.Logging` (structured). Mandated across all components.
- SOLID / DRY; libraries namespaced by domain.

## Project layout

All Modificus Curator projects live under `src/`, each component in
its own subfolder:

```
src/
  ui/                     the Avalonia app (UI only -- no direct data access)
  general/                cross-cutting infra (DI, logging, config, primitives)
  config/                 the global config schema + defaults
  profiles/               Profiles library -- profile data, staging, mods.lst
  mods/                   Mods library -- unified mod repository (IModRepository) + version-policy + source models
  steam/                  Steam library -- Steam/Darktide/Proton discovery + IsGameRunning
  integrations/           Integrations library -- Nexus v1 client/auth + mod acquisition + update check
  relay-client/           Relay-client library -- the launch façade
  launcher/               stub launcher -- the Steam non-steam-shortcut target placeholder
  nxm/                    Nxm library: nxm:// scheme-handler plumbing (URL parser, IPC
                          server, single-instance guard, router + handler seams, OS
                          registrar, relay helper)
  nxm-handler/            the OS-registered nxm:// scheme handler (native-AOT console
                          exe; relays the raw URL to running Curator, or cold-starts it)
  tests/                  xUnit test projects per library
```

The UI keeps domain data I/O (profile, repository, Nexus, Steam, launch)
behind backend library services. The one deliberate exception is the focused
UI-owned presentation-media service `IModThumbnailService`: it returns an
Avalonia `IImage` (a presentation type the backend libraries do not depend on)
and owns only an HTTP/disk cache for mod thumbnail images. Everything else the
UI presents flows through a registered library interface.

## Libraries (by domain)

Each library exposes interfaces namespaced by domain, used by both the UI and
other libraries. SOLID: libraries accept interfaces or primitives, not concrete
UI models.

| Library | Owns |
| --- | --- |
| **Relay** | All interaction with Mod Relay. v1 façade only: assemble launcher args, invoke, track process exit. (Live-control -- status / hot-reload / live enable-disable -- is a future Relay contract expansion; out of v1.) |
| **Profiles + Settings** | Profile data, files, directories; global/system settings (logging, profile base folder, mod repository); resolves each profile mod's version policy to a repository version folder; materializes the profile mod root + writes `mods.lst` at launch. |
| **Integrations** | External-service calls: Nexus Mods (primary user-mod source), local install. Nexus API key / OIDC, version checks, downloads / updates. |
| **Steam** | Steam operations outside Relay: locate Steam (`libraryfolders.vdf`), Darktide install + compatdata, Proton version; detect whether the game is running. Owns the Linux discovery + escape hatch (see [Launch](#launch)). |
| **General** | Cross-cutting infra: DI composition, structured logging, configuration, shared primitives. |

## Composition & startup

The composition root is `ui/CuratorComposition.cs` -- a static `Build()` that
constructs and returns the application `IServiceProvider`. Domain data I/O
(profile, repository, Nexus, Steam, launch) flows through registered library
interfaces; the UI keeps that boundary. The single deliberate exception is
`IModThumbnailService`, a focused UI-owned service that returns an Avalonia
`IImage` and owns only an HTTP/disk thumbnail cache. The UI registers only its
own surface (main window + view models + the thumbnail service) on top of the
library interfaces.

`CuratorComposition.Build()` runs this sequence, in order:

1. **Config loader**: `new ConfigLoader()`, registered as the live-read
   `IConfigLoader` singleton (one shared instance). The startup snapshot is a
   one-off `Load()` to build the logger; every consumer thereafter re-reads the
   current disk state per op via `IConfigLoader.Load()` (config is tiny; a
   startup cache would only create staleness for the Settings destination,
   which writes config at runtime; #31).
2. **Build the logger**: `LoggingBootstrap.CreateLoggerFactory(config)`
   (Serilog console + file, level-honored; day-rolling log file via
   `RollingInterval.Day`, `curator-<yyyyMMdd>.log` appended across starts within
   a day and pruned to `RetainedLogFileCount`). Both config and the logger are
   constructed **outside** DI because DI itself needs them.
3. **Compose services**: `new ServiceCollection()`, then the `Add<Library>()`
   extensions in their real order:
   - `AddSingleton<IConfigLoader>(loader)`: pre-registered before `AddGeneral`
     so the same live-read instance is shared.
   - `AddGeneral(loggerFactory)`: registers the logger factory, `AddLogging()`,
     and the runtime app-state store. `CuratorConfig` is intentionally NOT
     registered as a singleton (config is read live via `IConfigLoader`).
   - `AddMods()`: the unified mod repository + import service (called explicitly here and
     idempotently again inside `AddProfiles()`, so the store is discoverable at
     the root and `IProfileService` always resolves its staging dependency).
   - `AddProfiles()`: profile service + the `StagingLinkCreator` staging seam
     (junction on Windows, symlink on Linux).
    - `AddIntegrations()`: the typed Nexus v1
      HTTP client + the Nexus auth service + the OAuth token
      store + the loopback `IBrowser`.
    - `AddSteam()`: Steam discovery + the platform process-lookup seam.
    - `AddRelayClient()`: the launch façade + the process-launcher seam.
    - `AddLauncher()`: the launcher stub.
    - `AddSingleton<MainWindow>()`: the UI surface. Registered through an
      explicit factory that supplies `IAppStateStore` via the internal
      production constructor before the window is returned/shown; the public
      parameterless constructor stays available for Avalonia's XAML
      runtime/designer loader (see the UI architecture for the
      persisted-window-geometry contract).
4. **Build**: `BuildServiceProvider()`.
5. **Startup prune**: `ModCleanup.PruneUnreferenced` runs once (best-effort,
   logged + swallowed on failure) to drop repository versions no profile
   references + empty containers.
6. **Startup discovery**: `ISteamService.Discover()` runs once (best-effort +
   non-blocking, logged + swallowed on failure). In automatic mode (the
   default) this runs the platform discoverer and replaces the active-platform
   path snapshot, so the Settings destination shows the resolved paths rather
   than blanks. Missing fields block launch (re-checked at launch), not app
   startup.

**The DI contract:** each library exposes one `Add<Library>()` extension and
accepts only interfaces or primitives (never concrete UI models). Supporting
services and injectable seams are registered with `TryAdd` -- `SteamDiscoveryOptions`,
`ISteamRegistryReader`, `IProcessLookup` (Steam), `StagingLinkCreator` (Profiles),
`IProcessLauncher` (Relay-client), `IModRepository` (Mods) -- so tests
and hosts can pre-register overrides (e.g. the Steam fixture's fakes, or a
throwing `StagingLinkCreator` to exercise the failure path) and have them survive the
`Add<Library>()` chain. `TryAdd` is specifically load-bearing for `AddProfiles()`,
which calls `AddMods()` unconditionally: a plain `AddSingleton` there would
clobber a pre-registered mock.

**Per-profile vs global:** global, system-level settings live in `CuratorConfig`
(one config file under the OS local-app-data dir); per-profile settings (mods,
load order, per-mod policies) live with the profile, not in the global config.

Per-library public surfaces -- interfaces, key types, exact DI registrations --
are documented under [Reference -- Modificus Curator](../reference/).

## The Relay contract Curator consumes

Stable surface (Relay is built; this is the boundary Curator builds against):

- **Invocation:** subprocess `mod_relay.exe`, precedence **flag > env > default**.
- **Flags:** `--game-binary <path>` (required) · `--mod-path <path>` (the mod
  root) · `--log-file <path>` · `--log-append` (a bare flag Curator always emits
  right after `--log-file`: Relay writes a per-day `relay-<yyyyMMdd>.log` shared
  across launches, so it appends rather than overwrites) · `--log-level <level>`
  · `--steam-app-id <id>`
  (default `1361210`) · `--log-lua` (a bare boolean: tees Lua `print` output
  from the mod loader, DMF, and mods into the `--log-file`; off by default) ·
  `--skip-splash` (a bare boolean: skips Darktide's intro splash state so the
  game advances directly to the title screen; off by default).
- **The mod root (`--mod-path`) is what Curator owns and writes:** DMF (when
  installed) + user mods + `mods.lst` (the load-order file).
- **`mods.lst`:** one mod folder name per line, in load order. **Regenerated by
  Curator on every launch** from the profile's mod list (a projection, not a
  source of truth). Enable/disable is by omission -- disabled mods aren't
  listed. The file is a faithful projection of the profile's order: a fresh
  DMF add lands first + locked through the profile add boundary (see
  [Profiles](#profiles)), not through any staging- or Relay-side rule.
  Missing/empty file → Relay loads nothing (graceful).
- **Two roots (Curator must respect):** the mod root is Curator-owned; the loader
  root (`<dll-dir>/mod_loader/`) is Relay-owned and untouchable.
- **What Relay does NOT do (so Curator must):** load-order computation,
  dependency resolution, profile/staging management, platform plumbing on
  Linux.
- **Live control:** none in v1 -- launch is fire-and-forget; the launcher exits
  after resume. Status / hot-reload / live enable-disable are a tracked future
  Relay contract expansion (GitHub issue), not v1.

See the
[Mod Relay docs](https://github.com/ModifAmorphic/darktide-mod-relay)
for the full contract (env-var table, logging, the hook-ready handshake).

## Profiles

- A profile owns its own mods, mod settings, and load order. All settings
  except global are per-profile.
- A profile also owns its **launch settings**: ordered environment-variable
  entries (name/value pairs) + ordered Darktide command-line arguments + a
  skip-splash toggle and a Lua-logging toggle. These persist with the profile
  (`profile.json`) and apply at launch (env reaches Proton before it starts on
  Linux and the Relay process on Windows; game args flow through Relay's
  bare-`--` contract). The skip-splash toggle (`SkipSplash`) emits Relay's bare
  `--skip-splash` flag, skipping Darktide's intro splash state; its Relay env
  form `RELAY_SKIP_SPLASH` is reserved so the toggle is the single source of
  truth. The Lua-logging toggle (`EnableLuaLogs`) emits Relay's bare `--log-lua`
  flag, teeing Lua `print` output into the log file; its Relay env form
  `RELAY_LUA_LOGS` is reserved so the toggle is the single source of truth.
  Editing is unlocked while Darktide runs (changes apply next launch). Env names
  are validated against a reserved set (Curator-owned OS/launch + Relay config
  env) so a profile can't fight Curator's own launch values.
- The profile's mod root is what Curator passes to Relay as `--mod-path`;
  Curator writes `mods.lst` into it on each launch.
- **DMF on profile creation:** the new-profile flow surfaces a Yes/No confirm
  offering to add DMF (most mods depend on it, so this is the common case; DMF
  isn't mandatory, so the prompt is an offer, not a requirement; decline is
  respected). DMF is sourced from Nexus Mods (mod 8). Two cases: DMF already
  in the repo but not in the profile -> instant add (no download); DMF not in
  the repo -> on confirm, premium users get the in-app API download under a
  spinner + add; everyone else (no auth, regular, or unknown premium state)
  gets their browser opened at DMF's Nexus files page regardless of whether
  Curator owns the `nxm://` handler (when Curator owns it, the user clicks
  Download on the page and the handler picks up the URL + adds DMF to the
  active profile via the standard nxm flow; when Curator does not own it, the
  user downloads the archive and imports it via the normal add flow; the
  confirm message already explains the manual path). Beyond the prompt, the
  one DMF-specific behavior lives in the profile add boundary
  (`IProfileService.AddMod`): whenever DMF is added to a profile that does not
  already contain it (whatever path led there -- prompt, Premium download,
  nxm, local import, linked folder), the new entry is inserted first (rank 0)
  and order-locked, shifting existing entries down one rank. Recognition is a
  deliberately small rule (Nexus mod 8 by source, or the canonical lower-case
  `dmf` base folder containing `dmf.mod`). This is an initial placement
  default, not a protected state and not dependency-resolver enforcement: the
  user may unlock, reorder, disable, or remove DMF afterwards, and a re-add or
  update of an entry already in the profile is a strict no-op that preserves
  the user's current order, enabled state, policy, and lock. Existing profile
  entries are not migrated.
- Mods are stored **once, in a unified repository** keyed by `(source, identity)`
  per UUID container. Profiles reference a mod by `(containerId, policy)` and
  store no mod files of their own. See [Mod repository](#mod-repository).

## Mod repository

Mods are stored once, in a unified repository keyed by **one UUID container per
`(source-type, identity)`**. A container holds zero or more **opaque-ID version
subfolders**, indexed by a per-container `container.json` manifest. Profiles
reference a mod by `(containerId, policy)` and resolve the version folder at
stage time, so a profile never stores mod files of its own. This keeps one copy
of each mod + version, organized so they don't collide and can associate to
profiles.

Container identity by source: **Nexus** by `ModId`, **Untracked** (local) by
`Name`, **Linked** (an external folder added without copying) by its normalized
`ExternalPath`. Different source-types are separate namespaces, so an untracked
"WeaponTweaks", a Nexus "WeaponTweaks", and a linked "WeaponTweaks" are distinct
containers that never collide or share.
The container directory is **UUID-named (opaque)**, and its path is **derived**
(`<ModsFolder>/<containerUUID>/`), never stored absolute.

Each version is a subfolder with an **opaque unique ID**; the raw version tag
lives only in the manifest (for display + pin resolution), never as a folder
name. **`isLatest` is a flag on one version entry**, not a duplicate folder:
moving latest is a one-field manifest edit, and profiles resolve dynamically at
stage time (no rescanning profiles, no disk duplication).

Each profile entry carries a **version policy**: **pinned** (frozen to a specific
imported version) or **latest (auto-update)** (tracks the newest release). At
stage time:

| Profile policy | Resolved version |
| --- | --- |
| latest | the container's `isLatest` version folder |
| pinned `<versionId>` | the version whose `Folder` matches the pin's `VersionId` |
| pinned `<orphan id>` | skip + warn (no version matches) |

The pin is a **foreign key** (`PinnedPolicy.VersionId`) to a `ModVersion.Folder`
(the opaque version-folder ID the repository mints), not a version string. The
repository is the single source of truth for version details: the readable
release tag lives only on `ModVersion.VersionString` (for display). A pin
references a version row, so it always resolves to a real imported version or is
an orphan (skip + warn); a "phantom" pin to a version that was never imported
cannot be expressed (the policy editor offers only the container's actual
versions, and `SetModPolicy` rejects an unknown id). Nexus file versions are
arbitrary strings (not SemVer); there is no version ordering at this layer, and
"newer" is decided by fetching the latest release tag and checking string
inequality.

Each container also carries a **source** (Untracked / Nexus / Linked) so a pinned version
is legible ("WeaponTweaks *(Nexus #12345)* pinned to `1.2`"). The UI collects
URLs; the model stores the canonical identity (Nexus mod id) via a pure parser.
Local / untracked mods use the `UntrackedSource` source (dedup by name). A
**linked** container (`LinkedSource`) is metadata only: it records a normalized
`ExternalPath`, holds no version subfolders, and stages directly from the
external folder at launch.

A container also carries optional **source-agnostic display metadata**
(`ModDisplayMetadata`: a short `Summary`, a `ThumbnailUrl`, and an
`IsAdultContent` flag) on `ModContainer.DisplayMetadata`. The record is
source-agnostic: the Mods library does not reference Integrations, and
Integrations owns the normalization from its Nexus DTOs through one internal
mapper so acquisition and the gradual backfill cannot drift. `null` means
Curator has never retrieved display metadata; a non-null object with an empty
summary and a null thumbnail URL is an authoritative fetched result that
simply has no display content. The two states stay distinct: the backfill
treats only `null` as a candidate. Acquisition replaces the prior value in the
same manifest update as the version mutation (`AddVersion` with a non-null
metadata argument); `null` preserves the prior value, so a manual re-import
never erases a prior Nexus acquisition or backfill. The gradual missing-only
backfill initialization goes through `TryInitializeDisplayMetadata`, which
writes and persists atomically under the repository lock and returns false
when `DisplayMetadata` is already non-null (a concurrent writer is never
silently clobbered by a stale fetch). The field is backward compatible on
disk: a manifest from before it existed deserializes to `null`, so no
migration pass is required.

**Import flow:** adding a mod to the active profile goes through
`IModImportService` (domain file I/O stays behind the import service; the UI's
own `IModThumbnailService` is the narrow presentation-cache exception). The import service
validates the source structure (the source must contain exactly one base
directory with a matching `<base>.mod` descriptor inside it), then resolves (or
creates) the container for the source + extracts the archive / copies a folder
into the repository-managed opaque version folder via
`IModRepository.AddVersion`. Archive detection is content-based (via
SharpCompress's `ArchiveFactory`, reading magic bytes, not the extension), so
zip, 7z, rar, and the other SharpCompress formats all flow through one path;
extraction is traversal-safe (per-entry `WriteToDirectory`, directory entries
skipped, a defense-in-depth `AssertSafePath` containment check per file entry,
no `SymbolicLinkHandler`), and `AddVersion` stages the extraction into a sibling
temp dir + atomically swaps it into the version folder on success, so a failed
re-import (a CRC or I/O error mid-extraction) leaves the existing version
untouched. The validated base folder is **preserved** under
`<versionFolder>/<base>/` (the folder import copies the folder itself, not its
contents; the archive is validated to have a single top-level folder before
extraction). Container dedup: Untracked by name, Nexus by mod id. Version dedup:
re-importing the same tag reuses its folder (refreshed); a new tag creates a new
version + flips `isLatest`. The service returns `(containerId, versionId)`,
where `versionId` is the imported version's opaque on-disk folder id (a
`ModVersion.Folder` value); the display tag (`ModVersion.VersionString`) is
recorded in the container manifest and is not returned. The caller then adds
the profile reference via `IProfileService.AddMod`. Remote
acquisition (the Nexus API client, auto-fetch) is handled by
`IModAcquisitionService`; the acquisition service downloads the archive to a
temp path preserving the real Nexus `file_name` extension, then hands it to the
import service.

**Base-name collision hard-block:** two mods with the same base folder name
can't coexist in one profile (the mod loader can't tell them apart). Before
importing, the add flow peeks the base name (`IModImportService.GetBaseName`)
and the would-be container (`IModImportService.FindExistingContainer`), then
asks `IProfileService.GetBaseNameCollision` whether any existing profile mod (a
different container) resolves to the same base name. On a hit, the import is
**refused**: nothing is created. The would-be container is excluded, so a re-add
of a mod already in the profile (same container, `AddMod` idempotent) is not a
collision.

**Link flow (external folder, no copy):** the add split button's "Link external
folder" item records an external mod directory as a `LinkedSource` container via
`IModImportService.LinkFolder` **without copying it**. The container holds
metadata only (a `container.json`, no version subfolders); staging links into
the external folder directly at launch. The flow reuses the import gates: it
peeks the base name (the picked folder IS the base and must contain
`<base>.mod`), then runs the same base-name collision check (excluding a
re-link, which refreshes and returns the existing container id). On success the
caller adds the profile reference with `LatestPolicy` (inert for linked, since
there are no versions to resolve). The external folder is the user's:
Curator controls only load order and enabled/disabled, and never copies, writes,
versions, renames, or deletes anything inside it. A missing folder is shown as
broken (transient availability flag, no watcher; removed on rescan only if no
profile references it). Linked mods are excluded from the Nexus update check
(it is Nexus-only).

**Staging:** at launch (alongside regenerating `mods.lst`), Curator materializes
the profile's mod root (the `--mod-path` dir) by, for each enabled mod,
discovering its base folder name (the single subdirectory inside the resolved
version folder) and linking `staged/mods/<baseName>` to
`<versionFolder>/<baseName>/` via the platform-selective `StagingLinkCreator`
seam (an NTFS junction on Windows, where it is privilege-free: no Developer Mode,
no admin; a symlink on Linux). The base name, not the container's display name,
is the link + `mods.lst` name: mods bake their folder name into their code, so
the link must carry the base name for the mod's hardcoded paths to resolve. Like
`mods.lst`, the mod root is a projection of the profile's mod-list metadata,
regenerated each launch. Staging is a simple loop: base-name collisions are
blocked at import time, so staging never sees two mods with the same base folder
name in normal use. A linked mod stages directly from its external folder (the
link target is the external path; no version resolution, since a linked
container has no versions), and a missing/unreadable external folder is skipped
(no fallback copy is created). **Staging links, never copies**: the repository
holds the files; `staged/` is a staging-link projection (and for a linked mod the
link points straight at the user-owned external folder, which staging never
modifies).

**Startup cleanup:** `ModCleanup.PruneUnreferenced` runs once after composition,
dropping version folders no profile references + removing empty containers.
A referenced linked container (which has no versions) is kept by a containerId
sentinel in the referenced set, so it survives while any profile uses it; an
unreferenced one is pruned like any empty container, and its external target is
never touched. Keeps the on-disk tree in sync with what the profiles actually
use.

**Storage location:** because paths are derived, changing `<ModsFolder>` is a
physical move of the tree plus a config update; no manifest rewriting, no drift
detection. Curator does not offer the move from the UI (the operator edits the
config + moves the folder by hand); `Rescan` rebuilds the in-memory index from
the new location.

## Mod sources / integrations

- **Nexus Mods** -- the primary source for user mods (most Darktide mods live
  there). Nexus API key or OIDC; version checks; downloads / updates.
- **Local** -- manually-installed mods (the user supplies the files).
- **Linked external folder** -- an external mod directory added to a profile
  without copying it. Curator records a metadata-only container and stages from
  the external path at launch; it controls only load order and enabled/disabled,
  and never writes to, versions, renames, or deletes anything in the external
  folder. No versions, no Nexus update check; a missing folder shows as broken.
- **DMF specifically** -- the new-profile prompt offers to add it (most mods
  depend on it, so this is the common case; DMF isn't mandatory, so the prompt
  is an offer, not a requirement). DMF is sourced from Nexus Mods (mod 8); the
  prompt's download path branches on the user's premium state (premium: in-app
  API download; non-premium, unknown, or no auth: the browser opens at DMF's
  Nexus files page regardless of nxm setup). Bundling DMF with Curator is
  rejected (modding-community norms + Nexus rules). (See
  [Profiles](#profiles).)
- Per-mod: auto-update override (overrides the global setting); version pinning.
- **Import / Export** -- profile import / export.

## nxm:// scheme handler

The `nxm://` URL scheme is how the "Mod manager download" button on a Nexus
Mods file page reaches Curator. Curator registers a tiny native-AOT handler exe
that the OS invokes on each `nxm://` click; it forwards the raw URL over a
named pipe to the running app (or cold-starts Curator and retries), where the URL
is parsed, classified, and dispatched to a pluggable handler. Single-instance is
enforced by process enumeration before the pipe bind, and the pipe bind is a
separate, non-fatal check that degrades gracefully; the OS handler registration
is an explicit user action from the Nexus destination (Curator only handles
Darktide `nxm://` downloads). Full detail (the two-process model, the cold-start
path, single-instance enforcement, pipe-bind behavior, OS registration, and URL
routing) is in [nxm:// scheme handler architecture](nxm-scheme-handler.md); the
public surface is in [nxm reference](../reference/nxm.md).

## Nexus authentication

Nexus Mods auth has two user-facing paths, both surfaced in the Nexus
Integrations destination: **OAuth** (the primary, a loopback OIDC flow via
`Duende.IdentityModel.OidcClient`, RFC 8252) and **API key** (the alternative,
validated against `GET /v1/users/validate.json`). The user's explicit choice is
stored in `NexusConfig.AuthMethod` (`None` / `OAuth` / `ApiKey`); the v1 client's
auth factory is selected by that flag with no fallback, and switching methods
clears the other method's credentials. OAuth tokens are persisted in `CuratorConfig`
with 401-reactive refresh. Full detail (the loopback flow, the Nexus
destination, auth-factory selection, token persistence, the OAuth client_id, rate
limits, and the v1 endpoints) is in
[Nexus authentication architecture](nexus-authentication.md); the public surface
is in [integrations reference](../reference/integrations.md).

## Mod acquisition

When a user clicks "Mod manager download" on Nexus, the
[nxm handler](nxm-scheme-handler.md) relays the URL and the
`NxmModDownloadHandler` orchestrates the download and import into the active
profile. The reusable core is `IModAcquisitionService` (Integrations): it
resolves the CDN download links, fetches mod metadata, downloads to a
`.zip`-named temp file, and imports via `IModImportService`. The same
`GetModInfoAsync` call that resolves the mod name also carries the display
metadata (`Summary`, `PictureUrl`, `ContainsAdultContent`); it is normalized
once through the shared `ModDisplayMetadataMapper` and forwarded into
`Import`/`AddVersion`, so it lands on the container in the same manifest
update as the version mutation at no extra Nexus call. The handler (in the
UI assembly, not Integrations, because it coordinates UI-only services) checks
the link is for Darktide, checks auth and an active profile, calls the service,
registers the mod with `LatestPolicy`, refreshes the mod list, and surfaces
errors via `ShowAlertAsync`. The per-mod update button calls the same service.
Full detail (the acquisition flow, the handler checks, the UI-assembly
placement, and OS registration) is in
[mod acquisition architecture](mod-acquisition.md); the public surface is in
[integrations reference](../reference/integrations.md).

## Update check

The `IUpdateCheckService` (Integrations) is the Nexus-only update check.
On profile load it calls the v2 GraphQL `modsByUid` batch query once (1 API call
for all checkable mods), passing the UIDs of the active profile's `LatestPolicy` +
`NexusSource` mods (uid = `game_id * 2^32 + mod_id`, Darktide game_id = 4943).
The server returns the `viewerUpdateAvailable` field for each mod: a
server-computed Boolean that is true if the mod has been updated since the viewer
(current user) last downloaded it. This eliminates the v1 approach's
Month-endpoint intersect, cross-endpoint timestamp tolerance, per-mod
reconciliation, and reconciliation pinning: the server tracks the user's
downloads and computes the signal directly. A `null` `viewerUpdateAvailable`
(server has no download record, e.g. a manually imported mod) is treated as
false (not flagged). A second signal supplements `viewerUpdateAvailable`:
if the server's latest `version` string differs from the installed
`VersionString` (ordinal, case-insensitive), the mod is also flagged. This
catches cases `viewerUpdateAvailable` misses: the user installed an older
version, uses multiple PCs with different local versions, or imported manually
(all share the same root cause: the server's per-user download tracking doesn't
reflect the local machine's state). Either signal triggering is sufficient to
flag. A third tier refines tier-2-only flags: it resolves the newest
non-archived MAIN file via `NexusModFiles.LatestMain` (the same filter the
download path uses) and clears the flag when that file's version equals the
installed version. The mod-page header `version` can lag the latest file (the
author bumps the file without updating the header), which is the false positive
tier 2 produces; tier 3 confirms against the actual file. It is best-effort
(a failure or an unresolved file leaves the flag) and cached per (mod id, page
version, updated-at) with a 24h TTL, in memory and session-scoped. Tier-1 flags
(`viewerUpdateAvailable`) are authoritative and untouched. `PinnedPolicy` and
`UntrackedSource` mods are skipped. Rate-limit-aware: if the client throws `NexusRateLimitException`
(HTTP 429 / exhausted headers) or the response reports an exhausted daily or
hourly quota (and the limit was actually reported, guarding against the all-zero
header-absent fallback), the result is flagged `RateLimited` and the mod-list UI
surfaces a "check incomplete" indicator rather than "all up to date." The full
rate-limiting strategy (what Curator observes, how it reacts, what it does not
do, and what consumes the budget) is documented in
[Nexus API rate limiting](nexus-rate-limiting.md).

`CheckThoroughAsync` (the manual "check now" affordance) runs the same v2 batch
query as `CheckAsync`; the two differ only in the result's `Thorough` flag (kept
for interface compatibility). The result (`UpdateCheckResult`
with per-mod `ModUpdateInfo`) is published via `LastResult` + a
`CheckCompleted` event for the mod-list badges to consume without re-awaiting.
The check is fired fire-and-forget by `UpdateCheckRunner` (UI), which subscribes
to `IProfileSession.PropertyChanged` filtered to `ActiveProfileId`
(startup-with-restored-id + active-profile switch). The service itself has no
UI; the mod-list UI consumes `LastResult` / `CheckCompleted` to render per-row
"update available" badges + the per-mod Update button (which calls
`IModAcquisitionService`). The public surface is in
[integrations reference](../reference/integrations.md).

## App self-update

Curator can update itself in place when packaged by Velopack: the Windows
installer and the self-contained Linux AppImage. On startup the app checks the
GitHub Releases feed for a newer
version of itself; when one is available a dismissible pill appears in the
shell status strip, and an "Updates" section in Settings offers a manual
check and a Download and Restart action. The download, apply, and relaunch
are handled by Velopack, so the user never re-runs the installer. The portable
Windows ZIP and standalone Linux tarball remain manual-update distributions.
Full detail (the
engine-neutral `IAppUpdateService`, the conditional Velopack/no-op split, the
startup-only check, the threading discipline, and the lifecycle interaction)
is in [app auto-update architecture](app-auto-update.md); the public surface
is in [UI reference](../reference/ui.md).

## Mod list (main view)

- Per-mod: enable / disable, remove, update (when the source reports a newer
  version), pin to version, per-mod auto-update override. The pinned-vs-latest
  policy drives version resolution at stage time (see [Mod repository](#mod-repository)).
- Auto-sort (dependency-driven; toggleable); manual reorder (drag the per-row
  grip at the left edge, or the Move Up / Move Down buttons) overrides
  auto-sort. A mod can be locked to its exact zero-based position; a locked row
  keeps its position across any reorder or auto-sort, and its grip stops
  intercepting pointer input so its area falls through to touch scrolling.
- A freshly added DMF lands first and order-locked (the profile add
  boundary's placement default; updateable). The lock is a default the user
  can clear, not a protected state.
- **Row density.** The list has a persisted Compact/Detailed density (Compact
  is the default and the absent/unknown value; Detailed is the multi-line
  variant). Compact is the unchanged one-line row. Detailed renders a rounded
  card per row laid out as one adaptive Grid (the card root is a named width
  container, so an Avalonia `ContainerQuery max-width:680` in the view's styles
  swaps the layout at the 680-DIP card-width breakpoint): column 0 is the
  drag-reorder grip, column 1 is the thumbnail/placeholder slot, column 2
  holds the name + source badge (row 0) and a two-line summary (row 1), and
  row 2 is the action strip. When the card
  is wide (greater than 680 DIP) a 112-DIP thumbnail spans all three rows and
  the action strip occupies only the content column; when constrained (at or
  below 680 DIP) the thumbnail shrinks to 72 DIP spanning name + summary and
  the action strip moves to a full-width row beneath all three columns. The action
  strip is a right-aligned `WrapPanel` that wraps at the edge (no horizontal
  scrolling); width, height, row span, and the action column/span are driven by
  styles so the breakpoint changes them. The summary is plain text with `CharacterEllipsis` trimming and the full
  text retained in the tooltip and the automation name; a fetched-but-empty
  summary shows a neutral "details unavailable" fallback. The thumbnail is
  hydrated only when the container carries a non-null `ModDisplayMetadata`
  with a non-empty `ThumbnailUrl`; every no-image case (untracked/linked,
  missing metadata, no auth, offline, a failed image, an adult flag, an empty
  URL) shows the same neutral placeholder. An adult flag is only a persisted
  boolean: the thumbnail is skipped and the ordinary placeholder shows; no
  badge, filter, warning, or setting hangs off it. A stable-v1 missing-only
  metadata backfill (see [Mod acquisition](#mod-acquisition) and
  [Nexus API rate limiting](nexus-rate-limiting.md)) populates the summary and
  thumbnail URL for containers that were imported before the field existed or
  that were added without it; the backfill and the thumbnail cache live at the
  UI layer (the cache root is `AppPaths.ModThumbnailCacheDir`), domain data I/O
  stays behind the repository.
- **Hot-reload** -- tied to the Relay live-control contract; out of v1.
- **Dependency view** -- out of v1.
- **Conflict detection** -- out of v1.

## Launch

The launch path **diverges by OS**. In both cases Curator resolves the profile,
writes `mods.lst` into the profile's mod root, reads the profile's launch
settings (environment variables + Darktide command-line arguments + the
skip-splash and Lua-logging toggles), then invokes the Relay launcher with
`--game-binary`, `--mod-path`, `--log-file`, then an unconditional bare
`--log-append` (Relay writes its own per-day `relay-<yyyyMMdd>.log` shared
across launches, so it appends). When the profile's `EnableLuaLogs`
toggle is on, Curator appends the bare `--log-lua` flag (a tee of Lua print
output into the log file; not a redirect, so Darktide's console log stays
complete). When the profile's `SkipSplash` toggle is on, Curator appends the
bare `--skip-splash` flag (skips Darktide's intro splash state). All are bare
flags (no value); Relay requires only that its own flags precede the `--`
separator and that a value-taking flag be immediately followed by its value.
(Curator does not emit `--log-level`; the Relay shell's level
vocabulary differs from Curator's Serilog level, so the shell's `info` default
is used.)

A profile's launch settings apply at launch: environment variables reach Proton
before it starts on Linux (inherited by Proton/Relay/Darktide) and the Relay
launcher process on Windows; game arguments flow through Relay's bare-`--`
contract verbatim, in order (one `--` then each arg as its own argv entry; empty
game args emit no `--` = legacy launch). Env-var names are validated (non-empty,
no `=`/NUL, no NUL in values, case-insensitive duplicate rejection, reserved-name
block: the two `STEAM_COMPAT_*`, the five AppImage-identity vars, and the Relay
config env that Curator supplies as flags). Editing launch settings is unlocked
while Darktide runs (a `profile.json` write); changes apply next launch.

### Windows (trivial)

Curator is a native .NET process; `mod_relay.exe` is a native Windows
binary. The Relay library assembles the args and `Process.Start`s the
launcher directly. No Proton, no prefix, no path translation.

### Linux (native Curator + Proton-at-launch)

Curator runs **natively** on Linux (not Proton-wrapped). `mod_relay.exe` is
a Windows binary, so to run it Curator invokes it under **Proton**, using
**Darktide's own compatdata** as the prefix -- required, because the launcher
`CreateProcess`es Darktide, so the two must share the prefix.

**The constraint that shapes the design:** Proton reads
`STEAM_COMPAT_DATA_PATH` from the environment *before* `mod_relay.exe`
runs, to decide which prefix to use. By the time the launcher executes it's
already inside that prefix; it cannot relocate itself, and Darktide inherits
the prefix regardless. So the compatdata must be set by whoever invokes Proton
-- it is not passable as a launcher flag. **Curator sets both
`STEAM_COMPAT_DATA_PATH` (the Wine prefix) and
`STEAM_COMPAT_CLIENT_INSTALL_PATH` (the Steam install dir) in the environment
when it invokes Proton.** (The live-validated working invocation set both env
vars.) Steam discovers both; Relay-client sets both.

**AppImage desktop-identity sanitization:** when Curator is launched from its
installed AppImage, the AppImage runtime exports a handful of variables into
Curator's environment (`APPDIR`, `APPIMAGE`, `ARGV0`, `OWD`, plus the desktop
hint `BAMF_DESKTOP_FILE_HINT`). KDE Plasma's task manager reads
`BAMF_DESKTOP_FILE_HINT` and then `APPDIR` from `/proc/<pid>/environ` to
resolve a child's desktop identity, so if those leak through `proton run` into
Relay and Darktide, the game window is grouped under Curator's launcher.
Relay-client strips exactly those five keys from the inherited environment
before invoking Proton; every unrelated inherited variable passes through
unchanged, and the desktop-activation tokens (`DESKTOP_STARTUP_ID`,
`XDG_ACTIVATION_TOKEN`, `GIO_LAUNCHED_DESKTOP_FILE`) are intentionally kept.
On a non-AppImage launch (the standalone tarball, a dev build) none of those
keys are present, so the removals are silent no-ops.

Responsibilities:

- **Steam library -- discovery + escape hatch + fail-fast:**
  - Locate Steam (default `~/.local/share/Steam`; read `libraryfolders.vdf`
    for multi-library; handle non-default + Flatpak Steam installs).
  - Find Darktide's install (Windows in-prefix path) + its compatdata
    (`steamapps/compatdata/1361210/`).
  - Resolve the Proton version Darktide is configured to use by reading
    Steam's `CompatToolMapping` (the app-specific entry for Darktide is
    authoritative; the global entry is the fallback), then resolving that tool
    name to a `proton` binary from a custom `compatibilitytools.d` manifest or a
    Valve-managed install. With neither mapping, Darktide's appinfo
    `recommended_runtime` is Steam's non-user default on any Linux host; an
    invalid or unreadable mapping fails rather than bypassing a possible user
    choice. This follows Darktide's own Steam compatibility selection rather
    than guessing from directory names.
  - **Escape hatch:** when auto-discovery can't resolve any of the above,
    prompt the user for the missing path(s). This is possible only because
    discovery lives in the UI app, not in Relay (which has no UI and could
    only fail with a log line). Validate at Curator startup / profile setup so
    misconfiguration surfaces early (fail-fast), not at launch.
- **Relay library -- invocation:**
  - Translate the profile's native mod-path → `Z:\...` (and confirm
    `--game-binary` is the in-prefix Windows path).
  - Assemble the launcher args.
  - `Process.Start` with `STEAM_COMPAT_DATA_PATH = <compatdata>` and
    `STEAM_COMPAT_CLIENT_INSTALL_PATH = <steam-install>` in env,
    command = `<proton> run <runtime-dir>/mod_relay.exe <args>`.

**Relay is unchanged on Linux** -- no Linux helper, no Steam/Proton
discovery, no new flag. It remains the Windows launcher + shell + mod_loader,
run under Proton with `STEAM_COMPAT_DATA_PATH` + `STEAM_COMPAT_CLIENT_INSTALL_PATH`
set by Curator.

**Known characteristic (not a defect):** when Curator launches directly, Steam
isn't supervising the session (no overlay / playtime tracking).

### Launch wiring + Settings + escape-hatch

The shell's `LaunchCommand` invokes `IRelayLaunchService.Launch(activeProfileId)`
(gated by `CanLaunch`: a profile is selected, the game is not running, and no
launch attempt is in progress) and branches on `LaunchResult.Status`:

- **`Launched`**: an immediate `IsGameRunning` refresh (the session's
  `Refresh`), so the running indicator + launch-availability react at once
  rather than waiting for the next poll. Successful launch surfaces no status
  note or other confirmation; the running indicator is the durable signal.
- **`DiscoveryIncomplete`**: opens the focused escape-hatch dialog (below) with
  `LaunchResult.MissingDiscoveryFields`. **No auto-retry:** the user submits the
  paths, closes the dialog, and clicks Launch again. This avoids a loop if the
  user cannot get the paths right; the escape-hatch is a form, not a retry.
- **`StagingFailed`**: the profile's mod root could not be prepared (a staging
  link could not be created). The full exception is logged by the launch façade,
  and its body is carried on `Message`; the UI surfaces it after the localized
  framing (the body is a runtime/OS error, not a string Curator invented).
- **`Error`**: a modal alert surfacing `LaunchResult.Message`.

**Launch-attempt state is shell-owned and separate from game-running state.**
The shell's `IsLaunchAttemptInProgress` covers the whole attempt: it is set
before anything else on an executable launch, one dispatcher yield (Avalonia
`DispatcherPriority.Loaded`, after layout + render and before subsequent
input) lets the freshly-disabled button paint, and only then does the
synchronous discovery/staging/spawn call run on the UI thread. The state stays
set while a failure dialog is open (clearing after the dialog handling and on
any exception path, when retry becomes possible). After a `Launched` result it
stays set until the session's existing running-state signal observes Darktide
or a 30-second timeout elapses (started only after the spawn returns), so the
process-detection gap cannot double-launch and a detector that never sees the
game cannot wedge the button; a false polling result never clears it. This is
signal observation, not process supervision: Relay and Darktide remain
fire-and-forget (no process handle, no `WaitForExit`, no lifetime management
on either process).

While the attempt state is true, the whole client area is blocked by an
in-window launch overlay: a semi-opaque scrim over the disabled shell plus a
centered progress card (localized "Launching Darktide" title, "Preparing your
modded game…" message, and an ordinary indeterminate progress bar) drawn from
app-owned theme-safe resources. Native window chrome stays available (the
window can be moved, minimized, or closed), there is no Cancel control (no
safe cancellation exists once Relay starts), and failure dialogs appear above
the overlay as separate OS-owned windows. The overlay's visibility binds
directly to the attempt state and disappears with the same clear that
re-enables retry (see [UI architecture](ui-architecture.md)).

The **Settings** destination in the shell's navigation rail hosts two
sections, both persisting through `IConfigLoader` (read live, so the next
`Discover()` / launch picks them up):

- **Discovery:** the four discovery paths plus the global
  `OverrideAutomaticDiscovery` mode (`CuratorConfig.Discovery`). Platform-gated
  so Windows renders only the Steam install + Darktide binary rows (the
  compatdata + Proton rows are Linux-only). In automatic mode (off, the default)
  the rows are read-only and show the latest discovered snapshot; in manual mode
  (on) the rows are editable and Browse is enabled. Flipping the toggle is
  write-through: turning it on persists `true` and enables editing; turning it
  off persists `false`, runs an ordinary `ISteamService.Discover` (automatic),
  and refreshes every row. The Discover button forces an
  `ISteamService.Rediscover` in either mode (replaces the snapshot, leaves the
  mode unchanged). A manual-mode row edit writes through immediately via a
  read-modify-save; row writes are ignored in automatic mode.
- **Storage:** two buttons that launch the OS file manager at the Curator
  data root and the profiles root. The data-root path is the static
  `AppPaths.AppDataDir`; the profiles path is read live from config. Nothing
  in this section is editable.

The **escape-hatch dialog** is the focused form shown on `DiscoveryIncomplete`.
It shows inputs only for the missing fields, pre-filled with the current config
value (or blank). Alongside the rows it carries the same global
`OverrideAutomaticDiscovery` toggle + Discover button as Settings, with
identical write-through semantics, so the user can refresh the snapshot or switch
to manual entry without leaving the dialog. Submit writes each staged row value
plus `OverrideAutomaticDiscovery = true` in one read-modify-save (manual mode),
then closes; in automatic mode Submit does not rewrite path values (the toggle's
own write-through already persisted the mode). No auto-retry: the user clicks
Launch again.

Both the Settings discovery rows and the escape-hatch rows are driven by a
shared **`DiscoveryField` descriptor** (one source of truth for the field
metadata: canonical name, human label, browse kind (folder/file), current value,
setter). The canonical names match `DiscoveryResult`'s field names, which are
what `LaunchResult.MissingDiscoveryFields` carries, so the escape-hatch shows
exactly the fields launch reported missing.

## Configuration

One global config file for system-level settings (structured -- e.g. JSON or
TOML):

- Log file location + level (Serilog day-rolling: the file sink's
  `RollingInterval.Day` writes `curator-<yyyyMMdd>.log`, one file per day,
  appended across starts within the same day and rolled at local midnight, with
  older files pruned to `RetainedLogFileCount`. Relay keeps its own separate
  `relay-<yyyyMMdd>.log` in the same directory; relay-client resolves and prunes
  that file to the same retained count at launch and passes it as `--log-file`).
- Profiles base folder (where profiles, mods, and settings are stored).
- Mods folder (the global mod store; see
  [Mod repository](#mod-repository)).
- Relay dir (where `mod_relay.exe` + `relay_shell.dll` +
  `mod_loader/` live).

Per-profile settings live with the profile, not in the global config.

## v1 scope

**In v1:**

- Profiles (create / edit / remove / switch -- switch blocked while the game is
  running).
- Mod list: enable / disable / remove, update indicators, version pinning,
  per-mod auto-update override, auto-sort + manual sequential reorder.
- Mod storage (unified repository keyed by `(source, identity)`, version resolution by policy).
- Mod sources: Nexus Mods (primary) + local; DMF via the
  new-profile prompt (Nexus mod 8).
- Launch Darktide (Windows trivial; Linux native + Proton-at-launch +
  discovery + escape hatch).
- App self-update (Windows installer and Linux AppImage; in-app check,
  download, and relaunch via Velopack).
- Global config + per-profile settings.
- DMF new-profile prompt.

**Out of v1:**

- Relay live-control (status / hot-reload / live enable-disable): awaits
  a Relay IPC contract expansion.
- Dependency-view mod list.
- Conflict detection.

## References

- [darktide-mod-relay](https://github.com/ModifAmorphic/darktide-mod-relay): the
  runtime Curator builds on (launcher/shell contract, mod loader, `mods.lst`
  contract, two-roots model).
- `docs/architecture/README.md`: the Modificus Curator architecture + component
  model.
- `AGENTS.md`: agent orientation + conventions.
