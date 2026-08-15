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
    DiscoveryResult Rediscover();
    bool IsGameRunning();
}
```

- `Discover()`: honors the configured discovery mode
  (`CuratorConfig.Discovery.OverrideAutomaticDiscovery`). **Automatic mode**
  (the default, `false`) runs the platform discoverer every call and atomically
  replaces the active-platform path snapshot in config with the result
  (including nulls that clear stale values); on Windows only Steam + Darktide
  are written (Linux-only fields are left untouched), on Linux all four fields
  are written. **Manual mode** (`true`) does not invoke the discoverer: the
  stored paths are validated by kind on disk (directory for Steam + compatdata;
  file for Darktide + Proton), valid values pass through, and invalid/missing
  ones surface as null result fields without rewriting the stored input
  (`ProtonVersion` is null in manual mode). **Never throws on missing pieces**;
  those are reported via `DiscoveryResult.Status` and the nullable fields (the
  escape hatch the UI prompts against). `SteamService` itself contains no
  platform dispatch.
- `Rediscover()`: forces one automatic discovery pass regardless of the
  configured mode, replaces the active-platform snapshot (including nulls),
  leaves `OverrideAutomaticDiscovery` unchanged, and returns the discoverer's
  result. The Discover-button affordance in Settings and the escape hatch both
  call this.
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
- **Partial** -- some critical field resolved but the result is not launchable
  (the nullables indicate what to prompt for).
- **Failed** -- no critical field resolved (prompt for the entry-point field).

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
  `LinuxCompatibilityToolsDir`, `LinuxSystemCompatibilityToolsDirs` (the two
  standard Steam system directories, defaulting so `/usr/share/steam/compatibilitytools.d`
  is searched), `IsSteamDeck` (a platform identity input: whether the host is a
  Steam Deck; `CreateDefault()` detects it from OS release metadata --
  `ID=steamos` + `VARIANT_ID=steamdeck`, reading `/run/host/os-release` before
  `/etc/os-release`; tests can inject a fixed value), `WindowsDefaultSteamRoot`,
  `DarktideAppId` (`1361210`), `DarktideCommonDir`, `GameBinaryName`,
  `GameProcessName`.
- `ISteamDiscoverer` (internal) -- `Discover() → DiscoveryResult`. The
  platform-specific discovery strategy. Two implementations
  (`LinuxSteamDiscoverer`, `WindowsSteamDiscoverer`), selected once at DI time
  from `SteamDiscoveryOptions.Platform` (see [Cross-platform notes](#cross-platform-notes)).
- `SteamDiscoveryCore` (internal) -- the shared, platform-agnostic mechanics
  (root resolution, `libraryfolders.vdf` reading, Darktide probing, the all-null
  failure result) that both discoverers compose, **plus the single completeness
  rule** `ComputeStatus(platform, steam, darktide, compatdata, proton)`. The
  discoverers call it when building their result and `SteamService` calls it
  when computing `Status` from the manual-mode validation, so the recomputed
  status is, by construction, the same rule the discoverer used. This is
  composition, not inheritance -- each discoverer injects the core and layers its
  own platform steps on top.
- `ProtonResolver` (internal) -- the Linux Proton compatibility-tool resolver,
  constructed by `LinuxSteamDiscoverer`. Reads Steam's tool selection (the
  app-specific `CompatToolMapping` entry, then the global `"0"` entry, then the
  appinfo recommended runtime when both mappings are absent), then resolves the
  selected name to a `proton` binary as a custom tool (a
  `compatibilitytool.vdf` manifest) or a Valve-managed tool (appinfo +
  appmanifest). See [Linux Proton resolution](#linux-proton-resolution).
- `SteamAppInfoReader` (internal) -- parses Steam's binary `appinfo.vdf`
  container (versions 39-41) in one scan that collects the first app entry
  carrying a `compat_tools` collection and the requested app's recommended
  runtime (either may be absent; a requested app id of 0 skips the runtime
  lookup so the scan stops at the first registry). Used by `ProtonResolver` for
  Valve-managed tool resolution + the no-user-mapping runtime fallback. Has an
  internal `ReadSnapshot(Stream, requestedAppId)` overload so tests feed a
  synthetic binary fixture.
- `SteamDeckDetector` (internal static) -- the production Steam Deck detection
  behind the `SteamDiscoveryOptions.IsSteamDeck` platform identity input (see
  above). Quoted values are tolerated; IO/access failures degrade to "not a
  Deck".
- `SteamTextVdf` (internal static) -- the single entry point for Steam text KV1
  parsing (`config.vdf`, `compatibilitytool.vdf`, `appmanifest_*.acf`). Wraps
  ValveKeyValue with `HasEscapeSequences = true` always on (Steam files
  routinely contain C-style escapes, and ValveKeyValue defaults that flag to
  `false`). Visible to tests via `InternalsVisibleTo`.
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

`SteamService.Discover()` branches on `CuratorConfig.Discovery.OverrideAutomaticDiscovery`
(read live via one `IConfigLoader.Load()` per call). All platform logic lives in
the discoverer + the shared `SteamDiscoveryCore` it composes; the service itself
holds only the mode policy.

### Automatic mode (default, `OverrideAutomaticDiscovery = false`)

Runs the platform discoverer every call and atomically replaces the
active-platform snapshot in config with the result:

1. **Discover** -- run the platform `ISteamDiscoverer`, which probes the
   platform-appropriate Steam install locations and resolves the Steam install,
   Darktide install, and (Linux) compatdata + Proton.
2. **Replace active-platform fields** -- a read-modify-save starting from the
   current config (so the mode bool + the inactive-platform fields survive
   untouched) overwrites only the active-platform fields with the discoverer's
   snapshot. Windows writes Steam + Darktide and leaves the Linux-only compatdata
   + Proton fields untouched; Linux writes all four. Nulls are written too, so a
   field the discoverer could not resolve clears a stale value rather than
   leaving a path that no longer reflects reality. The save is skipped when the
   snapshot already matches what is persisted (no churn on a steady-state call).
3. **Return** -- the discoverer's `DiscoveryResult`, with `Status` computed by
   `SteamDiscoveryCore.ComputeStatus` (the same rule the discoverer used).

### Manual mode (`OverrideAutomaticDiscovery = true`)

The discoverer is not invoked. The stored paths are validated by kind on disk:

- Steam install + compatdata must be existing directories.
- Darktide binary + Proton script must be existing files.

Valid values pass through unchanged; invalid/missing ones surface as null result
fields. The stored input is **never rewritten or cleared**, so an invalid manual
value stays in config exactly as the user typed it (the UI keeps showing it for
correction). `ProtonVersion` is null (no discoverer label is available). On
Windows the compatdata + Proton fields are Linux-only and are not validated;
they stay null in the result. `Status` is computed via the same
`SteamDiscoveryCore.ComputeStatus` rule, so on Windows a valid Darktide binary
alone yields `Complete` and an optional valid Steam path is returned for display
only (an absent/invalid one surfaces null in the result while the stored string
is preserved).

### `Rediscover()`

Forces one automatic pass regardless of the configured mode, replaces the
active-platform snapshot (including nulls), leaves `OverrideAutomaticDiscovery`
unchanged, and returns the discoverer's result. The Settings and escape-hatch
Discover buttons call this so a user can refresh the snapshot without first
flipping the mode.

**Caller contract:**

- The composition root calls `Discover()` at startup (non-blocking). A missing-
  fields result is logged as a warning so the user can still use the app; they
  just cannot launch until resolved (the launch-time `Discover()` re-runs and
  surfaces the escape-hatch when incomplete).
- [relay-client](relay-client.md)'s `RelayLaunchService.Launch()`
  calls `Discover()` at launch (blocking). Because automatic mode re-runs the
  discoverer every call, a launch follows changes to the Steam/Darktide/Proton
  layout live; a missing-fields result yields
  `LaunchResult.Status = DiscoveryIncomplete`, surfacing the escape-hatch modal.
- The Settings destination reads `DiscoveryConfig` directly, which automatic
  mode keeps populated with the latest snapshot.

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
5. **Proton** -- resolves the effective compatibility tool from Steam's
   `CompatToolMapping` (see [Linux Proton resolution](#linux-proton-resolution)).

   Status is `Complete` only if Steam + Darktide + compatdata + Proton all
   resolve.

### Linux Proton resolution

`ProtonResolver` resolves the tool Steam actually selected for Darktide, rather
than guessing from directory names. The steps are best-effort: a missing or
unreadable file degrades to an unresolved Proton (warning), never a throw.

1. **Selected tool name** -- `<steamRoot>/config/config.vdf` →
    `Software > Valve > Steam > CompatToolMapping`, with this precedence:
    - The app-specific mapping for Darktide's app id (`1361210`) is
      authoritative when present; its `name` is used as-is.
    - The global `"0"` mapping is considered only when the app-specific mapping
      is absent, and is authoritative when present.
    - Only when both are absent, Darktide's appinfo
      `common/steam_deck_compatibility/configuration/recommended_runtime` is
      Steam's non-user default and supplies the name on any Linux host; host
      identity (Steam Deck or not) is not consulted for this decision.
    - A present mapping whose `name` is missing, non-string, empty, or
      whitespace is **invalid**: resolution fails without falling through (this
      covers both the app-specific and the global entry, and an invalid
      app-specific entry blocks the global entry too). Likewise, a selected
      mapping whose named tool cannot be resolved stays authoritative and never
      falls through to the recommendation, and a `config.vdf` that exists but
      cannot be read or parsed fails unresolved rather than bypassing a
      possible user choice. A missing config file, or a valid config with
      neither key, counts as no user mapping and permits the recommended-runtime
      fallback.
    - A missing, empty, whitespace, `native`, or unresolvable recommendation
      yields unresolved with a warning; no other runtime is guessed.
2. **Custom tool** -- a `compatibilitytool.vdf` manifest whose `compat_tools`
    collection defines the selected name, searched across every
    compatibility-tool root in order: the resolved Steam root's
    `compatibilitytools.d`, the configured user root, then the system roots
    (including `/usr/share/steam/compatibilitytools.d`). Each root is checked
    root-level first (Valve permits a manifest directly at the root with a
    relative or absolute `install_path`), then its per-tool subdirectories. The
    resolved `install_path`'s `proton` file must exist. This runs first for any
    selected name, including the recommended runtime.
3. **Valve-managed tool** -- the `compat_tools` entry in
    `<steamRoot>/appcache/appinfo.vdf` (binary; parsed by
    `SteamAppInfoReader`) whose key or comma-separated alias matches the selected
    name, then `appmanifest_<appid>.acf` across the libraries, parsing its
    `installdir`, requiring
    `<library>/steamapps/common/<installdir>/proton` to exist. One `Resolve`
    call parses `appinfo.vdf` at most once: when the recommended runtime
    supplies the name, the snapshot already read for the recommendation is
    reused here.
4. Nothing resolves → `null` (escape hatch; UI prompts). A reason is appended to
    `Warnings`.

`ProtonVersion` carries the tool's `display_name` when present (the custom
manifest's, or the appinfo entry's), otherwise the internal tool name.

### Windows (`WindowsSteamDiscoverer`)

Registry first (`ISteamRegistryReader` -- authoritative when present), then the
default path (`C:\Program Files (x86)\Steam`); the resolved source is recorded.
Same multi-library `libraryfolders.vdf` parse + Darktide probe (shared core).
Compatdata/Proton are null (native -- unused). Steam is the discovery anchor:
the discoverer locates Darktide by walking Steam's libraries, so a missing Steam
yields `Failed` before Darktide is even probed (the automatic pass never resolves
a Darktide binary without one). The completeness rule (`ComputeStatus`) requires
only the Darktide binary on Windows, which is what lets a manual Darktide path
count as `Complete` without Steam.

### Text KV1 parsing

Steam text KeyValues1 files (`config.vdf`, `compatibilitytool.vdf`,
`appmanifest_*.acf`) are deserialized through `SteamTextVdf`, which always
enables `KVSerializerOptions.HasEscapeSequences`. ValveKeyValue defaults that
flag to `false`, but Steam files routinely contain C-style escapes (`\"`, `\\`),
so centralizing the option here keeps every caller correct. The binary KV1 blobs
inside `appinfo.vdf` are delegated to ValveKeyValue's binary serializer by
`SteamAppInfoReader`; the outer appinfo container (magic/version/string
table/app entries) is parsed by the narrow internal reader.

`LibraryFoldersVdf` (internal) is a minimal regex parser for Steam's
`libraryfolders.vdf` -- it extracts the library root `"path"` values in document
order (enough to drive multi-library discovery without routing that one file
through the ValveKeyValue dependency), unescaping `\\` → `\` and `\"` → `"`.
Visible to tests via `InternalsVisibleTo`.

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
each `Discover()` / `Rediscover()` call so a Settings / escape-hatch / hand-edit
write is visible immediately), so an `IConfigLoader` must be registered externally
before resolving `ISteamService`. In production that is [General](general.md)'s
`AddGeneral()` (which `TryAdd`s `ConfigLoader`); tests register a fake whose
`Save` mirrors the real loader's round-trip so a subsequent `Load` sees the saved
state.

`SteamRegistryReader` is Windows-only: no `Microsoft.Win32.Registry` package is
required (on `net10.0` the `Registry` type is in the reference assembly, gated
behind `[SupportedOSPlatform("windows")]`), and the reader is annotated
`[SupportedOSPlatform("windows")]` at the type level (for CA1416, with no
per-call runtime guard). It is registered **only on Windows** by `AddSteam()`;
on Linux it is intentionally absent so resolving `ISteamRegistryReader` fails
fast (the honest outcome for a Windows-only capability, rather than a silent
no-op).

## Dependencies

- **Curator libraries:** [config](config.md) (`DiscoveryConfig`, the
  discovery-mode + path-snapshot section of `CuratorConfig` that automatic mode
  rewrites and manual mode validates) + [general](general.md) (`IConfigLoader`,
  the live reader/writer `SteamService` reads `Discovery` from and writes the
  active-platform snapshot back to on each `Discover()` / `Rediscover()` call).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`, and `ValveKeyValue` 0.70.0.499
  (MIT, `net10.0`) for text + binary KV1 parsing of `config.vdf`,
  `compatibilitytool.vdf`, `appmanifest_*.acf`, and the binary KV1 blobs inside
  `appinfo.vdf`. The appinfo outer container (magic/version/string table/app
  entries) is parsed by the narrow internal `SteamAppInfoReader`; ValveKeyValue
  is not used for the outer container.

## Testing

`Modificus.Curator.Steam.Tests` covers Linux discovery (`LinuxDiscoveryTests`,
`FlatpakDiscoveryTests`), Windows discovery (`WindowsDiscoveryTests`), Proton
compatibility-tool selection (`ProtonSelectionTests`, `ProtonResolverTests` --
app-specific vs global mapping vs the appinfo recommended-runtime fallback
(identical regardless of Deck identity) and every blocking rule around it,
custom vs Valve-managed tool resolution, root-level vs subdirectory custom
manifests, system roots), the binary
`appinfo.vdf` reader (`SteamAppInfoReaderTests`, against compact synthetic v41
fixtures plus a realistic multi-entry fixture matching the live appinfo shape --
Darktide's `recommended_runtime` + the Steam Play manifest's `compat_tools` in
both entry orders), the Steam Deck OS-release detector (`SteamDeckDetectorTests`), the
`SteamTextVdf` escape-semantics helper
(`SteamTextVdfTests`, including a sanitized realistic `config.vdf` fixture with
an escaped JSON scalar), the `libraryfolders.vdf` parser
(`LibraryFoldersVdfTests`), game-running detection (`GameRunningTests`,
`ArgvMatchTests` -- the latter pinning the `MatchesArgv0` backslash normalization),
the `SteamPathNormalizer` pure helper (`SteamPathNormalizationTests`),
the `AddSteam` DI wiring (`SteamServiceCollectionExtensionsTests`: the `TryAdd`
overrides, the `ISteamDiscoverer` selection by `SteamDiscoveryOptions.Platform`,
and the platform `IProcessLookup` selection), and the discovery-mode policy
(`SteamServiceOverlayTests`: automatic mode runs the discoverer + persists the
full active-platform snapshot including nulls that clear stale values, skips the
save when the snapshot is unchanged, follows a changed Darktide library location,
and on Windows writes only Steam + Darktide while preserving leftover Linux
fields; manual mode validates stored paths by kind, returns null for invalid
fields without rewriting the stored input, skips compatdata/Proton on Windows,
and yields null `ProtonVersion`; `Rediscover` forces an automatic pass even in
manual mode, replaces active fields including nulls, and preserves the mode; the
live-read contract makes a mode change between calls visible on the next
`Discover()`). `WindowsDiscoveryTests` force `Platform = Windows` + a fake
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
