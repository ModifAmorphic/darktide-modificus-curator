# Config (`Modificus.Curator.Config`): reference

> The global configuration schema: a POCO model with platform-appropriate
> defaults, bound from JSON by the [General](general.md) library.

`CuratorConfig` holds system-level settings shared across all profiles. Per-profile
settings live with the profile, not here. Every field carries a default, so an
absent or partially-populated config file always yields a usable object.

A sample file ships at `src/config.example.json`; copy it to
`<app-data>/config.json` (on Windows
`%LOCALAPPDATA%\ModifAmorphic\Modificus Curator\config.json`, on Linux
`~/.local/share/Modificus Curator/config.json`) and edit only what you want
to override.

## Public surface

### `CuratorConfig`

The root config object: the aggregate the loader binds and `ConfigLoader.Save`
writes back wholesale on a Preferences change.

```csharp
public sealed class CuratorConfig
{
    public LoggingConfig Logging { get; set; } = new();
    public string ProfilesBaseFolder { get; set; }      // default profiles root
    public string ModsFolder { get; set; }         // default mods root
    public string RelayDir { get; set; }      // default relay root
    public DiscoveryConfig Discovery { get; set; } = new();
    public IntegrationsConfig Integrations { get; set; } = new();
    public PreferencesConfig Preferences { get; set; } = new();

    public static CuratorConfig CreateDefault();
}
```

| Field | Default | Meaning |
| --- | --- | --- |
| `Logging` | see `LoggingConfig` | Log level + file (consumed by `LoggingBootstrap`). |
| `ProfilesBaseFolder` | `<app-data>/profiles` | Where profiles and per-profile settings are stored (mods live in `ModsFolder`; see [mods](mods.md)). |
| `ModsFolder` | `<app-data>/mods` | The global mod store (see [mods](mods.md)). |
| `RelayDir` | `<app-data>/relay` | Where `mod_relay.exe`, `relay_shell.dll`, and `mod_loader/` live (consumed by [relay-client](relay-client.md)). |
| `Discovery` | see `DiscoveryConfig` | User-supplied discovery overrides (Steam / Darktide / compatdata / Proton paths). Validated on disk + healed from the discoverer + persisted by `SteamService.Discover()`. |
| `Integrations` | see `IntegrationsConfig` | External-service (mod-source) integration settings. |
| `Preferences` | see `PreferencesConfig` | User-facing global preferences (theme, font scale, language). |

`<app-data>` is `AppPaths.AppDataDir`: `Environment.SpecialFolder.LocalApplicationData`
plus an app-data segment that is `ModifAmorphic\Modificus Curator` on Windows (an
org/app hierarchy, kept distinct from the Velopack install root at
`%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\`) and `Modificus Curator` on Linux.

### `LoggingConfig`

```csharp
public sealed class LoggingConfig
{
    public const string DateTimeToken = "{DateTime}";
    public string Level { get; set; } = "Information";   // Serilog level name
    public string LogFile { get; set; }                   // default <app-data>/logs/curator-{DateTime}.log
    public int RetainedLogFileCount { get; set; } = 5;
}
```

- `Level`: a Serilog level name (`Verbose`/`Debug`/`Information`/`Warning`/
  `Error`/`Fatal`); an unknown value falls back to `Information` at bootstrap.
- `LogFile`: the structured log file. May contain `DateTimeToken` (`{DateTime}`);
  when it does, the bootstrap resolves the token to a fixed-width local timestamp
  (`yyyyMMddHHmmss`) once at startup and pins that path for the process lifetime
  (a new file per start). The resolved path is shared with Mod Relay: Curator
  passes it to the launcher as `--log-file`, so the two write the same per-process
  file. When the token is absent, the configured file is reused and truncated on
  each start (the legacy behavior).
- `RetainedLogFileCount`: how many datetime-named log files to keep (including
  the current process's file). Default 5. Only applies when `LogFile` contains
  `DateTimeToken`; the log directory is pruned to the newest N after each start.
  A value below 1 disables pruning (keeps all).

### `IntegrationsConfig` / `NexusConfig`

```csharp
public sealed class IntegrationsConfig
{
    public NexusConfig Nexus { get; set; } = new();
}

public sealed class NexusConfig
{
    public const int MinAutoUpdateCheckIntervalMinutes = 5;
    public const int MaxAutoUpdateCheckIntervalMinutes = 1440;

    public string BaseUrl { get; set; } = "https://api.nexusmods.com";
    public string OAuthBaseUrl { get; set; } = "https://users.nexusmods.com";
    public NexusAuthMethod AuthMethod { get; set; } = NexusAuthMethod.None;
    public string? ApiKey { get; set; }                          // used when AuthMethod == ApiKey
    public NexusOAuthTokens? OAuth { get; set; }                 // used when AuthMethod == OAuth
    public bool AutoUpdateCheckEnabled { get; set; } = true;
    public int AutoUpdateCheckIntervalMinutes { get; set; } = 10;
    public bool AutomaticUpdatesEnabled { get; set; }            // opt-in Premium auto-install, default false
}

public enum NexusAuthMethod { None, OAuth, ApiKey }

public sealed record NexusOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    string Scope,
    DateTimeOffset ExpiresAt);
```

Nexus fields:

- `BaseUrl`: the Nexus REST API root, without a trailing slash. Defaults to the
  public endpoint; override only for testing.
- `OAuthBaseUrl`: the Nexus OAuth issuer root, without a trailing slash. The
  OIDC discovery, authorize, token, and userinfo endpoints hang off this root.
  Defaults to the public endpoint; override only for testing.
- `AuthMethod`: the user's explicit auth-method choice, read live by the auth
  message factory selector on every request. `None` is the default
  (unauthenticated; API calls fail with a clear error, callers gate on it).
  Set by the Integrations dialog: OAuth login sets `OAuth`, API-key validate
  sets `ApiKey`, sign-out resets to `None`. There is **no fallback**: if the
  selected method's credentials are missing or expired, the client surfaces an
  auth error for that method rather than silently using the other. Switching
  methods clears the other method's credentials (no stale leftovers).
- `ApiKey`: the Nexus API key (sent as the `apikey` header). Set when
  `AuthMethod == ApiKey`; cleared on sign-out or when switching to OAuth.
  `null`/whitespace is treated as "not configured".
- `OAuth`: the persisted OAuth tokens. Set when `AuthMethod == OAuth`; cleared
  on sign-out or when switching to API key. `null` is treated as "not
  authenticated". See `NexusOAuthTokens` below.
- `AutoUpdateCheckEnabled`: whether the periodic background update check runs
  while a profile is active. `true` by default. Gates ONLY the periodic timer;
  the profile-load check (startup + active-profile switch) and the manual "check
  now" button always run regardless. Read live on each timer tick, so a dialog
  change takes effect without a restart.
- `AutoUpdateCheckIntervalMinutes`: the periodic update-check interval, in
  minutes. `10` by default. Honored to a 1-minute granularity; values below 5
  are clamped.
- `MinAutoUpdateCheckIntervalMinutes` / `MaxAutoUpdateCheckIntervalMinutes`:
  named policy bounds (5 / 1440 minutes) for `AutoUpdateCheckIntervalMinutes`,
  applied on save (the Integrations dialog) + at tick time (the runner). The
  5-minute floor is a Nexus API acceptable-use compliance measure (at 5 minutes
  the periodic check tops out at 288 calls/day).
- `AutomaticUpdatesEnabled`: whether Premium accounts have flagged mod updates
  installed automatically after an update check runs. `false` by default
  (opt-in). Independent of `AutoUpdateCheckEnabled`: turning this on never
  requires periodic checking to be on (startup + switch + manual checks still
  drive it), and changing the periodic-check toggle never clears a configured
  `true` here. Runtime execution additionally requires a fresh verified Premium
  account, so a configured `true` is preserved (stays checked + visible but
  disabled in the Integrations dialog) if Premium later becomes unavailable,
  while no automatic install runs. Surfaced as a checkbox in the Integrations
  "Update checks" section, enabled only for a verified Premium account.

The `NexusAuthMethod` enum carries the three explicit choices. The OAuth client
id is a build-time constant (in `Modificus.Curator.Integrations.NexusOAuthConstants`),
not config and not an env var.

`NexusOAuthTokens` is an immutable record holding the OAuth session state:

- `AccessToken`: the OAuth bearer access token sent as
  `Authorization: Bearer <token>` on every API request.
- `RefreshToken`: the refresh token used to obtain a new access token when the
  current one expires (401-reactive refresh). May be `null` when the server did
  not issue one (rare; effectively single-session).
- `Scope`: the granted scope string (space-delimited). Persisted for
  diagnostics; not consulted by the client.
- `ExpiresAt`: when the access token expires (UTC). The factory does **not**
  proactively refresh before this; it refreshes reactively on the first 401
  after expiry.

Set on a successful login; replaced wholesale on a token refresh; cleared on
sign-out.

### `PreferencesConfig` / `ThemeMode`

User-facing global preferences, exposed through the Preferences dialog. The
dialog applies each change immediately (theme + font scale + language take
effect live) and persists through `ConfigLoader.Save`; there is no commit step.

```csharp
public sealed class PreferencesConfig
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public double FontScale { get; set; } = 1.0;
    public string Language { get; set; } = "en";
}

public enum ThemeMode
{
    System = 0,   // follow the OS theme (Avalonia ThemeVariant.Default)
    Dark = 1,     // Avalonia ThemeVariant.Dark
    Light = 2,    // Avalonia ThemeVariant.Light
}
```

- `Theme`: the UI theme variant. `System` follows the OS (Avalonia's
  `ThemeVariant.Default`); applied via `Application.Current.RequestedThemeVariant`.
- `FontScale`: a continuous UI font-scale multiplier (1.0 = no scaling). The
  Preferences dialog exposes this as a percent slider (80 to 150 in 5% steps);
  the persisted value is the raw double (e.g. 1.25 for 125%). Applied as an
  application-level `AppFontSize` resource that a Window style binds to
  (`DynamicResource`), cascading to all controls via inheritance.
- `Language`: the display language as a culture name (e.g. `en`, `fr`). Empty or
  `en` resolves to the neutral English resources. English ships; the selector +
  culture switching are in place; real translations are content added later via
  translated `Strings.<culture>.resx` files (no code change). Switching language
  updates the live UI through the UI-layer `LocalizationService` (dynamic, no
  restart).

### `DiscoveryConfig`

User-supplied overrides for Steam/Darktide/Proton discovery. The Settings
window and the discovery escape-hatch dialog write these;
`SteamService.Discover()` reads them live (one `Load()` per call, via
[IConfigLoader](general.md)), validates each platform-relevant field's path on
disk, heals the missing/non-existent ones from the platform discoverer, and
persists **only the healed fields** back through `Save` (preserving valid
fields + any concurrent hand-edit). An absent section yields a fully-defaulted
(all-null) instance, which causes every field to be healed on the first
`Discover()` call (typically at startup).

```csharp
public sealed class DiscoveryConfig
{
    public string? UserSteamInstallPath { get; set; }       // Steam client dir; null/non-existent = heal
    public string? UserDarktideGameBinaryPath { get; set; } // native Darktide.exe path; null/non-existent = heal
    public string? UserCompatdataPath { get; set; }         // Wine prefix (Linux only); null/non-existent = heal
    public string? UserProtonBinaryPath { get; set; }       // proton script path (Linux only); null/non-existent = heal
}
```

- Every field is nullable and defaults to `null`, meaning "no override yet."
  On the first `Discover()` call, missing fields are healed from the platform
  discoverer and persisted here so the next call is a fast validation (no
  discoverer run).
- **Validate + heal + persist:** a supplied value is checked on disk (directory
  for Steam install + compatdata; file for the Darktide binary + Proton script).
  A value that exists is kept as-is (preserved across calls). A null/whitespace
  value, or one whose path no longer exists, is healed from the platform
  discoverer when possible, and the healed value is persisted back here (only
  that field; the others are untouched). A field the discoverer also cannot
  resolve stays null and is flagged via `DiscoveryResult.Status`.
- **Platform-gating:** the compatdata + Proton fields are Linux-only; on
  Windows they are neither validated nor healed, so they stay null in the
  result. A leftover Linux value (e.g. from a prior Linux run) is preserved
  untouched rather than cleared.
- Field mapping to `DiscoveryResult` (the final path is the override when it
  exists on disk, otherwise the discoverer's value): `UserSteamInstallPath` →
  `DiscoveryResult.SteamInstallPath`; `UserDarktideGameBinaryPath` →
  `DarktideGameBinaryPath`; `UserCompatdataPath` → `CompatdataPath`;
  `UserProtonBinaryPath` → `ProtonBinaryPath`. See [steam](steam.md) for the
  validate + heal + persist pipeline + the shared completeness rule.
- `UserCompatdataPath` / `UserProtonBinaryPath` are Linux-only (Windows is
  native; they are not validated or healed there, and stay null in the result).

### `AppPaths`

Resolves the default locations once, from `LocalApplicationData`. The app-data
segment is `ModifAmorphic\Modificus Curator` on Windows (an org/app hierarchy,
kept distinct from the Velopack install root at
`%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\`, which Velopack owns and
replaces on update) and `Modificus Curator` on Linux. Shared by `CuratorConfig`
and `LoggingConfig` so every field has a default and the JSON binder only
overwrites what the file sets.

## DI registration

None. This is a POCO schema library with no service registrations. Other
libraries register a loaded `CuratorConfig` singleton via `AddGeneral()` (see
[General](general.md)) and resolve `CuratorConfig` from the container as needed.

## Dependencies

- **Curator libraries:** none.
- **NuGet:** none (pure POCO; targets `net10.0`).

## Testing

Covered transitively: there is no standalone test project. The schema's
defaults and JSON binding are exercised by `Modificus.Curator.General.Tests`
(`ConfigLoader` first-run-safe + override tests, plus `Preferences`
round-trip + `Save` coverage) and by every other library's test
fixtures, which build a `CuratorConfig.CreateDefault()` pointing at a temp dir.

```sh
dotnet test src/modificus-curator.sln -c Release
```

## See also

- [Modificus Curator architecture](../architecture/MODIFICUS-CURATOR.md): the
  [Configuration](../architecture/MODIFICUS-CURATOR.md#configuration) section.
- [general](general.md): the loader that binds this schema (and writes it back).
- `src/config.example.json`: the sample file.
