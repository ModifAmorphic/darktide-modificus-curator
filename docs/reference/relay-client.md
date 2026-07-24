# Relay-client (`Modificus.Curator.RelayClient`) -- reference

> The v1 launch façade over Mod Relay. Resolves the profile + Steam
> discovery, assembles the launcher args, and invokes `mod_relay.exe` --
> directly on Windows, under `proton run` on Linux. Fire-and-forget in v1: it
> starts the launcher and returns; it does not track the game process.

## Public surface

### `IRelayLaunchService`

```csharp
public interface IRelayLaunchService
{
    LaunchResult Launch(Guid profileId);
}
```

`Launch(profileId)` always returns a `LaunchResult` -- it never throws for
expected conditions:

- Resolves Steam discovery first (`ISteamService.Discover()`). If discovery is
  missing required fields for the current OS, returns `DiscoveryIncomplete`
  **without** writing the profile's mod root (no point writing `mods.lst` for a
  launch that won't happen) -- `MissingDiscoveryFields` lists them. The
  per-platform required set comes from the active `IPlatformLaunchStrategy`.
- Prepares the mod root (`IProfileService.PrepareModRoot(profileId)` -- writes
  `mods.lst` and returns the `--mod-path`). A staging-link creation failure
  (the raised built-in exception: `Win32Exception` from the junction path on
  Windows, `IOException` / `UnauthorizedAccessException` from the symlink path
  on Linux) is caught here and mapped to `StagingFailed`, carrying the
  exception's body on `Message` (the full exception is also logged). An unknown
  profile (`KeyNotFoundException` from PrepareModRoot) is caught and mapped to
  `Error`.
- Reads the profile's launch settings (`IProfileService.GetLaunchSettings`) fresh
  on each launch + passes them to the strategy (environment merge + game-arg
  emission). No Relay version preflight: `--` + game args are emitted
  unconditionally when the profile has them (Curator bundles Relay >= the `--`
  contract; a deliberately-old external `RelayDir` is an accepted failure).
- Resolves the launcher path (see [Launcher path resolution](#launcher-path-resolution)).
  If it cannot be found in any applicable location, returns `Error` reporting the
  configured `<RelayDir>/mod_relay.exe` path.
- Spawns the launcher via the active `IPlatformLaunchStrategy` (directly on
  Windows; under `proton run` on Linux) -- the service itself contains no
  per-launch OS branch.

```csharp
public sealed record LaunchResult(
    LaunchStatus Status,
    string? Message,                         // populated for Error + StagingFailed
    IReadOnlyList<string> MissingDiscoveryFields);  // populated only for DiscoveryIncomplete

public enum LaunchStatus { Launched, DiscoveryIncomplete, StagingFailed, Error }
```

- `Launched` -- the launcher process started (fire-and-forget; no game-process tracking in v1).
- `DiscoveryIncomplete` -- discovery is missing required fields; the field names
  mirror `DiscoveryResult` properties so the UI can map them to a prompt.
- `StagingFailed` -- the profile's mod root could not be prepared (a staging
  link could not be created). `Message` carries the raised exception's body (a
  runtime/OS error); the UI surfaces it after the localized framing.
- `Error` -- unknown profile, missing runtime dir, or process-start failure; see
  `Message`.

`MissingDiscoveryFields` is derived from the `DiscoveryResult` fields directly
(both platforms need Steam + the game binary; Linux additionally needs compatdata
+ Proton), so it and `DiscoveryStatus` cannot diverge -- it is equivalent to
`Status != Complete`. The per-platform required set is owned by the active
`IPlatformLaunchStrategy` (`RequiredDiscoveryFields`).

### Injectable seams

- `IPlatformLaunchStrategy` (internal) -- the per-platform launch surface:
  - `RequiredDiscoveryFields(discovery)` -- the discovery fields this platform
    requires but could not resolve (Windows: Steam + game binary; Linux: +
    compatdata + Proton).
  - `Start(launcherPath, discovery, gameBinary, modPath, logFile, launchSettings) → bool`
    -- the spawn. Windows: a direct invocation of the launcher with native
    (untranslated) args; Linux: `<proton> run <launcher.exe> <args>` with both
    `STEAM_COMPAT_*` env vars and the path-valued flags `Z:\`-translated. The
    `launchSettings` parameter carries the profile's environment variables
    (merged into the spawn request) + game arguments (appended after the
    launcher's own flags as a bare `--` separator then one argv entry each).
  - `Name` -- a short label ("Windows" / "Linux") for log messages.
  - Two implementations (`WindowsLaunchStrategy`, `LinuxLaunchStrategy`),
    selected once at DI time from the host OS (see
    [Cross-platform notes](#cross-platform-notes)).
- `IProcessLauncher` -- `Start(ProcessLaunchRequest) → bool` (fire-and-forget;
  `true` if started, `false` if it could not start -- never throws). Abstracted
  so the launch path is deterministic and mockable in tests (the real
  `Process.Start` would spawn a real process). Injected into the strategy (not
  the service) so tests can fake the spawn. The default `ProcessLauncher` builds
  a `ProcessStartInfo` with `UseShellExecute = false`, strips every key the
  request lists in `EnvironmentVariablesToRemove` from the inherited parent
  environment, then applies each `EnvironmentOverrides` entry on top (overrides
  win when a key appears in both sets), and adds each argument verbatim to
  `ArgumentList` (argv-correct, no shell, no injection surface). The
  deterministic `ProcessStartInfo` construction is factored into an internal
  `BuildStartInfo(ProcessLaunchRequest)` pure helper that production `Start`
  uses verbatim, so tests can inspect the final environment + argument layout
  without spawning a real process.

  The request is the immutable public `ProcessLaunchRequest` sealed object
  (`FilePath`, `Arguments`, `EnvironmentOverrides`, `EnvironmentVariablesToRemove`).
  Collections are snapshotted into genuinely immutable containers at construction
  and exposed as empty collections rather than `null`. The single-value API
  keeps the launcher straightforward for the two strategies and any future
  per-profile environment overrides.

`WinePath.ToWine(posixPath)` (internal) translates an absolute POSIX path to its
Wine `Z:\` form (`/` → `\`, `Z:` prefix) for the launcher-under-Wine flags; it is
used only by `LinuxLaunchStrategy`.

## Cross-platform notes

The launch path branches on platform via the active `IPlatformLaunchStrategy`,
selected once at DI registration from the host OS -- the launch service contains
no per-launch OS branch. Each strategy owns the spawn (via `IProcessLauncher`),
its required discovery fields, and its own log label.

### Launcher path resolution

`RelayLaunchService.ResolveLauncherPath` resolves `mod_relay.exe` with a
fixed precedence, then the service spawns whatever it resolved via the active
strategy:

1. **Configured `CuratorConfig.RelayDir`** -- `<RelayDir>/mod_relay.exe`.
   Honors an explicit user override and the data-root default once Relay is
   deployed there (the Linux layout, and the Windows dev/data layout).
2. **App-local fallback (Windows and Linux)** --
   `<AppContext.BaseDirectory>/relay/mod_relay.exe`. Velopack packages
   Relay app-local inside the payload. On Windows this is under the installed
   `current\` directory; on Linux it is under the mounted AppImage's `usr/bin/`
   payload. Velopack replaces the owning package on update.
3. **Sibling-folder fallback (Windows only)** -- `<AppContext.BaseDirectory>/../relay/
   mod_relay.exe` (normalized to no `..` segment). The portable Windows
   archive ships Curator under `<root>/app/` and Relay under `<root>/relay/` (a
   sibling of the app folder, mirroring the Linux layout), so Relay resolves
   without a config override.
4. **`null`** -- none of the applicable locations had the launcher; the service
   returns `Error` reporting the configured path.

Linux uses the configured `RelayDir` first, preserving the standalone tarball
layout and user overrides. If that launcher is absent, it uses the app-local
AppImage payload. Linux does not use the Windows portable sibling fallback.
The helper is a pure function of `(configRelayDir, baseDirectory, isWindows)`
so the precedence is unit-testable on any CI OS.

### Windows -- direct invocation

`Process.Start(mod_relay.exe, args)`. No Proton, no path translation --
native Windows paths. Args: `--game-binary`, `--mod-path`, `--log-file`
(verbatim, untranslated; the value is Curator's startup-resolved, process-pinned
log path from `LoggingBootstrap.CurrentLogFile`, so Relay writes the same
per-process file as Curator, with the configured `Logging.LogFile` only a
fallback when the bootstrap has not run); then, when the profile's `EnableLuaLogs` toggle is on,
a bare `--lua-logs` flag (tees Lua `print` output into the log file; no value,
appended right after `--log-file`); then (when the profile has game arguments)
one bare `--` + each game arg as its own argv entry. The profile's environment
variables are applied as overrides on the Relay process; Relay creates Darktide
with an inherited environment, so the values reach the game. No removals (Windows
never runs from an AppImage mount, so there is nothing to sanitize); an empty
profile env yields no overrides (the child inherits Curator's environment
verbatim).

### Linux -- native Curator + Proton-at-launch

Curator runs natively (not Proton-wrapped); `mod_relay.exe` is a Windows
binary, so Curator invokes it under **Proton**, using **Darktide's own compatdata**
as the prefix (required -- the launcher `CreateProcess`es Darktide, so the two
must share the prefix). Proton reads `STEAM_COMPAT_DATA_PATH` from the
environment *before* the launcher runs; it cannot be passed as a launcher flag,
so Curator sets it in the environment when it invokes Proton.

Command: `<proton> run <launcher.exe> <args>`, where:

- The `proton` command + the launcher.exe path are **native Linux paths**
  (Proton resolves the `.exe` from a native path).
- The launcher's *own* path-valued flags (`--game-binary`, `--mod-path`,
  `--log-file`) are **`Z:\`-translated** (the launcher runs under Wine and needs
  Windows paths) -- including `--log-file`, otherwise the Relay shell log
  couldn't be written where Curator expects. The `--log-file` value is Curator's
  startup-resolved, process-pinned path (`LoggingBootstrap.CurrentLogFile`), so
  Relay writes the same per-process file as Curator. When the profile's `EnableLuaLogs`
  toggle is on, a bare `--lua-logs` flag is appended right after `--log-file`
  (a Relay-owned logging flag with no value, so it is NOT path-valued and is not
  `Z:\`-translated).
- Environment: `STEAM_COMPAT_DATA_PATH = <compatdata>` (the Wine prefix) +
  `STEAM_COMPAT_CLIENT_INSTALL_PATH = <steam-install>` -- both required for Proton
  to use the right prefix and find the Steam client. Discovery guaranteed both
  non-null above. These are applied as overrides AFTER the AppImage-identity
  removals (below) AND after the profile environment values, so they win even
  if a key happened to collide (the reserved-name validation block makes that
  impossible in normal use; the layering is defense in depth).

#### Launch settings merge (environment + game arguments)

The profile's launch settings (`GetLaunchSettings`, read fresh on each launch)
are merged into the spawn request so Curator-owned values always win:

1. **Inherited Curator environment** (the request's implicit base, snapshotted
   lazily by `ProcessLauncher`).
2. **AppImage identity removals** (`EnvironmentVariablesToRemove`; see below).
3. **Profile environment values** (as `EnvironmentOverrides`).
4. **Curator-owned `STEAM_COMPAT_*`** as `EnvironmentOverrides` layered LAST, so
   they win even though validation already blocks reserved names (defense in
   depth).

On Windows the merge is simpler: profile environment values as overrides on the
Relay process (no Steam-compat vars, no removals).

**Game arguments** follow Relay's bare-`--` contract (item 4): when the profile's
`GameArguments` is non-empty, Curator appends one `--` element to the launcher's
argv, then each game argument as its own `ArgumentList` entry (verbatim, in
order). When empty, no `--` is emitted (legacy launch). Curator uses
`ProcessStartInfo.ArgumentList` throughout; it never prequotes or joins the
values -- Relay owns the final Windows `CreateProcess` quoting. No Relay version
preflight is performed.

**Lua logging:** when the profile's `EnableLuaLogs` toggle is on, Curator appends
the bare `--lua-logs` flag right after `--log-file` (before any `--` + game
args). It tees Lua `print` output (the mod loader, DMF, and mods) into the
`--log-file` Curator always emits; it is a tee, not a redirect. The flag carries
no value, so on Linux it is NOT `Z:\`-translated (only `--game-binary`,
`--mod-path`, `--log-file` are path-valued). Its Relay env form
`RELAY_LUA_LOGS` is reserved so the toggle is the single source of truth.

#### AppImage desktop-identity sanitization

When Curator is launched from its installed AppImage, the AppImage runtime
exports a handful of variables into Curator's environment (`APPDIR`,
`APPIMAGE`, `ARGV0`, `OWD`, plus the desktop hint `BAMF_DESKTOP_FILE_HINT`).
KDE Plasma's task manager reads `BAMF_DESKTOP_FILE_HINT` and then `APPDIR`
from `/proc/<pid>/environ` to resolve a child's desktop identity, so if those
leak through `proton run` into Relay and Darktide, the game window is grouped
under Curator's launcher.

To stop that, `LinuxLaunchStrategy` requests the launcher strip exactly those
five keys from the inherited environment (`LinuxLaunchStrategy.AppImageIdentityVariables`):
`APPDIR`, `APPIMAGE`, `ARGV0`, `OWD`, `BAMF_DESKTOP_FILE_HINT`. Only those are
removed; every unrelated inherited variable passes through unchanged. The
desktop-activation tokens `DESKTOP_STARTUP_ID`, `XDG_ACTIVATION_TOKEN`, and
`GIO_LAUNCHED_DESKTOP_FILE` are intentionally NOT removed. The two
`STEAM_COMPAT_*` overrides are applied AFTER the removals (and after the profile
environment values) so they always win. On a non-AppImage launch (the standalone
tarball, a dev build) none of those keys are present, so the removals are silent
no-ops. The five AppImage-identity names are also in the profile launch-settings
reserved set, so a profile cannot re-add them.

### `--log-level` is intentionally not emitted

`CuratorConfig.Logging.Level` is a Serilog level name
(`Verbose`/`Information`/`Warning`/`Fatal`) for Curator's own log, but the Relay
shell's level vocabulary is `error`/`warn`/`info`/`debug`/`trace`. Forwarding the
Serilog name silently mis-resolved 4 of 6 levels (e.g. `Warning` → shell `info`,
more noise than intended). The two logs serve different purposes; the shell log
level is decoupled and the launcher's `info` default is used. A dedicated
shell-level config field can be added if a future need arises.

## DI registration

```csharp
public static IServiceCollection AddRelayClient(this IServiceCollection services)
{
    services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        services.TryAddSingleton<IPlatformLaunchStrategy, WindowsLaunchStrategy>();
    else
        services.TryAddSingleton<IPlatformLaunchStrategy, LinuxLaunchStrategy>();

    services.AddSingleton<IRelayLaunchService, RelayLaunchService>();
    return services;
}
```

`IProcessLauncher` and `IPlatformLaunchStrategy` are `TryAdd` so tests (and hosts
wiring a custom launch hook) can pre-register an override before calling
`AddRelayClient` -- the same pattern the Steam library uses for its platform
seams. The strategy is selected once, here, from the host OS, so the launch
service contains no per-call OS branch. `IRelayLaunchService` is `AddSingleton`
(holds no per-launch state). Resolves `IProfileService`, `ISteamService`,
`CuratorConfig`, `IPlatformLaunchStrategy`, and `ILogger<RelayLaunchService>`
from the container.

## Dependencies

- **Curator libraries:** `config` (`RelayDir`; `Logging.LogFile` as the fallback
  log path), `general` (`LoggingBootstrap.CurrentLogFile`, the startup-resolved,
  process-pinned log path forwarded to Relay as `--log-file`, reached
  transitively via `steam`), `profiles` (`IProfileService.PrepareModRoot` +
  `GetLaunchSettings`), `steam` (`ISteamService.Discover`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Modificus.Curator.RelayClient.Tests` is a **dual-purpose** project. `dotnet
test` runs the xUnit suite -- `RelayLaunchServiceTests` (Windows + Linux arg
assembly via the concrete `WindowsLaunchStrategy` / `LinuxLaunchStrategy` + a
fake `IProcessLauncher`, `DiscoveryIncomplete` missing-field derivation,
`StagingFailed` + `Error` mapping, plus the Linux AppImage-identity removal set
and the Windows empty-removal/override assertion, plus the launch-settings merge --
Linux profile env before Proton startup alongside the AppImage removals + the
`STEAM_COMPAT_*` overrides; Windows profile env as overrides; empty/legacy when no
settings), `GameArgumentsTests` (the bare-`--` contract via the pure
`BuildLauncherArgs` seam: empty emits no `--`, multiple emit one `--` then each
arg as its own element in order, values with spaces + quotes stay one element),
`ProcessLauncherTests`
(the deterministic `ProcessLauncher.BuildStartInfo` path: a requested inherited
key is removed, an unrelated inherited key remains, an override is applied, an
override wins after removal, `UseShellExecute` is false, arguments stay distinct
including values with spaces and shell metacharacters), `WinePathTests`, the
`AddRelayClient` DI wiring, all against the
fakes in `TestDoubles.cs`. Tests inject the concrete strategy to exercise either
path on any CI OS. `dotnet run -- <discover|list|launch>` runs the **composition
smoke harness** under `SmokeHarness/Program.cs` -- it composes the **real**
services (general + profiles + steam + relay-client, no fakes) via the same
`Add<Library>()` chain the UI uses. `launch <profileId>` invokes an actual launch
against the user's Steam/Darktide setup, for user-machine validation; `discover`
reports the resolved Steam/Darktide/Proton discovery + `IsGameRunning()`; `list`
lists profiles.

```sh
dotnet test src/modificus-curator.sln -c Release          # xUnit suite
dotnet run --project src/tests/Modificus.Curator.RelayClient.Tests -- launch <profileId>
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md) -- the
  [Launch](../architecture/MODIFICUS-CURATOR.md#launch) section (the Windows /
  Linux split, the `STEAM_COMPAT_*` constraint, the escape hatch).
- [steam](steam.md) -- produces the `DiscoveryResult` this consumes.
- [profiles](profiles.md) -- `PrepareModRoot` produces the `--mod-path` + `mods.lst`.
