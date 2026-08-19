# Profiles (`Modificus.Curator.Profiles`) -- reference

> Profile + per-profile mod-list management: the profile data model, its on-disk
> persistence, and the projection of the mod list into a staged mod root
> (staging links to the repository's resolved version folders) + `mods.lst` for
> Mod Relay. Status: implemented (the unified mod repository replaced
> the earlier shared-store + per-profile allocation model in #30; staging links
> into the repository rather than copying; an NTFS junction on Windows, a symlink
> on Linux).

A profile owns its own mod list, mod settings, and load order. The profile's
staged mod root is what Curator passes to the Relay launcher as `--mod-path`;
Curator writes `mods.lst` into it on each launch. A profile references mods by
their repository container id and stores no mod files of its own.

## Public surface

### `IProfileService`

Profile lifecycle + per-profile mod-list management. All storage details (paths,
version-folder resolution) stay behind the interface.

```csharp
public interface IProfileService
{
    event EventHandler<ProfileSummary>? ProfileCreated;  // carries id + name + description

    IReadOnlyList<ProfileSummary> ListProfiles();
    Profile GetProfile(Guid id);

    // Atomic create: name + description + launch settings in a single write.
    Profile CreateProfile(string name, string description, LaunchSettings launchSettings);

    // Atomic update: name + description + launch settings in a single read-validate-write.
    // The single editable-profile write (the hosted Profiles page routes every metadata +
    // launch-settings edit through it).
    void UpdateProfile(Guid id, string name, string description, LaunchSettings launchSettings);
    void DeleteProfile(Guid id);

    IReadOnlyList<ModListEntry> GetModList(Guid id);
    void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder);
    void SetModEnabled(Guid id, Guid containerId, bool enabled);
    void SetModOrderLocked(Guid id, Guid containerId, bool orderLocked);
    void AddMod(Guid id, Guid containerId, ModVersionPolicy policy);
    void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy);
    void RemoveMod(Guid id, Guid containerId);

    ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId);  // import-time hard-block

    LaunchSettings GetLaunchSettings(Guid id);                  // focused read (launch path)

    string PrepareModRoot(Guid id);
}
```

`CreateProfile(name, description, launchSettings)` and `UpdateProfile` are the only
editable-profile writes. Both validate and normalize every input before any
persistence, so an invalid input writes nothing. There is no focused
launch-settings or rename write: every metadata + launch-settings edit routes
through the atomic `UpdateProfile`.

Method behavior:

- `ProfileCreated` -- raised whenever a profile is created (carries the new
  profile's summary, including description). The UI's `DmfPromptService`
  subscribes so it can surface the DMF install prompt when the new profile
  becomes active and is missing DMF; subscribers that need to react to "a
  profile was just created" use this rather than diffing `ListProfiles()`.
- `ListProfiles()` -- every profile under `ProfilesBaseFolder`, as lightweight
  summaries (id + name + description), sorted by `Name` (ordinal). Non-`Guid`
  directories and unreadable profiles are skipped with a debug/warning log; one
  bad profile never breaks listing.
- `GetProfile(id)` -- loads the full profile (metadata + mod list). Throws
  `KeyNotFoundException` if the profile dir or `profile.json` is absent. Legacy
  mod entries lacking `ContainerId` are dropped on read +
  logged (fresh-start; the operator re-adds mods).
- `CreateProfile(name, description, launchSettings)` -- generates the `Guid`,
  scaffolds the directory tree (`staged/`) **before** persisting, and writes
  name, description, and launch settings in the initial `profile.json`. `name`
  must be non-whitespace (trimmed); `description` must be non-null, single-line
  (no CR/LF), and at most `Profile.DescriptionMaxLength` characters after trim
  (trimmed, may be empty); `launchSettings` must be non-null and pass
  `LaunchSettingsValidator`. Every input is validated and normalized before any
  filesystem touch, so an invalid input throws and creates nothing.
- `UpdateProfile(id, name, description, launchSettings)` -- atomically replaces
  the profile's name, description, and launch settings in a single
  read-validate-write. Preserves id, creation time, mods, mod order, enabled
  state, and per-mod policies. Same validation as `CreateProfile`; an invalid
  input leaves the existing profile file unchanged. Throws
  `KeyNotFoundException` if the profile is unknown. This is the single
  editable-profile trust boundary: the hosted Profiles page routes every
  metadata + launch-settings edit through it (there is no focused
  launch-settings or rename write).
- `DeleteProfile(id)` -- removes the entry and its entire directory tree
  (recursive). Throws `KeyNotFoundException` if absent.
- `GetModList(id)` -- the profile's mods in stored order, not load order.
- `SetModOrder(id, containerIdsInOrder)` -- reassigns each entry's `Order` so the
  listed containers come first; unmentioned mods keep their relative order
  appended after; unknown ids are ignored. No mods are added or removed.
  **Lock projection:** an entry with `OrderLocked = true` keeps its current
  zero-based load-order index; the requested ordering is projected onto the
  unlocked slots only, so a locked row cannot be displaced. With no locks,
  behavior is a plain reorder of the whole list. `Order`
  values are renumbered dense 0..n-1.
- `SetModEnabled(id, containerId, enabled)` -- toggles a single mod. Throws
  `KeyNotFoundException` if the profile or the container is not in its list.
- `SetModOrderLocked(id, containerId, orderLocked)` -- toggles a single mod's
  `OrderLocked`. Lock metadata alone preserves order, enabled, and policy, and
  implies no staged-game change (the staged root is regenerated on the next
  `PrepareModRoot`). Throws `KeyNotFoundException` if the profile or the
  container is not in its list.
- `AddMod(id, containerId, policy)` -- adds a mod entry (`Enabled = true`)
  and renumbers `Order` dense across the list. A fresh add of DMF (Darktide
  Mod Framework, recognized by a deliberately small rule: the container's
  source is Nexus mod 8, or the content the given policy would stage resolves
  to the canonical lower-case `dmf` base folder containing `dmf.mod`) is
  inserted at rank 0 with `OrderLocked = true`, shifting existing entries down
  one rank while preserving their relative order + all metadata (including
  lock bits); the shifted indexes are the new structural baseline, consistent
  with remove compaction. Every other add appends at the end
  (`OrderLocked = false`). One persistence write either way; the rule lives in
  this boundary so every acquisition path (the DMF prompt, Premium download,
  nxm handler, local import, linked folder) inherits it without caller
  choreography. **List entry only: does NOT fetch or install mod files** (the
  repository holds the files; staging symlinks to them). Idempotent: re-adding
  a `containerId` already in the list is a strict no-op
  (order/enabled/policy/lock untouched), so a DMF update or re-import never
  overrides the user's current arrangement (unlock, reorder, disable, remove
  are all the user's to make; the lock is a fresh-add default, not a protected
  state).
- `SetModPolicy(id, containerId, policy)` -- records the new policy. Resolution
  happens at stage time, so there is no on-disk transition (the policy is just
  metadata; `PrepareModRoot` re-resolves on the next launch). A `PinnedPolicy` is
  validated: its `VersionId` must reference a version present on the container,
  else `ArgumentException` (the UI dropdown can't produce a bad id; this guards
  programmatic / stale-id calls). `LatestPolicy` needs no check.
- `RemoveMod(id, containerId)` -- drops the entry (locked or unlocked), then
  renumbers survivor `Order` dense 0..n-1; the new survivor indices are the new
  baseline for surviving locks. The repository copy is **not** touched (other
  profiles may still reference it; the startup prune reclaims it when no profile
  does).
- `GetBaseNameCollision(id, baseName, excludeContainerId)`: pre-checks a
  base-name collision for the add flow: returns the profile mod (if any) whose
  resolved base folder name matches `baseName`, excluding
  `excludeContainerId` (the container a re-add would dedup to, so a re-add is
  not flagged). Considers **all** mods (enabled + disabled); a mod whose base
  name can't be resolved (missing container/version, corrupted version folder)
  is skipped silently. Pure query: no logging, no side effects. Throws
  `KeyNotFoundException` for an unknown profile; `ArgumentException` for a
  null/whitespace `baseName`. Used by the add flow to REFUSE an import that
  would stage two mods under the same folder name.
- `PrepareModRoot(id)` -- regenerates the staged mod root from
  the current per-mod version resolution and writes `mods.lst` + the staging
  ownership marker. Idempotent (clears + rebuilds `staged/` each call). Returns
  the staged root (the parent of the `mods/` tree Relay consumes; the launch
  façade hands Relay either this or the game dir, depending on hosting mode). A
  staging-link creation failure propagates the raised built-in exception
  (`Win32Exception` from the junction path on Windows,
  `IOException` / `UnauthorizedAccessException` from the symlink path on Linux;
  the manager never silently copies); the relay-client launch façade catches
  that and maps it to `LaunchStatus.StagingFailed`, carrying the exception's
  body, and the UI surfaces it after the localized framing.
- `ProfilesRoot` -- a focused read of the profiles' base folder (the live
  `ProfilesBaseFolder` from config, ensured to exist). Consumed by the
  relay-client game-dir host as the ownership prefix: a game-dir hosting link
  whose stored target lies under this folder is Curator's even when the target
  is currently missing.
- `GetLaunchSettings(id)` -- a focused read of the profile's launch settings
  (environment variables + Darktide command-line arguments), used by the launch
  path (relay-client reads it on each launch). The hosted Profiles destination
  edits launch settings through `UpdateProfile`; the launch path applies the
  settings next launch, and editing is unlocked while Darktide runs (a
  `profile.json` write that does not touch the running process).

### Key types

- `ModListEntry` -- a single mod within a profile's list (immutable record):
  `ContainerId` (Guid; the join key against `IModRepository`), `Enabled`
  (disabled mods are omitted from `mods.lst`: enable-by-omission), `Order`
  (`int`, lower loads first), `OrderLocked` (bool, default false; when true the
  entry keeps its current zero-based load-order index across `SetModOrder`
  calls, independent of `Enabled` -- a disabled or linked row can still be
  order-locked), `Policy` (default `ModVersionPolicy.Latest`; drives version
  resolution). Mutations go through `IProfileService`, which rebuilds the
  changed entry via `with` expressions and persists. An existing `profile.json`
  written before `OrderLocked` existed loads every entry unlocked (the bool
  default for a missing property).
- `Profile` -- the aggregate root persisted to
  `<ProfilesBaseFolder>/<Id>/profile.json`. Identity is `Id` (a `Guid`, stable
  across renames and the on-disk directory name); `Name` is a display label, not
  unique, not a path. `Description` is a short, single-line description (defaults
  to empty; coerced from JSON `null` / missing property on read, mirroring
  `Mods`). `CreatedAt` is UTC. `Mods` is exposed as an immutable
  `IReadOnlyList<ModListEntry>`. `LaunchSettings` defaults to a non-null empty
  instance (coerced from JSON `null` / missing property on read, mirroring
  `Mods`); metadata + launch-settings changes go through `UpdateProfile`
  (the single atomic editable-profile write).
  `Profile.DescriptionMaxLength` (`120`) is the description length cap enforced
  at the service boundary.
- `ProfileSummary(Guid Id, string Name, string Description)` -- a lightweight
  projection for profile pickers (no mod list loaded).
- `StagingLinkCreator` -- a `delegate` that creates a directory staging link.
  The default (registered by `AddProfiles`) is platform-selective: an NTFS
  junction on Windows (privilege-free; no Developer Mode / admin required) and a
  symlink via `Directory.CreateSymbolicLink` on Linux. Injectable so tests
  exercise the failure path without platform permission hacks. A creation
  failure propagates the raised built-in exception as-is (`Win32Exception` from
  the junction path; `IOException` / `UnauthorizedAccessException` from the
  symlink path); the staging call site lets it propagate, so the staging layer
  never silently copies.
- `StagingOwnership` -- the shared staging-ownership contract: the marker
  filename (`.curator.json`) Curator writes into the staged `mods/` on every
  `PrepareModRoot` pass. Profiles is the writer; relay-client's game-dir host
  reads the file's presence to prove a hosting link is Curator's.

`ModVersionPolicy` (PinnedPolicy/LatestPolicy), `ModSource`, `ModContainer`, and
`ModVersion` live in the [mods](mods.md) library; Profiles consumes
them.

### Launch settings (`EnvVar` + `LaunchSettings`)

Per-profile environment variables + Darktide command-line arguments, persisted
with the profile and applied at launch. Environment values reach Proton before
it starts on Linux (inherited by Proton/Relay/Darktide) and the Relay launcher
process on Windows; game arguments flow through Relay's bare-`--` contract
verbatim, in order.

```csharp
public sealed record EnvVar(string Name, string Value);

public sealed record LaunchSettings
{
    public static readonly IReadOnlyCollection<string> ReservedEnvironmentNames;  // 14, case-insensitive
    public IReadOnlyList<EnvVar> EnvironmentVariables { get; init; }  // ordered, default empty
    public IReadOnlyList<string> GameArguments { get; init; }        // ordered, default empty
    public bool EnableLuaLogs { get; init; }                         // emits Relay's --log-lua when true
    public bool SkipSplash { get; init; }                            // emits Relay's --skip-splash when true
}
```

- Ordered lists (not dictionaries) so JSON order is explicit and game-argument
  order + duplicates survive persistence; duplicate-name detection happens in
  `UpdateProfile` validation (the shared `LaunchSettingsValidator`), not
  silent storage collapse.
- Backward compatible: an existing `profile.json` without `LaunchSettings`, and
  an explicit JSON `null`, both deserialize to an empty (non-null) instance
  (`ReadProfileFile` coerces `null` to `new()`, mirroring `Mods ??= Empty`).
- `ReservedEnvironmentNames` (case-insensitive, 14 names) is the central
  reserved-name policy consumed by the shared `LaunchSettingsValidator` (below)
  so the launch-settings UI pre-validates inline from the same source of truth.
  Two groups: Curator-owned OS/launch env (7: the two `STEAM_COMPAT_*`,
  `APPDIR`, `APPIMAGE`, `ARGV0`, `OWD`, `BAMF_DESKTOP_FILE_HINT` -- a profile
  value would fight Curator or break the AppImage-identity invariant) and Relay
  config env (7: `MODIFICUS_GAME_BINARY`, `MODIFICUS_MOD_PATH`,
  `RELAY_LOG_FILE`, `RELAY_LOG_LEVEL`, `MODIFICUS_STEAM_APP_ID` -- Curator
  supplies these as flags so the env fallback is inert; blocked to avoid a
  silently-ignored value -- plus `RELAY_LUA_LOGS`, owned by the per-profile
  `EnableLuaLogs` toggle and reserved so a profile env can't double-control or
  silently bypass that toggle, and `RELAY_SKIP_SPLASH`, owned by the per-profile
  `SkipSplash` toggle and reserved for the same reason).

### Launch-settings validation (`LaunchSettingsValidator`)

The single source of truth for launch-settings validation, shared by the
authoritative `UpdateProfile` (the trust boundary) and the launch-settings
editor (inline per-field feedback). Pure: no localization, no I/O, no side
effects. It returns **structured, machine-readable errors**, not localized
strings (the Profiles library is backend-only; each consumer localizes the
kinds its own way).

```csharp
public enum LaunchSettingsValidationErrorKind
{
    NameEmpty, NameInvalid, NameReserved, NameDuplicate, ValueNul,
}

public enum LaunchSettingsErrorField { Name, Value }

public sealed record LaunchSettingsValidationError(
    int Index,                                  // env entry index
    LaunchSettingsValidationErrorKind Kind,
    string Name)                                // offending name (empty for NameEmpty)
{
    public LaunchSettingsErrorField Field { get; }  // derived from Kind
}

public static class LaunchSettingsValidator
{
    public static IReadOnlyList<LaunchSettingsValidationError> Validate(LaunchSettings settings);
    public static bool IsValid(LaunchSettings settings);  // Validate(...).Count == 0
}
```

Rules: per entry, name non-empty after trim; name contains neither `=` nor NUL;
name not in the reserved set (case-insensitive); name not a case-insensitive
duplicate of another entry; value contains no NUL. Values are otherwise stored
exactly (spaces + empty values preserved). Game arguments are not validated (any
string is a legal argv value). Per-entry precedence: NameEmpty, NameInvalid,
NameReserved, NameDuplicate, ValueNul (the first applicable kind wins; at most
one error per entry). **Duplicates are reported on every colliding entry** (a
name that appears more than once case-insensitively), so the UI can flag every
row involved; the service throws on the first error in entry order.

`UpdateProfile` delegates to `Validate`, then throws `ArgumentException` on
the first error with a clear, developer-facing (English) message that echoes the
offending name. The structured errors carry no localization; the per-kind
exception messages here are developer-facing only. A parameterized agreement
test (`LaunchSettingsValidatorTests`) feeds the same inputs through both
verdicts (does the validator report errors? does `UpdateProfile` throw?) and
asserts they agree, guarding against drift. Profile files are plaintext, so this
is not secret storage; logs never print environment values (only the profile id
+ counts).

### `ModCleanup` (static)

Startup prune orchestration. Collects every `(containerId, versionFolder)`
referenced by any profile (resolving each entry's policy against its container
via `ModContainer.ResolveVersion`), then calls
`IModRepository.PruneUnreferenced`. The composition root invokes it once after
building the service provider; a failure is logged + swallowed so cleanup never
blocks startup.

A `LinkedSource` container has no versions, so it is referenced by containerId
alone: a linked profile entry adds `(containerId, "")` to the referenced set.
The empty version folder is a sentinel that never matches a real opaque version
id, so it cannot affect version dropping; its only role is to mark the
containerId as referenced so the prune keeps the linked container while any
profile uses it. An unreferenced linked container is pruned like any empty
container; the external target is never touched.

```csharp
public static class ModCleanup
{
    public static void PruneUnreferenced(IProfileService profiles, IModRepository repo);
}
```

## DI registration

```csharp
public static IServiceCollection AddProfiles(this IServiceCollection services);
```

`AddProfiles()` registers:

- `AddMods()` -- called defensively (idempotent) so a lone `AddProfiles()`
  yields a resolvable `IProfileService`; the composition root also calls it.
- `TryAddSingleton<StagingLinkCreator>(_ => CreateStagingLink)` -- the
  platform-selective default (an NTFS junction on Windows via `Junction.Create`;
  `Directory.CreateSymbolicLink` on Linux). `TryAdd` so a test may pre-register a
  throwing/fake delegate.
- `AddSingleton<IProfileService, ProfileService>()` -- the filesystem-backed
  implementation (internal). Resolves `CuratorConfig`, `IModRepository`,
  `StagingLinkCreator`, and `ILogger<ProfileService>` from the container.

Registered as a singleton: it holds no per-request state, and `CuratorConfig` (its
only config source) is itself a singleton.

## On-disk layout

```
<ProfilesBaseFolder>/              (auto-created on first run)
  <guid>/                          (profile dir; id-named)
    profile.json                   (metadata + mod list + launch settings - the source of truth)
    staged/                        (the staged mod root;
                                     REGENERATED each launch - a projection)
      mods/                        (the mod host folder the game-dir link points at)
        <baseName>                 (staging link -> <versionFolder>/<baseName>/)
        mods.lst                   (successfully-staged enabled mods, in order)
        .curator.json              (the staging ownership marker)
```

`profile.json` and `mods.lst` are UTF-8 without BOM. There is no per-profile
`mods/` directory (mods live in the repository).

### The staging ownership marker

Every `PrepareModRoot` pass rewrites `staged/mods/.curator.json` (the name is
the shared `StagingOwnership.MarkerFileName` contract) with the projected
profile's identity: `{ schema, profileId, profileName, projectedAtUtc }`.
The marker -- not reparse-ness -- is what proves a game-dir hosting link aimed
at the staged tree is Curator's (see the relay-client
[`IGameDirModsHost`](relay-client.md#igamedirmodshost) ladder); relay-client
reads only the file's presence. App version is deliberately absent: Profiles
does not know it, and profile identity + timestamp carry the troubleshooting
value. The pass cleared + rebuilt the tree, so the marker is rewritten (a
stale marker surviving a rebuild would misattribute the tree).

### Staging (`PrepareModRoot`)

Each enabled mod resolves its `ModVersionPolicy` against its container, then
**discovers the base folder name on the fly** as the single subdirectory inside
the resolved version folder (the import validation guarantees exactly one). The
link + the `mods.lst` entry carry the **base name**, not the container's display
name: mods bake their folder name into their code, so the link must carry the
base name for the mod's hardcoded paths to resolve. The container `Name` is UI
display only.

- **LatestPolicy** → link `staged/mods/<baseName>` → `<versionFolder>/<baseName>/`
  where the version is the container's `IsLatest`.
- **PinnedPolicy(vId)** → link `staged/mods/<baseName>` → `<versionFolder>/<baseName>/`
  where the version's `Folder == vId` (resolution by opaque version id, not by
  tag).
- **LinkedSource** → link `staged/mods/<baseName>` → the external folder itself (no
  version resolution; a linked container has no versions). The base name is the
  external folder's own name (Curator never renames it). A missing/unreadable
  external folder is skipped with reason "external folder unavailable" (no
  fallback copy is created).
- **No match / corrupted** (container missing, no versions, no `IsLatest`, the
  pinned version id is absent, the version folder is missing on disk, or it has
  zero/multiple subdirs so no base name can be derived) → skipped with a warning
  (no `staged/` entry, no `mods.lst` entry).

Staging is a **simple loop**: base-name collisions are blocked at import time
(`GetBaseNameCollision`), so staging never sees two mods with the same base
folder name in normal use. The collision check resolves a linked mod's base name
(the external folder's name) via the same path, so a linked container whose
folder name matches an existing managed mod's base name in the same profile is
reported as a collision. No dedupe, no last-wins, no disambiguation.
**Staging links, never copies** -- the repository holds the files; `staged/` is a
staging-link projection (an NTFS junction on Windows, a symlink on Linux). For a
linked mod the link points directly at the external folder; the external folder
is the user's and is never modified.
`mods.lst` lists exactly what got staged, in `Order`: staging itself enforces
no placement (the fresh-add DMF-first + lock default lives in `AddMod`), so the
file is a faithful projection of
the profile's list.

### Moving `IsLatest` requires zero profile-entry changes

Because a profile references `(containerId, policy)` and resolves at stage time,
flipping which version is `IsLatest` in the repository is a one-field manifest
edit. Every profile with `LatestPolicy` on that container picks up the new
version on the next `PrepareModRoot`; no profile entry changes.

### Data safety -- `ClearStagedDir`

`staged/` is cleared before each rebuild. The clear is **reparse-point-aware**
(it handles directory junctions and directory symlinks alike): it removes each
top-level entry as a link (never following it into the repository). A naive
`Directory.Delete(staged, recursive: true)` could follow a directory link and
delete repository mod files. The delete API is chosen to match the link's kind --
directory reparse points (junction or symlink) use `Directory.Delete` (the link
only), file reparse points use `File.Delete` -- so it stays data-safe on both OSes.

## Dependencies

- **Curator libraries:** `config` (`CuratorConfig.ProfilesBaseFolder`), `mods`
  (`IModRepository`, `ModContainer` / `ModVersion`, `ModVersionPolicy`). The
  dependency direction is clean: Profiles depends on Mods (the repository
  knows nothing of profiles).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Modificus.Curator.Profiles.Tests` covers profile CRUD (`ProfileCrudTests`),
profile description + the atomic create/update contract (`ProfileMetadataTests`:
create round-trip with description + launch settings, missing/null `Description`
read normalization, `UpdateProfile` preserving
identity/mods/order/enabled/policies, name + description normalization and
rejection, launch-settings validation through the shared validator, no-partial-write
atomicity, and `ListProfiles`/`ProfileCreated` projecting description), mod
  list ordering/enable/policy + the base-name collision hard-block
  (`ModListTests`, including the legacy-Name-entry drop + null-Policy coercion +
  `GetBaseNameCollision` over all/none/disabled/excluded/corrupted cases), the
  profile-scoped load-order locks (`ModOrderLockTests`: `OrderLocked` persistence
  + the older-profile-json backward-compatible load, the `SetModOrder` lock
  projection over locked-first/multiple/all-locked/partial/unknown/duplicate/
  no-lock-regression cases, `SetModOrderLocked` true/false + unknown-mod
  behavior, ordinary `AddMod` append-unlocked + compaction + idempotent re-add
  preserving lock, and `RemoveMod` compaction/re-baselining permitting a locked
  row's removal), the DMF fresh-add rule (`DmfAddTests`: Nexus mod 8 first +
  locked on an empty profile + prepended after ordinary mods with survivor
  metadata intact, the canonical `dmf`/`dmf.mod` content recognition for
  untracked + linked containers, lookalikes staying ordinary (wrong-case base
  folder, non-matching descriptor, other Nexus ids), the unknown-container-id
  append allowance, idempotent re-add after the user unlocks/reorders/disables,
  remove-then-re-add reapplying first + locked, and the prepend lock's
  interplay with later reorders + unlocking), the
launch-settings model + service (`LaunchSettingsTests`: round-trip across a
fresh instance, old-JSON-loads-empty + explicit-null normalization, order +
duplicate preservation, the full validation surface -- empty / `=` / NUL name,
NUL value, case-insensitive duplicate, reserved names -- routed through
`UpdateProfile`, + the guarantee that an update preserves
Id/CreatedAt/Mods) + the shared validator
(`LaunchSettingsValidatorTests`: the structured result's index/kind/field shape,
per-kind verdicts, and a parameterized agreement test that feeds the same
inputs through both `UpdateProfile`'s verdict and the validator's verdict
across valid + every invalid case), `PrepareModRoot` + staging-link
staging (junction on Windows, symlink on Linux) + the data-safe `ClearStagedDir`
(`PrepareModRootTests`, `StagingTests`), the **linked-mod staging + safety**
(`LinkedModStagingTests`: links the external folder directly, missing-external
skip with no fallback copy, enable/disable + reorder, cross-source base-name
collision, sentinel survival; `LinkedFolderSafetyTests`: availability
missing-then-returned on rescan, sentinel survival across a full
link-stage-remove-rescan-delete sequence; `ModCleanupTests`: referenced linked
container survives prune, unreferenced is pruned, external target untouched), and the `AddProfiles` DI wiring
(including the `TryAdd` `StagingLinkCreator` override).

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md) -- the
  [Profiles](../architecture/MODIFICUS-CURATOR.md#profiles) +
  [Mod repository](../architecture/MODIFICUS-CURATOR.md#mod-repository) sections.
- [mods](mods.md) -- the unified mod repository + version-policy model.
- [relay-client](relay-client.md) -- the launch façade that consumes
  `PrepareModRoot`.
