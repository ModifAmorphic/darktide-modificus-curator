# Steam (`Modificus.Curator.Steam`) -- reference

> Steam / Darktide / Proton discovery and game-running detection. Steam
> **discovers** everything needed to launch Darktide modded on the current OS
> and reports missing pieces via the result's nullable fields; it does NOT set
> env vars or invoke Proton -- that is [relay-client](relay-client.md)'s
> job.

## Public surface

### `ISteamService`

```csharp
public interface ISteamService
{
    DiscoveryResult Discover();
    bool IsGameRunning();
}
```

- `Discover()`: runs the **validate + heal + persist** pipeline. Reads the
  live `CuratorConfig.Discovery` user overrides (via `IConfigLoader`), checks
  each platform-relevant field's path on disk (a directory for Steam install +
  compatdata; a file for the Darktide binary + Proton script), and treats an
  existing override as **valid** (kept as-is). A null / whitespace / non-
  existent override **needs healing**: if any field needs healing the platform
  `ISteamDiscoverer` runs once and each healing field picks up the discoverer's
  value (which may itself be null, the "still missing" case). Healed fields are
  then persisted back to `Discovery.User*Path` (a single read-modify-save
  carrying only the healed writes; valid fields + any concurrent hand-edit are
  preserved). When every field is valid the discoverer is skipped entirely
  (fast path). **Never throws on missing pieces**; those are reported via
  `DiscoveryResult.Status` and the nullable fields (the escape hatch the UI
  prompts against). `SteamService` itself contains no platform dispatch.
- `IsGameRunning()` -- cross-platform best-effort check against Darktide's process
  name. Delegates to the platform `IProcessLookup`; never throws -- enumeration
  failures degrade to "not running."

### `DiscoveryResult`

The outcome of a discovery pass. Fields are nullable: a null means "couldn't
resolve this -- the UI should prompt for it."

```csharp
public sealed record DiscoveryResult(
    string? SteamInstallPath,          // Steam client dir → STEAM_COMPAT_CLIENT_INSTALL_PATH
    string? DarktideGameBinaryPath,    // native path to Darktide.exe
    string? CompatdataPath,            // Wine prefix → STEAM_COMPAT_DATA_PATH (Linux only)
    string? ProtonBinaryPath,          // the proton script for `proton run` (Linux only)
    string? ProtonVersion,             // informational label, e.g. "Proton - Experimental"
    DiscoveryStatus Status,            // Complete / Partial / Failed
    IReadOnlyList<string> Warnings);   // non-fatal notes (Flatpak, Proton-selection reason, …)
```

- `DarktideGameBinaryPath` is the native OS path; relay-client Z:\-translates
  it on Linux for `--game-binary`.
- `CompatdataPath` / `ProtonBinaryPath` / `ProtonVersion` are null **by design**
  on Windows (native -- not used).

```csharp
public enum DiscoveryStatus { Complete, Partial, Failed }
public enum DiscoveryPlatform { Linux, Windows }
```

- **Complete** -- every critical field for the current OS is non-null.
- **Partial** -- Steam located but some critical field is missing (the nullables
  indicate what to prompt for).
- **Failed** -- could not even locate Steam (prompt for the Steam dir).

`DiscoveryPlatform` is the platform discovery runs against; production detects it
from the runtime OS, tests can force it to exercise cross-platform logic on one
OS. Darktide ships on Windows (native) and Linux (Proton) only.

### Injectable seams

The discovery pipeline is fully exercisable against synthetic layouts because
every OS-specific input + platform seam is injected:

- `SteamDiscoveryOptions` -- the candidate Steam roots + auxiliary paths (so the
  discoverer never hardcodes `~/.local/share/Steam`) and `Platform`. Production
  wires the real OS defaults via `CreateDefault()`; tests inject fixture paths.
  Notable fields: `LinuxDefaultSteamRoot`, `LinuxFlatpakSteamRoot`,
  `LinuxCompatibilityToolsDir`, `WindowsDefaultSteamRoot`, `DarktideAppId`
  (`1361210`), `DarktideCommonDir`, `GameBinaryName`, `GameProcessName`.
- `ISteamDiscoverer` (internal) -- `Discover() → DiscoveryResult`. The
  platform-specific discovery strategy. Two implementations
  (`LinuxSteamDiscoverer`, `WindowsSteamDiscoverer`), selected once at DI time
  from `SteamDiscoveryOptions.Platform` (see [Cross-platform notes](#cross-platform-notes)).
- `SteamDiscoveryCore` (internal) -- the shared, platform-agnostic mechanics
  (root resolution, `libraryfolders.vdf` reading, Darktide probing, the all-null
  failure result) that both discoverers compose, **plus the single completeness
  rule** `ComputeStatus(platform, steam, darktide, compatdata, proton)` used by
  both discoverers (when building their result) and by `SteamService` (when
  computing `Status` from the final post-validate + post-heal field values).
  Consolidating the rule here guarantees the recomputed status is, by
  construction, the same rule the discoverer used; the discoverers' per-platform
  `StatusForLinux` / `StatusForWindows` helpers were extracted into it. This is
  composition, not inheritance -- each discoverer injects the core and layers its
  own platform steps on top.
- `ISteamRegistryReader` -- reads the Windows registry for the Steam install path
  (`GetSteamPath()` → `HKCU\Software\Valve\Steam\SteamPath`, or null if
  unreadable) and **normalizes it at the read boundary**: Steam's cross-platform
  client stores the value Unix-style (lowercase drive + forward slashes, e.g.
  `c:/program files (x86)/steam`), so the reader uppercases the leading drive
  letter and swaps `/` → `\` (via the platform-neutral
  `SteamPathNormalizer.NormalizeWindowsPath`) so the returned path is always
  native Windows form regardless of how Steam wrote it. Idempotent. Abstracted so
  the Windows discoverer's registry resolution is mockable on Linux CI. The
  production `SteamRegistryReader` is Windows-only (annotated
  `[SupportedOSPlatform("windows")]`) and is registered **only on Windows** -- on
  Linux it is intentionally absent so resolving it fails fast.
- `IProcessLookup` -- `IsRunning(processName)`; two production implementations,
  selected once at DI time from the host OS (see [Cross-platform notes](#cross-platform-notes)).

## Discovery behavior

`SteamService.Discover()` runs a four-step **validate + heal + persist** pipeline
per call (all platform logic lives in the discoverer + the shared
`SteamDiscoveryCore` it composes):

1. **Validate** -- read the live `CuratorConfig.Discovery` user overrides (one
   `IConfigLoader.Load()` per call). For each platform-relevant field, check the
   override's path on disk: `Directory.Exists` for Steam install + compatdata;
   `File.Exists` for the Darktide binary + Proton script. An existing override
   is **valid** (kept as-is); a null / whitespace / non-existent override
   **needs healing**. On Windows the compatdata + Proton fields are Linux-only
   and are neither validated nor healed (they stay null in the result; any
   leftover config values are preserved untouched).
2. **Heal** -- if any field needs healing, run the platform discoverer once and
   let each healing field pick up the discoverer's value for it. A field the
   discoverer also cannot resolve stays null ("still missing"). **Fast path:**
   when every platform-relevant field is valid, the discoverer is skipped
   entirely (no I/O beyond the existence checks).
3. **Selectively persist** -- if any field was healed to a non-null value,
   re-read the config fresh and write **only the healed fields'** `User*Path`
   back through `IConfigLoader.Save`. Valid fields are NOT overwritten
   (preserving the user's choice), and a hand-edit on disk between the
   top-of-call read and the save is preserved too (the read-modify-save starts
   from the current file, not the stale snapshot).
4. **Return** -- build a `DiscoveryResult` with the final paths (valid + healed)
   and a status computed via `SteamDiscoveryCore.ComputeStatus` (the same rule
   the discoverer used). `ProtonVersion` is carried only when Proton was healed
   from the discoverer (the auto label describes the discoverer's path); a
   valid user override drops the label (it may not describe the user-chosen
   path).

**Caller contract:**

- The composition root calls `Discover()` at startup (non-blocking). A missing-
  fields result is logged as a warning so the user can still use the app; they
  just cannot launch until resolved (the launch-time `Discover()` re-checks and
  surfaces the escape-hatch when incomplete).
- [relay-client](relay-client.md)'s `RelayLaunchService.Launch()`
  calls `Discover()` at launch (blocking). A missing-fields result yields
  `LaunchResult.Status = DiscoveryIncomplete`, surfacing the escape-hatch modal.
- The Settings destination reads `DiscoveryConfig` directly (now populated by the
  startup `Discover()`), so the discovery fields show the current paths rather
  than blanks.

### Linux (`LinuxSteamDiscoverer`)

1. **Steam root** -- ordered candidates: native default (`~/.local/share/Steam`)
   first, then Flatpak (`~/.var/app/com.valvesoftware.Steam/data/Steam`). The
   first whose `steamapps/libraryfolders.vdf` exists wins; resolving Flatpak
   raises a warning. A missing root (no candidate carries a valid VDF) → `Failed`.
2. **Libraries** -- parses `libraryfolders.vdf` (multi-library) via the internal
   `LibraryFoldersVdf` parser; always includes the Steam root itself as a
   fallback (the VDF usually lists itself as library "0"). (Both steps are
   `SteamDiscoveryCore` mechanics, shared with the Windows path.)
3. **Darktide** -- `<lib>/steamapps/common/<DarktideCommonDir>/binaries/<GameBinaryName>`
   probed across every library; first hit wins. (Shared `SteamDiscoveryCore` step.)
4. **Compatdata** -- `steamapps/compatdata/<DarktideAppId>/` probed on the main
   install first, then each library in VDF order (the prefix frequently lives on
   a library drive, not the main install); first existing dir wins.
5. **Proton** (heuristic -- deep Steam per-game config parsing is out of v1):
    1. `Proton - Experimental` in `steamapps/common` (common default).
   2. The highest-versioned `Proton X.Y` in `steamapps/common`.
   3. The highest-versioned build in `compatibilitytools.d` (ProtonUp-GE).
   4. Nothing → `null` (escape hatch; UI prompts).

   The chosen source is recorded in `Warnings`. Status is `Complete` only if
   Steam + Darktide + compatdata + Proton all resolve.

### Windows (`WindowsSteamDiscoverer`)

Registry first (`ISteamRegistryReader` -- authoritative when present), then the
default path (`C:\Program Files (x86)\Steam`); the resolved source is recorded.
Same multi-library `libraryfolders.vdf` parse + Darktide probe (shared core).
Compatdata/Proton are null (native -- unused). Status is `Complete` only if Steam
+ Darktide resolve.

### VDF parsing (`LibraryFoldersVdf`, internal)

A minimal regex parser for Steam's `libraryfolders.vdf` -- extracts the library
root `"path"` values in document order (enough to drive multi-library discovery
without a heavyweight VDF dependency). Unescapes `\\` → `\` and `\"` → `"`
(Windows VDF uses C-style escapes; Linux Steam writes forward slashes). Visible
to tests via `InternalsVisibleTo`.

## Cross-platform notes

There are two independent platform selections, made once each at DI registration
by `AddSteam()` -- neither leaves a per-call OS branch inside the service:

| Collaborator | Selected from | Implementations |
| --- | --- | --- |
| `ISteamDiscoverer` | `SteamDiscoveryOptions.Platform` (overridable) | `LinuxSteamDiscoverer`, `WindowsSteamDiscoverer` |
| `IProcessLookup` | host runtime OS | `LinuxProcessLookup`, `WinProcessLookup` |

The discoverer follows the **`Platform` option, not the runtime OS**, on purpose:
the `Platform` knob exists precisely so cross-OS testing works -- a fixture forces
`Platform = Windows` and the Windows discoverer runs on Linux CI (and vice
 versa). `IsGameRunning` has no such option, so `IProcessLookup` is picked from
the host OS.

### `IProcessLookup`

| Host | Implementation | How it matches |
| --- | --- | --- |
| Linux | `LinuxProcessLookup` | scans `/proc/<pid>/cmdline`, compares the `argv[0]` basename-stem |
| Windows | `WinProcessLookup` | `Process.GetProcessesByName(processName)` (process comm) |

`LinuxProcessLookup` reads `argv[0]` because the kernel `comm` field (what
`GetProcessesByName` reads on Unix, capped at 15 chars) is **unreliable under
Proton** -- Darktide's `comm` is literally `main`, which would yield a false
negative while the game is running.

The load-bearing detail is `MatchesArgv0`: under Proton/wine the launched exe's
`argv[0]` is a **Windows-style** path (`S:\...\Darktide.exe`).
`Path.GetFileNameWithoutExtension` only recognizes the *current* runtime's
directory separators, so on Linux it would not split on backslashes and would
yield the wrong stem. `MatchesArgv0` normalizes backslashes → slashes first so
stem extraction is correct on both OSes. It matches `argv[0]` only -- a
whole-cmdline substring match is a known false-positive trap (it would match the
`steam.exe` wrapper and the detector itself).

Both implementations swallow enumeration failures (permission denied, exited
processes, procfs unavailable) as "not running" so a launch is never blocked on
a false negative. `WinProcessLookup` catches `Win32Exception` /
`InvalidOperationException`; `LinuxProcessLookup`'s outer try/catch around
`Directory.EnumerateDirectories("/proc")` is load-bearing (an eager existence
check raises there, not during enumeration).

## DI registration

```csharp
public static IServiceCollection AddSteam(this IServiceCollection services)
{
    services.TryAddSingleton(_ => SteamDiscoveryOptions.CreateDefault());
    services.TryAddSingleton<SteamDiscoveryCore>();

    // Discoverer follows the (overridable) Platform knob, NOT the runtime OS.
    services.TryAddSingleton<ISteamDiscoverer>(sp =>
        sp.GetRequiredService<SteamDiscoveryOptions>().Platform == DiscoveryPlatform.Linux
            ? new LinuxSteamDiscoverer(...)   // core + options + logger
            : new WindowsSteamDiscoverer(...)); // core + options + ISteamRegistryReader + logger

    // Windows-only capability: NOT registered on Linux (fail-fast if resolved).
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        services.TryAddSingleton<ISteamRegistryReader, SteamRegistryReader>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        services.TryAddSingleton<IProcessLookup, LinuxProcessLookup>();
    else
        services.TryAddSingleton<IProcessLookup, WinProcessLookup>();

    services.AddSingleton<ISteamService, SteamService>();
    return services;
}
```

`SteamDiscoveryOptions`, `SteamDiscoveryCore`, `ISteamDiscoverer`,
`ISteamRegistryReader`, and `IProcessLookup` are all `TryAdd` so tests (and hosts
with custom paths) can pre-register overrides -- the discovery pipeline is then
fully exercisable against fixture layouts (e.g. the Steam fixture pre-registers
its `FakeRegistryReader` + forces `Platform = Windows`, which drives the discoverer
selection so the Windows path runs on Linux CI). `ISteamService` is `AddSingleton`
(holds no per-call state).

Note: `AddSteam()` does **not** register `IConfigLoader` itself. `SteamService`
depends on `IConfigLoader` (it reads + writes `CuratorConfig.Discovery` live on
each `Discover()` call so a Settings / escape-hatch / hand-edit write is visible
immediately), so an `IConfigLoader` must be registered externally before
resolving `ISteamService`. In production that is [General](general.md)'s
`AddGeneral()` (which `TryAdd`s `ConfigLoader`); tests register a fake (e.g. the
validate + heal + persist tests register a `FakeConfigLoader` whose `Save`
mirrors the real loader's round-trip so a subsequent `Load` sees the saved
state).

`SteamRegistryReader` is Windows-only: no `Microsoft.Win32.Registry` package is
required (on `net10.0` the `Registry` type is in the reference assembly, gated
behind `[SupportedOSPlatform("windows")]`), and the reader is annotated
`[SupportedOSPlatform("windows")]` at the type level (for CA1416, with no
per-call runtime guard). It is registered **only on Windows** by `AddSteam()`;
on Linux it is intentionally absent so resolving `ISteamRegistryReader` fails
fast (the honest outcome for a Windows-only capability, rather than a silent
no-op).

## Dependencies

- **Curator libraries:** [config](config.md) (`DiscoveryConfig`, the user-override
  section of `CuratorConfig` validated + healed + persisted by `Discover()`) +
  [general](general.md) (`IConfigLoader`, the live reader/writer `SteamService`
  reads `Discovery` from and writes the healed fields back to on each `Discover()`
  call).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Modificus.Curator.Steam.Tests` covers Linux discovery (`LinuxDiscoveryTests`,
`FlatpakDiscoveryTests`), Windows discovery (`WindowsDiscoveryTests`), Proton
selection (`ProtonSelectionTests`), the `libraryfolders.vdf` parser
(`LibraryFoldersVdfTests`), game-running detection (`GameRunningTests`,
`ArgvMatchTests` -- the latter pinning the `MatchesArgv0` backslash normalization),
the `SteamPathNormalizer` pure helper (`SteamPathNormalizationTests`),
the `AddSteam` DI wiring (the `TryAdd` overrides, the `ISteamDiscoverer`
selection by `SteamDiscoveryOptions.Platform`, and the platform `IProcessLookup`
selection), and the `SteamService.Discover()` validate + heal + persist
pipeline (`SteamServiceOverlayTests`: every field valid skips the discoverer
entirely; missing fields are healed from the discoverer + persisted; the
selective save writes only the healed fields while preserving valid fields +
concurrent hand-edits; unresolvable fields stay null and flag `Status =
Partial`; on Windows only Steam + Darktide are checked + healed; the
`ProtonVersion` side-effect when a valid override takes the Proton field; and
the live-read contract that makes a config write between calls visible on the
next `Discover()`). `WindowsDiscoveryTests` force `Platform = Windows` + a fake
registry reader so the Windows discoverer path runs on Linux CI -- the
load-bearing proof that discoverer selection follows `Platform`, not the runtime
OS.

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md) -- the
  [Launch](../architecture/MODIFICUS-CURATOR.md#launch) section (the Linux
  discovery + escape-hatch + fail-fast design).
- [relay-client](relay-client.md) -- consumes `DiscoveryResult` to invoke
  the launcher.
