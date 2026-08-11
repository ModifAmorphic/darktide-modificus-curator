# General (`Modificus.Curator.General`): reference

> Cross-cutting infrastructure: structured logging, JSON config loading, runtime
> app-state persistence, and the DI registration that wires all three into the
> container.

The composition root (`src/ui/CuratorComposition.cs`) calls into this
library first, before any domain library, to load `CuratorConfig` and build the
logger the rest of the app shares.

## Public surface

### `LoggingBootstrap` (static)

Builds the structured-logging pipeline from `CuratorConfig.Logging`. Serilog
(console + file sinks) bridged into `Microsoft.Extensions.Logging`, filtered to
the configured level. Day-rolling log file: the file sink's
`RollingInterval.Day` inserts the date before the extension (the default
`curator-.log` stem yields `curator-<yyyyMMdd>.log`), one file per day, appended
across starts within the same day, rolled at local midnight, and pruned to
`RetainedLogFileCount` newest files. Serilog owns the day-naming, midnight
rolling, and pruning.

```csharp
public static class LoggingBootstrap
{
    public static ILoggerFactory CreateLoggerFactory(CuratorConfig config);
}
```

`CreateLoggerFactory(config)`:
- Reads `config.Logging.Level` (a Serilog level name: `Verbose` / `Debug` /
  `Information` / `Warning` / `Error` / `Fatal`); an unknown value falls back to
  `Information`.
- Creates the log-file parent directory (the file sink does not reliably create
  missing parents).
- Builds a Serilog logger (`.MinimumLevel.Is(level)` + `.Enrich.FromLogContext()`
  + console + the day-rolling file sink at `config.Logging.LogFile` with
  `RollingInterval.Day` and `retainedFileCountLimit: config.Logging.RetainedLogFileCount`),
  assigns it to the global `Log.Logger`, wraps it in a `LoggerFactory` via
  `AddSerilog(logger, dispose: true)`, and returns it. Disposing the factory
  disposes the Serilog logger (flushing the file sink).

Relay keeps its own separate log (`relay-<yyyyMMdd>.log`) in the same directory;
relay-client resolves and prunes that file at launch (see
[relay-client](relay-client.md)).

### `IConfigLoader` / `ConfigLoader`

Loads `CuratorConfig` from JSON with full defaults, and writes it back through
`Save`. Consumers inject `IConfigLoader` and re-read on each
operation (config is read live from disk, not cached at startup; #31), so runtime
writes via the Settings destination are visible immediately.
A missing or partial file yields a fully-usable config on load: every field has
a platform-appropriate default (see [config](config.md)).

```csharp
public interface IConfigLoader
{
    CuratorConfig Load();
    void Save(CuratorConfig config);   // atomic write-back
}

public sealed class ConfigLoader : IConfigLoader
{
    public ConfigLoader(string? path = null);
    public string Path { get; }
    public CuratorConfig Load();
    public void Save(CuratorConfig config);
    public static string DefaultConfigPath();
}
```

- `ConfigLoader(path)`: `null` resolves to `DefaultConfigPath()`.
- `Load()`: starts from `CuratorConfig.CreateDefault()`. If the config file's
  parent directory exists, binds the JSON file onto the defaults via
  `Microsoft.Extensions.Configuration` (`AddJsonFile(optional: true)`); if the
  directory is absent (a fresh first run), skips straight to the defaults rather
  than letting `SetBasePath` throw. Unset keys keep their defaults. Cheap to
  call per op (the file is tiny); the live-read model avoids a startup cache that
  would only create staleness.
- `Save(config)`: writes the whole `CuratorConfig` back to the
  JSON file via `System.Text.Json` (config is machine-managed; rewriting it
  wholesale is fine and simpler than per-section merges). The `ThemeMode` enum
  is serialized as a string (camelCase) so the persisted file is human-readable
  and stable across enum renumbering. The parent directory is created if missing
  (first-run safe). **Atomic publish:** serialize to a temp file in the same
  directory as the target, then `File.Move(overwrite: true)` renames it into
  place (same-directory guarantees same-volume, so the rename is atomic); a crash
  mid-write never leaves a half-written config, and concurrent `Load()` callers
  see either the prior file or the new one, never truncated JSON. Writes are
  best-effort: a persistence failure (unwritable dir, full disk) is swallowed
  rather than crashing the app mid-interaction.
- `DefaultConfigPath()`: `<app-data>/config.json`, where `<app-data>` is
  `AppPaths.AppDataDir` (`%LOCALAPPDATA%\ModifAmorphic\Modificus Curator` on
  Windows, `~/.local/share/Modificus Curator` on Linux).

### `IAppStateStore` / `AppStateStore`

Persists **runtime application state**: values that capture "where the app left
off" rather than user system settings. Kept deliberately narrow: the first-run
onboarding flag, the active profile id, the last update-check timestamp, the
manual-refresh throttle window, the persisted known-update snapshots, and the
last Nexus display-metadata backfill timestamp. A separate file (not
`CuratorConfig`) holds it so the settings schema stays pure (system settings
vs. runtime state).

```csharp
public interface IAppStateStore
{
    bool OnboardingCompleted { get; set; }                          // set persists immediately
    Guid? ActiveProfileId { get; set; }                             // set persists immediately
    DateTimeOffset? LastUpdateCheckUtc { get; set; }                // set persists immediately
    IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; } // set persists immediately
    IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; } // set persists immediately
    DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }      // set persists immediately
}

public sealed record KnownUpdateSnapshot(
    Guid ProfileId, Guid ContainerId, int ModId,
    string CurrentVersion, DateTimeOffset CheckedAt, DateTimeOffset? LatestUpdateAt);

public sealed class AppStateStore : IAppStateStore
{
    public AppStateStore(string? path = null);
    public string Path { get; }
    public static string DefaultStatePath();
}
```

- File: `<app-data>/app-state.json`
  (`{ "OnboardingCompleted": ..., "ActiveProfileId": ..., "LastUpdateCheckUtc": ...,
  "ManualRefreshTimestamps": ..., "KnownUpdates": { "<profile-guid>": [ { ...snapshot... }, ... ] } | null,
  "LastNexusMetadataBackfillUtc": "<iso-8601>" | null }`),
  derived from `AppPaths.AppDataDir` the same way `ConfigLoader` derives its
  config path.
- JSON is handled with `System.Text.Json` directly (read + write);
  `Microsoft.Extensions.Configuration` is binding-oriented and read-only, the
  wrong fit for a tiny writable state file.
- The full state model is cached in memory after the first read and written
  whole on every change, so assigning one property never clobbers the others.
- **First-run safe:** a missing or corrupt file never throws; `get` just
  returns the default (`false` for `OnboardingCompleted`, `null` for the rest).
  Writes are best-effort (runtime state is non-critical; a persistence failure
  is swallowed rather than crashing the app). An old file written before a field
  existed deserializes that field as its default, so a first run after upgrade
  sees no recorded value and the consumers seed cleanly.
- `OnboardingCompleted` is used by the UI-layer onboarding coordinator
  (`OnboardingService`) to decide whether to show the first-run Welcome modal:
  it reads the flag at startup and sets it to `true` once the user has chosen
  (Set up Nexus or Continue without Nexus), persisting before any further UI so
  navigating away from Nexus (or the navigation failing) can never
  cause Welcome to repeat.
- `ActiveProfileId` is used by `IProfileSession` (the active-profile authority)
  to restore the active profile on construction and persist it on changes.
- `LastUpdateCheckUtc` + `ManualRefreshTimestamps` are used by `UpdateCheckRunner`
  to seed and persist the last update-check timestamp (so the interval gate
  survives a close/reopen) and the manual throttle's sliding-window timestamps
  (so the manual free-refresh budget survives a close/reopen).
- `KnownUpdates` is used by the Integrations-layer `IUpdateStateStore` to persist
  profile-scoped known-update snapshots (so a restart inside the interval gate
  shows prior update flags before any API call). The shell and the Profiles
  destination read the active id through the session; they do not touch this
  store.
- `LastNexusMetadataBackfillUtc` is used by the Integrations-layer
  `INexusModMetadataService` (see [integrations](integrations.md#metadata-backfill-service))
  to gate the missing-metadata backfill pass to at most one real pass per 24-hour
  window. It is a **repository-wide** gate (not profile-scoped like
  `KnownUpdates`, and unrelated to the update-check interval state in
  `LastUpdateCheckUtc`): the backfill covers every Nexus container in the
  repository missing display metadata, regardless of which profile is active.
  Defaults to `null`; assigned the current UTC time immediately after a pass that
  attempted at least one API request (a no-auth, already-gated, or no-candidate
  no-op does not stamp). Backward compatible on disk: an old `app-state.json`
  written before the field existed deserializes it to `null` (System.Text.Json
  default for an absent nullable member), so the first run after upgrade proceeds
  normally. See
  [rate-limiting strategy: metadata-backfill gate](rate-limiting-strategy.md#metadata-backfill-gate).

`KnownUpdateSnapshot` is a plain serializable DTO (no domain behavior) so the
General library can persist it without depending on the Integrations
update-check domain. The Integrations `IUpdateStateStore` owns the rules (when to
record, when to clear, how to filter on hydration); this record is the persisted
shape. Each field exists to identify the flagged mod and invalidate stale
knowledge after a local version change without re-querying Nexus. Display names
are not persisted (they continue to come from repository persistence).

## DI registration

```csharp
public static IServiceCollection AddGeneral(
    this IServiceCollection services,
    ILoggerFactory loggerFactory);
```

`AddGeneral(loggerFactory)` is called by the composition root **after** config is
loaded and the logger built (both are constructed outside DI because DI itself
needs them). It registers:

- `AddSingleton(loggerFactory)`: the Serilog-backed `ILoggerFactory`.
- `AddLogging()`: wires `ILogger<T>` resolution through the factory.
- `TryAddSingleton<IConfigLoader, ConfigLoader>()`: the live-read config loader.
  `TryAdd` (not `Add`) so the composition root pre-registers the same loader
  instance it used for its one-off startup snapshot (one shared live-read
  singleton) before calling `AddGeneral`; the typed default is the fallback for
  hosts that do not pre-register (tests, smoke harnesses).
- `TryAddSingleton<IAppStateStore, AppStateStore>()`: the runtime app-state
  store. `TryAdd` (not `Add`) so a test or host may pre-register an override
  (e.g. an in-memory or temp-path store) before `AddGeneral` runs.

`CuratorConfig` is intentionally **not** registered as a singleton here: config is
read live from disk via `IConfigLoader` on each access (the startup snapshot used
to build the logger is a one-off; logging config does not change at runtime in
v1). `loggerFactory` is a constructed object passed in; `IConfigLoader` +
`IAppStateStore` are the seams (overridable via pre-registration).

## Dependencies

- **Curator libraries:** `config` (project reference: `CuratorConfig`).
- **NuGet:** `Microsoft.Extensions.Configuration` (+ `.Binder`, `.Json`),
  `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging` (+ `.Abstractions`), Serilog (`Serilog`,
  `Serilog.Extensions.Logging`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`).

## Testing

`Modificus.Curator.General.Tests` covers `ConfigLoader` (first-run-safe + JSON
override binding, plus `Preferences` round-trip + `Save` coverage:
round-trip, parent-dir creation, sibling-section preservation, enum-as-string
serialization), `AppStateStore` (round-trip + first-run + corrupt-file safety +
the app-data default path, the profile-scoped `KnownUpdates` round-trip, the
old-file-without-field compatibility for every field including
`LastNexusMetadataBackfillUtc`, and the whole-model rewrite preserving sibling
fields on each assignment), `LoggingBootstrap` (level parsing, Serilog
day-rolling file creation and the append-within-a-day behavior at
`RetainedLogFileCount`), and the `AddGeneral` DI wiring (including the `TryAdd` `IConfigLoader` + `IAppStateStore`
overrides, so the composition root + tests may pre-register their own instances).

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md):
  design; the [Composition & startup](../architecture/MODIFICUS-CURATOR.md#composition--startup)
  section.
- [config](config.md): the `CuratorConfig` schema this library loads.
