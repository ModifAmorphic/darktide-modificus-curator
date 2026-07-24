# Modificus Curator

The mod-manager application -- the user-facing layer on
top of Mod Relay. It owns profiles, mod staging, load order,
dependency resolution, mod-source integrations (Nexus Mods,
Steam), and the "Launch Darktide" button that invokes the Relay launcher.
Target architecture:
[`../docs/architecture/MODIFICUS-CURATOR.md`](../docs/architecture/MODIFICUS-CURATOR.md).

## Tech stack

- **.NET 10 (LTS)** -- target framework `net10.0`.
- **Avalonia 12** -- native cross-platform UI.
- **CommunityToolkit.Mvvm** -- MVVM (source generators).
- **Microsoft.Extensions.DependencyInjection** -- DI.
- **Microsoft.Extensions.Logging + Serilog** -- structured logging (console +
  file sinks), config-honored level/file.
- **Microsoft.Extensions.Configuration** -- JSON config → `CuratorConfig`.
- **xUnit** -- tests.

## Project layout

```
src/
  modificus-curator.sln            solution root
  Directory.Build.props           shared MSBuild properties (net10.0, nullable)
  config.example.json             sample global config (schema reference)
  ui/                             Modificus.Curator.UI       Avalonia executable + DI composition root
                                                            (shell + profiles, Preferences, mod-list UI,
                                                            Launch + Settings)
  general/                        Modificus.Curator.General  cross-cutting infra: logging, config loader,
                                                            app-state store, DI
  config/                         Modificus.Curator.Config   the CuratorConfig schema + defaults (POCO)
  profiles/                       Modificus.Curator.Profiles          profile data + lifecycle + container-based staging
  mods/                           Modificus.Curator.Mods              unified mod repository + version-policy + source models + import
  integrations/                   Modificus.Curator.Integrations      Nexus v1 client/auth + mod acquisition + update check
  steam/                          Modificus.Curator.Steam             Steam/Darktide/Proton discovery + IsGameRunning
  relay-client/                   Modificus.Curator.RelayClient       the launch façade
  launcher/                       Modificus.Curator.Launcher          stub (Steam non-steam-shortcut target placeholder)
  tests/
    Modificus.Curator.General.Tests/         xUnit tests for the general library
    Modificus.Curator.Profiles.Tests/        xUnit tests for the profiles library (incl. staging)
    Modificus.Curator.Mods.Tests/            xUnit tests for the mod repository + import
    Modificus.Curator.Integrations.Tests/    xUnit tests for the Nexus client + auth + acquisition + update check
    Modificus.Curator.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Modificus.Curator.RelayClient.Tests/     xUnit tests for the launch façade (dual-purpose: dotnet test / dotnet run smoke harness)
    Modificus.Curator.UI.Tests/              xUnit tests for the shell + manage-profiles + mod-list view models
```

Each library exposes an `Add<Library>()` extension method on
`IServiceCollection`; the UI composition root (`ui/CuratorComposition.cs`) calls
them all. See the architecture doc for each library's domain responsibility.

## Build

Requires the **.NET 10 SDK**. From the repo root:

```sh
dotnet build src/modificus-curator.sln --configuration Release
```

## Run

```sh
dotnet run --project src/ui --configuration Release
```

The window shows the top bar (app title, profile dropdown + "Manage profiles…"
gear, Launch Darktide) and the status strip (Darktide running indicator). The
profile dropdown switches the active profile (persisted across restarts via
`IAppStateStore`); "Manage profiles…" opens the create / rename / delete dialog.
The startup log lines (`Modificus Curator starting`, `Config loaded …`) go to the
console and to the configured log file.

## Test

```sh
dotnet test src/modificus-curator.sln --configuration Release
```

## CI and releases

The PR gate (`.github/workflows/curator-build.yml`) runs on `pull_request`
targeting `main` (and `workflow_dispatch`). There is intentionally no `push`
trigger; the release workflow handles push-to-main. It runs an Ubuntu-only
format job, then `dotnet build` and `dotnet test` on a Windows + Ubuntu
matrix that depends on the format job. A separate Ubuntu 22.04 packaging-smoke
job publishes, packs, extracts, and validates the Linux AppImage and its
Velopack feed, runs shell syntax checks on all four production Linux scripts
(`install.sh`, `install-standalone.sh`, `uninstall.sh`,
`uninstall-standalone.sh`), and runs the AppImage installer, AppImage
uninstaller, and standalone uninstaller test harnesses. For
same-repo PRs the format job
runs `dotnet format` and commits any changes as
`style: dotnet format [skip ci]`; for fork PRs and `workflow_dispatch` it
runs `dotnet format --verify-no-changes`. `paths-ignore` skips release-please's
bot-authored release PRs. No build artifact is uploaded; release assets are
produced by the release workflow instead.

Releases are cut by `release-please` (`.release-please-config.json` +
`.release-please-manifest.json` at the repo root; tag style `v0.1.0`, no
component prefix). When release-please creates a release, the release workflow
publishes each target as unsigned assets (Windows: a Velopack installer
and a portable ZIP; Linux: a framework-dependent tar.gz bundle plus a
self-contained Velopack AppImage and update feed), fetches
the latest non-draft Mod Relay prerelease, and uploads a GitHub Artifact
Attestation against each asset. Verify an asset's provenance with:

```
gh attestation verify <file> --repo ModifAmorphic/darktide-modificus-curator
```

The Windows releases are:

- **Velopack installer** (`modificus-curator-setup.exe`, pack id
  `ModifAmorphic.ModificusCurator`, installs to
  `%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\`, bootstraps the .NET 10
  runtime). This is the recommended installation method; it supports in-app
  self-update via Velopack.
- **Portable ZIP** (`curator-<tag>-windows-x64.zip`), a framework-dependent
  archive with two top-level folders: `app/` (the Curator UI and NXM handler)
  and `relay/` (the bundled Relay runtime). The .NET 10 Runtime must be
  installed separately. The portable build does not include Velopack and
  does not support in-app self-update; download a newer ZIP manually to update.

Linux has two permanent distributions:

- **AppImage** (`ModificusCurator-linux-x64.AppImage`): a
  self-contained .NET 10 payload with the UI, native-AOT NXM handler, and Relay
  under its app-local `relay/` directory. It is packed on channel `linux-x64`
  with `releases.linux-x64.json`, a full nupkg, and a delta when a predecessor
  exists. Velopack's generated AppImage is renamed after packing for the public
  download; the internal pack ID and nupkg names remain
  `ModifAmorphic.ModificusCurator`. It uses `VelopackAppUpdateService` and
  supports in-app self-update.
- **Standalone tarball** (`curator-<tag>-linux-x64.tar.gz`): the existing
  framework-dependent archive, with two top-level folders extracted into the
  Linux app-data root (`~/.local/share/Modificus Curator`):

- `app/` - the Curator UI + the `nxm://` handler (+ the launcher stub, pending
  later cleanup).
- `relay/` - the bundled Relay runtime, which seeds the default `RelayDir`.

See [`docs/reference/release-strategy.md`](../docs/reference/release-strategy.md)
for the full deployment model (Windows install vs data roots, app-local Relay,
the Velopack pack, auto-update).

A separate post-release workflow (triggered by `repository_dispatch` from the
release workflow, also runnable on manual `workflow_dispatch`) scans the
published bytes (Microsoft Defender PowerShell scan and VirusTotal). Defender
scans are performed using `Start-MpScan -ScanType CustomScan` and explicitly
classified as clean, detection, or tool_error. VirusTotal scanning requires
the `VIRUSTOTAL_API_KEY` repo secret to be configured. The file is submitted
via the pinned Marketplace action `crazy-max/ghaction-virustotal@936d8c5c00afe97d3d9a1af26d017cfdf26800a2`
with `request_rate: 4` to respect the VirusTotal public API quota. The workflow
opens a tracking issue with title "AV manual review for release <tag>" when
VirusTotal upload succeeds and returns analysis links. The issue contains the
Defender output and VirusTotal analysis links for manual review. No issue is
created if VirusTotal upload fails. The workflow fails when Defender scan tool
errors occur, when Defender detects threats, when VirusTotal upload fails, or
when the VirusTotal key is missing. It is still post-release and non-gating for
publication, but red means the scan signal is invalid or VirusTotal upload failed.
Releases created with `GITHUB_TOKEN` do not fire `release: published`, which is
why the AV/VT workflow runs on `repository_dispatch` instead.

The AppImage installer (`scripts/install.sh`, the recommended Linux installer)
installs the self-contained image at
`<root>/appimage/Modificus.Curator.AppImage`, plus user desktop integration.
The standalone tarball installer (`scripts/install-standalone.sh`) installs the
framework-dependent archive into `${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/`
(replacing only `app/` + `relay/`). Both are served from `raw/main`, default to
stable, accept `--prerelease`, and resolve independent URLs from
`scripts/release.env` (the AppImage reads `APPIMAGE_*` keys, the standalone
reads `RELEASE_*`/`PRE_RELEASE_*`) without querying the GitHub API. They
preserve shared user data and may coexist; the most recently run installer
repoints the common `~/.local/bin/modificus-curator` symlink.

Each distribution has its own per-user uninstaller with a default mode and a
`--purge-data` mode:

- `scripts/uninstall.sh` defaults to removing only the AppImage
  payload, its owned desktop/NXM integration, and app-specific Velopack update
  state. It preserves user data and the standalone distribution.
- `scripts/uninstall-standalone.sh` defaults to removing only the standalone
  `app/` + `relay/` payload, the command symlink when it points at the
  standalone UI, and the NXM desktop entry when its Exec points at the
  standalone handler. It preserves user data, the AppImage distribution, and
  Velopack state (the standalone build does not use Velopack).
- Either uninstaller's `--purge-data` mode removes the entire Linux Curator
  data root, including both distributions and all profiles, mods, config, logs,
  and app state. The purge semantics match, so one `--purge-data` invocation is
  a complete Linux removal regardless of which uninstaller runs it.

`scripts/tests/test-install.sh`, `scripts/tests/test-uninstall.sh`, and
`scripts/tests/test-uninstall-standalone.sh` exercise the install and both uninstall
modes in isolated HOME/XDG and Velopack-state paths.
See the root [`README.md`](../README.md) for the user-facing install steps.

## Storage model (unified repository)

Mods are stored **once, in a unified repository** keyed by one UUID container per
`(source-type, identity)`, holding opaque-ID version subfolders indexed by a
per-container `container.json` manifest. Profiles reference a mod by
`(containerId, policy)` and store no mod files of their own. **Symlinks** project
the resolved set into the staged mod root at launch (store once, symlink;
copying would duplicate repository files). On-disk layout:

```
<ModsFolder>/                  # the mod repository root (CuratorConfig)
  <containerUUID>/                   # one container per (source, identity)
    container.json                   # { id, source, name, versions: [{ folder, versionString, isLatest, importedAt }] }
    <versionFolder>/                 # opaque-ID version subfolder; the mod files for that version
<ProfilesBaseFolder>/<guid>/
  profile.json                       # metadata + mod list + launch settings (entries carry ContainerId + Policy; launch settings carry ordered env vars + game args)
  staged/                            # the staged mod root = the --mod-path (REGENERATED each launch)
    mods/                            #   the mod host folder Relay consumes
      <baseName>                     #   staging link → <versionFolder>/<baseName>/ (junction on Windows, symlink on Linux; Latest → isLatest; Pinned(versionId) → matching Folder); the base name, not the container display name
      mods.lst                       #   successfully-staged enabled mods, in order
```

`Profiles` owns the staging seam (`ProfileService.PrepareModRoot` clears +
rebuilds `staged/`, discovering each enabled mod's base folder name inside its
resolved version folder, then writes `mods.lst`); `Mods` owns the repository
(`IModRepository`) + the version-policy model + the source model + the
local-import service. Version resolution at stage time is by policy: Latest →
the container's `isLatest` version; Pinned(vId) → the version whose `Folder`
matches the pin's versionId (the profile references a version by id, not by
tag, so the repo stays the sole source of truth for version details). See
[`../docs/architecture/MODIFICUS-CURATOR.md`](../docs/architecture/MODIFICUS-CURATOR.md)
(Mod repository).

## Configuration

One global config file (JSON), loaded by
`general/ConfigLoader.cs` onto a fully-defaulted `CuratorConfig`. The default
location is `<app-data>/config.json`, where `<app-data>` is
`%LOCALAPPDATA%\ModifAmorphic\Modificus Curator` on Windows and
`~/.local/share/Modificus Curator` on Linux. Every field has a
platform-appropriate default, so the app runs with no file present; specify only
what you want to override. See [`config.example.json`](./config.example.json)
for the schema.

| Field                          | Default                                         |
| ------------------------------ | ----------------------------------------------- |
| `Logging:Level`                | `Information`                                   |
| `Logging:LogFile`              | `<app-data>/logs/curator-{DateTime}.log`        |
| `Logging:RetainedLogFileCount` | `5`                                             |
| `ProfilesBaseFolder`           | `<app-data>/profiles`                           |
| `ModsFolder`                   | `<app-data>/mods`                               |
| `RelayDir`                     | `<app-data>/relay`                              |

Per-profile settings live with the profile, not in the global config. This
includes the launch settings (environment variables + Darktide command-line
arguments, edited from a per-row action in Manage Profiles and applied at
launch).
