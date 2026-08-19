# 0001 - Game-dir mod hosting

> Status: **spec (operator-approved design).** The behavior inversion, the
> ownership marker, the rename-and-consent flow, and the experimental external
> toggle were settled with the operator. This document is the implementation
> spec; it does not re-litigate the design.

## Problem

Some mods (the motivating case: SimpleAudio) resolve game-directory-relative
paths at runtime and require the mods tree to live under the real game
directory (`<game>\mods\...` alongside `<game>\binaries\darktide.exe`).
Curator's staging model serves mods from
`<profiles>\<id>\staged\mods\`, so those mods cannot work today.

Junctions/symlinks into the game dir are a validated mechanism for this
(operator-verified, including cross-volume junctions on Windows). Curator
already stages through reparse points; this spec moves one link to the game
dir and makes that the default.

## Behavior

### Default: game-dir hosting

On every modded launch (after staging succeeds and the game binary is known):

1. `GAME_DIR` is derived from the discovered `DarktideGameBinaryPath`
   (`dirname(dirname(binary))`; the Darktide layout is
   `<game>\binaries\darktide.exe`). Validate the derived dir exists.
2. The staging pass writes its ownership marker (below) into
   `<staged>\mods\.curator.json`.
3. Curator ensures `GAME_DIR\mods` is a link it owns, pointing at
   `<staged>\mods` of the active profile:
   - Windows: NTFS junction (privilege-free, cross-volume).
   - Linux: symlink (privilege-free; created on the native path inside the
     Proton prefix, Wine follows it).
   - Re-pointing is delete-plus-recreate of the link only. The staged tree is
     never deleted through the game-dir link.
4. When hosting is active, the `--mod-path` handed to Relay becomes `GAME_DIR`
   (native path; the Linux strategy Z:\-translates it exactly as today).
   Relay's contract is unchanged: it already receives the parent of the
   `mods\` folder (today `<staged>`, now `GAME_DIR`).

A plain Steam launch remains vanilla: nothing injects or loads mods without
Curator launching through Relay.

### Ownership: the marker, never the reparse point

Reparse-ness alone proves nothing (a user may have made their own
junction/symlink). Ownership is decided by:

- **Marker:** `.curator.json` inside the link target's `mods` root (the staged
  `mods\` dir), rewritten on every staging pass:
  ```json
  { "schema": 1, "profileId": "<guid>", "profileName": "<name>", "projectedAtUtc": "<iso>" }
  ```
- **Path prefix:** a link whose stored target lies under the Curator profiles
  root is Curator's even if the target is currently missing (dead link after a
  data move); Curator re-creates/re-points it without ceremony.

Claim ladder for the thing at `GAME_DIR\mods`:

| State | Verdict | Action |
| --- | --- | --- |
| Absent | - | Create the link, silently. |
| Link, marker at target, or target under profiles root | ours | Re-point silently if needed. |
| Anything else (real dir, real file, link to elsewhere, dead link outside our space) | foreign | Consent flow, below. |

### Foreign mods folder: rename + consent

A foreign entry never gets deleted or modified. The launch returns the new
`LaunchStatus.GameDirConflict` (message carries the detected path) before any
game-dir mutation. The UI shows a three-choice modal:

- **Proceed:** Curator renames the foreign entry to
  `mods_<yyyyMMdd-HHmm>` (bump `-1`, `-2`, ... on collision), writes a short
  `README.txt` inside the renamed folder (folder case only) explaining what
  happened and that nothing was deleted, records a receipt in app-state
  (original path, new path, timestamp), then retries the launch once.
- **Keep my current setup:** persists `Preferences.ExternalModHosting = true`
  (the experimental external mode) and retries the launch once; that launch
  and all later ones serve mods from staging without the game-dir link.
- **Cancel:** abort the launch.

The retry is one-shot per consent: a second `GameDirConflict` in the same
attempt chain surfaces the standard error alert (no loop).

### The experimental external toggle

`CuratorConfig.Preferences.ExternalModHosting`, default `false`, global
(one `GAME_DIR\mods` slot, one authority). `true` restores the pre-hosting
behavior: `--mod-path` = staged root as today, plus a best-effort removal of a
Curator-owned game-dir link if one exists. The Preferences destination
presents it as experimental with its known issue stated (mods that require
game-folder paths will not load).

Read live per launch like the other launch-affecting preferences.

## Change sites

| File | Change |
| --- | --- |
| `src/profiles/ProfileService.cs` | `PrepareModRoot` writes `.curator.json` into the staged `mods\` each pass (profile id/name already in hand). |
| `src/profiles/IProfileService.cs` | Focused read `string ProfilesRoot { get; }` (ownership prefix check + support). Doc-comment touch-ups. |
| `src/relay-client/GameDirModsHost.cs` (new) | The ladder, link create/re-point/remove, foreign classification, takeover rename + README + receipt. Injects `StagingLinkCreator` (platform link primitive from Profiles DI) + `IAppStateStore` (receipts). |
| `src/relay-client/IGameDirModsHost.cs` (new) | Contract for the two consumers: `EnsureHosting(gameDir, stagedRoot)` (called by the launch service) and `TakeOver(gameDir)` (called by the UI after consent). |
| `src/relay-client/RelayLaunchService.cs` | Insert the host step after `PrepareModRoot`/game-binary resolution; `GameDirConflict` result before spawn; `modPath` = `GAME_DIR` when hosting, staged root (after owned-link removal) when external. Link-creation IO/Win32 failures map to `LaunchStatus.Error` with the exception message. |
| `src/relay-client/IRelayLaunchService.cs`, `LaunchResult`/`LaunchStatus` | New status + contract docs. |
| `src/config/CuratorConfig` | `Preferences.ExternalModHosting` (bool, default false) + defaults + XML docs. |
| `src/general/` app-state | `RenamedModsFolders` receipts list (original, renamed, timestamp) + atomic round-trip + old-file compat. |
| `src/ui/CuratorComposition.cs` | Register `IGameDirModsHost`; wire receipt seam. |
| `src/ui/ViewModels/ShellViewModel.cs` | `GameDirConflict` branch: modal via `IDialogService`, Proceed -> `TakeOver` + one retry, Keep-setup -> persist pref + one retry, Cancel -> abort; overlay/attempt state machine held through the modal exactly like failure dialogs. |
| `src/ui/IDialogService`/`DialogService` + new dialog view/VM | `ShowGameDirConflictAsync` returning the three-way choice; styled after `ConfirmDialog` (ESC = Cancel via `EscapeClosesBehavior`). |
| `src/ui/ViewModels/PreferencesViewModel.cs` + `PreferencesView` + `Strings.resx` | Experimental toggle + localized copy. |
| Tests | Profiles: marker written/rewritten per pass. RelayClient: every ladder row, `modPath` switch, external removal, conflict result shape, Linux path translation. UI: modal branch, all three choices, retry-once guard, attempt-state invariants. Config + General: round-trips + old-file compat. |
| Docs (same PR) | `AGENTS.md`, root `README.md`, `docs/architecture/MODIFICUS-CURATOR.md`, `docs/reference/` for profiles + relay-client + config + general + ui: replace "no game-directory footprint" language with the one-link story (no patched game files, no copies, one self-identifying opt-in link, vanilla Steam launches stay vanilla). |

## Design notes

- `IGameDirModsHost` exists because it has two real consumers in different
  layers (launch orchestration reads the ladder; the shell performs takeover
  after consent) plus test fakes; a static or UI-owned helper would leak
  game-dir mutation knowledge into the shell.
- The conflict is reported through `LaunchResult`, not a callback, so all
  game-dir mutation stays inside the launch path and the UI handles it like
  `DiscoveryIncomplete` (modal, then retry).
- Marker write lives in `PrepareModRoot` (Profiles already holds profile
  identity and owns the staged tree); the host only reads it.
- App version is deliberately absent from the marker: Profiles does not know
  it, and profile identity + timestamp carry the troubleshooting value.

## Acceptance criteria

1. Fresh install, no `GAME_DIR\mods`: modded launch creates the link
   (junction on Windows, symlink on Linux) pointing at the active profile's
   staged `mods\`; `--mod-path` = `GAME_DIR`; no modal.
2. Profile switch then launch: link silently re-points; marker reflects the
   newly active profile.
3. Pre-existing real `mods` folder (manual DMF user upgrading): launch returns
   `GameDirConflict`; Proceed renames to `mods_<timestamp>` with `README.txt`
   inside + receipt persisted; retry launches hosted.
4. User-made junction/symlink without our marker and outside the profiles
   root: treated as foreign (modal), never claimed or deleted.
5. Dead link whose target is under the profiles root: silently re-created.
6. Toggle external on: next launch serves from staging with the old
   `--mod-path` and removes a Curator-owned link if present; a foreign entry
   is never touched in this mode.
7. Cancel aborts; Keep-setup persists the preference and launches externally,
   both without game-dir mutation.
8. Staging rebuilds never delete repository content through the game-dir link
   (existing data-safety tests still pass).
9. `dotnet build` + `dotnet test` green on the solution.

## Out of scope

Copy tier for reparse-incapable filesystems (exFAT), importing a foreign
folder's mods into the repository, per-profile hosting toggles, uninstall
changes (data preservation already covers the staged tree; the link resolves
to surviving data), Gaming Mode interactions (hosting is not
desktop-dependent).

## Version grounding

.NET 10, Avalonia 12, no new packages. `Directory.ResolveLinkTarget` (BCL,
.NET 6+) resolves both junctions and symlinks for the ownership read; junction
creation reuses the existing Profiles `StagingLinkCreator` platform seam. The
dialog follows the existing `ConfirmDialog` pattern (AVLN3001-clean,
ESC-through-behavior opt-in).

## Implementation note for the coder

This is contract-surface work: the final report must include the design
reflection (judgment calls where a contract could have been a leak or vice
versa, framed against over-abstraction / fake-member satisfaction /
kitchen-sink / policy-on-mechanism), per the repo engineering standards.
