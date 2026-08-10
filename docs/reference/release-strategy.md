# Modificus Curator release strategy

Reference for how Modificus Curator releases are produced, attested, scanned,
and installed. Covers the release workflow, the post-release AV/VT scan, the
PR gate, the two Linux installers, and both Linux uninstallers.

## Versioning

Releases are cut by
[release-please](https://github.com/googleapis/release-please)
(`googleapis/release-please-action@v5`), configured as a root, single-component
product:

- `.release-please-config.json` sets `release-type: simple`,
  `include-component-in-tag: false`, and `prerelease: true`. Tags follow the
  `v0.1.0` style (no component prefix), and GitHub releases are marked as
  prereleases (no suffix-style `v0.1.0-rc.1` tags).
- `.release-please-manifest.json` is the source-of-truth version. The first
  release (v0.1.0) has already shipped, so the manifest now reflects the
  current release version. Steady-state is derived from conventional commits.
- No Curator version is paired to a Relay version. Any Curator version is
  expected to work with the latest Relay.

The version is injected at publish time via `-p:Version` and
`-p:InformationalVersion` on `dotnet publish ui` (both set from the
release-please version). `InformationalVersion` is what an About box shows.
`AssemblyVersion` and `FileVersion` are not passed explicitly.

## Release workflow

`.github/workflows/release.yml` is triggered on `push` to `main` and via
`workflow_dispatch`. It chains release-please, the asset build, attestation,
and the AV/VT dispatch in a single workflow. In-workflow chaining is used
because events created with `GITHUB_TOKEN` do not trigger other workflows, so
a `release: published` trigger would never fire.

Repository Actions settings must allow the workflow token to write and to
create pull requests, because release-please creates and updates release PRs
with `GITHUB_TOKEN`. In GitHub, this is **Actions > General > Workflow
permissions > Read and write permissions** plus **Allow GitHub Actions to
create and approve pull requests**. The workflow still declares least-privilege
job permissions, so this setting only makes those declared permissions
available to the token.

Jobs:

1. **release-please** runs on `ubuntu-latest` against
   `.release-please-config.json` + `.release-please-manifest.json`. It cuts
   release PRs and publishes releases.
2. **build-windows** and **build-linux** run in parallel, each gated on
   `releases_created == 'true'` and checked out at the release commit. The
   Windows leg runs on `windows-latest` (native AOT for the `nxm://` handler
   requires a Windows runner); the Linux leg runs on `ubuntu-22.04` for a
   conservative native-AOT/glibc build environment.

   **Common to both legs:**
   - Sets up .NET 10.
   - Publishes the Curator UI framework-dependent into `stage/app/` with
     `-p:Version` + `-p:InformationalVersion`, targeting `win-x64` or `linux-x64`
     RIDs with `--self-contained false` to filter native libraries to one platform.
      The Windows installer publish and the separate Linux AppImage publish set
      `-p:CuratorUseVelopack=true`, which
     adds the Velopack package reference and the `CURATOR_VELOPACK` compilation
     symbol that wires the `VelopackApp.Build().Run()` lifecycle hook in
     `Program.cs`.
   - Publishes the NxmHandler native-AOT into `stage/app/` (`win-x64` on
     Windows, `linux-x64` on Linux).

    **build-windows** then produces both a Velopack installer and a portable
    ZIP:
    - Extracts the latest non-draft Relay prerelease from
      `ModifAmorphic/darktide-mod-relay` into `stage/relay/` (for the
      portable ZIP) and `stage/app/relay/` (for the Velopack pack) so Relay
      ships in both artifacts. (Fetched via
      `gh release list --exclude-drafts --order desc` with explicit
      `isPrerelease` filtering +
      `gh release download --pattern '*-windows-x64.zip'`. The Relay tag is
      resolved per workflow run; there is no Relay version pinning and no
      Relay provenance sidecar.)
    - Publishes the Curator UI framework-dependent with
      `-p:CuratorUseVelopack=true` into `stage/app/` (for Velopack) and
      publishes a second copy without the property into `stage-portable/app/`
      (for the portable ZIP). Both use the same release Version and
      InformationalVersion.
    - Publishes the NxmHandler native-AOT into `stage/app/` and
      `stage-portable/app/` (both `win-x64`).
    - **Velopack installer:**
      - Runs `vpk pack` (Velopack 1.2.0) with `--packId ModifAmorphic.ModificusCurator`,
        the release version, `--packDir stage/app`, `--mainExe Modificus.Curator.exe`,
        `--packTitle "Modificus Curator"`, the app icon, and
        `--framework net10.0-x64-runtime` (the installer bootstraps the .NET 10
        runtime if it is missing). Output goes to `stage/releases/`: a `Setup.exe`,
        a `*-full.nupkg`, and a `releases.win.json` (plus a `*-delta.nupkg` on
        non-first releases).
      - Renames `Setup.exe` to `modificus-curator-setup.exe`.
      - Uploads `modificus-curator-setup.exe`, the `*-full.nupkg`, and
        `releases.win.json` to the release with `gh release upload --clobber`
        (plus the delta nupkg when present).
      - Attests `modificus-curator-setup.exe` and the `*-full.nupkg` with
        `actions/attest@v4`.
    - **Portable ZIP:**
      - Stages the portable build: copies `stage/relay/` to `stage-portable/relay/`.
      - Creates a ZIP archive from `stage-portable/` with top-level `app/` and
        `relay/` directories using PowerShell `Compress-Archive`:
        `curator-<tag>-windows-x64.zip`.
      - Uploads the ZIP to the release with `gh release upload --clobber`.
      - Attests the ZIP with `actions/attest@v4`.

   **build-linux** produces both permanent Linux distributions:
   - **Standalone tarball:**
     - Fetches the same Relay Windows zip (Linux runs Relay under Proton, so
       there is no Relay Linux asset) and extracts it into `stage/relay/`.
     - Builds the release archive from `stage/` so it has a top-level `app/` +
       `relay/` layout: `curator-<tag>-linux-x64.tar.gz` (tar).
     - Uploads the archive to the release with `gh release upload --clobber`.
     - Attests the uploaded asset with `actions/attest@v4`.
   - **Self-contained AppImage:**
     - Publishes a second `linux-x64` UI payload with `--self-contained true`
       and `-p:CuratorUseVelopack=true`, publishes the native-AOT NXM handler
       into the same root, and stages Relay under its app-local `relay/`.
     - Installs job-local `vpk` 1.2.0 and packs with channel/runtime
       `linux-x64`, producing
       `ModificusCurator-linux-x64.AppImage` (renamed from Velopack's generated
       filename after packing),
       `releases.linux-x64.json`, and the current full nupkg.
     - Searches prior stable and prerelease releases for the newest release
       carrying the exact `linux-x64` feed, then seeds that feed and its one
       full nupkg before packing so Velopack can generate a delta. The first
       AppImage release proceeds without a predecessor.
     - Uploads only the current AppImage, feed, full nupkg, and optional delta.
       It attests the AppImage and current nupkgs.
3. **dispatch-av-vt**, gated on both build legs succeeding, fires a
   `repository_dispatch` event (`event_type: curator-release-assets-published`,
   carrying the tag + asset names) to trigger the post-release AV/VT workflow.
4. **update-manifest**, gated on `releases_created == 'true'` and `build-linux`
   success, rewrites `scripts/release.env` (the install manifest both Linux
   installers consume) to point at the new tarball and AppImage. It resolves
   the tarball independently by content type and the AppImage by its exact
   asset name. A stable release updates `RELEASE_URL` plus
   `APPIMAGE_RELEASE_URL`; a prerelease updates `PRE_RELEASE_URL` plus
   `APPIMAGE_PRE_RELEASE_URL`. It commits the change as
   `chore(release): update install manifest [skip ci]`. The `[skip ci]` in the
   commit message suppresses re-triggering this workflow, and the `chore` type
   keeps release-please from picking it up. Only the two matching channel lines
   are rewritten; the opposite channel and the comment header are left untouched.

### Deployment model

Windows and Linux each offer an installed/self-updating distribution and a
portable or standalone distribution.

**Windows** ships as a [Velopack](https://github.com/velopack/velopack) installer
plus auto-update payload, and as a portable ZIP. Both artifacts are built from the
same source commit and version.

- **Velopack installer** (`modificus-curator-setup.exe`): the recommended
  installation method. It is a one-click installer (no wizard), installs to
  `%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\` (the Velopack pack id is
  `ModifAmorphic.ModificusCurator`), creates Start Menu and desktop shortcuts,
  and registers in Apps & Features for uninstall. It bootstraps the .NET 10
  runtime if missing (`--framework net10.0-x64-runtime` baked into the pack).
  The Velopack lifecycle hook (`VelopackApp.Build().Run()` in `Program.cs`,
  compiled in under `CURATOR_VELOPACK`) fires install/update/uninstall hooks
  and applies any pending update on startup. Curator checks for updates on
  startup and can update itself in place via Velopack. Relay ships app-local
  inside the payload at `current\relay\`.

- **Portable ZIP** (`curator-<tag>-windows-x64.zip`): a framework-dependent
  archive for manual installation. It contains two top-level folders: `app/`
  (the Curator UI and NXM handler) and `relay/` (the bundled Relay runtime).
  Extract the ZIP anywhere and run `app/Modificus.Curator.exe`. The .NET 10
  Runtime must be installed separately from <https://dotnet.microsoft.com/download/dotnet/10.0>.
  The portable build is compiled without `-p:CuratorUseVelopack`, so it uses
  `NoopAppUpdateService` and does not support in-app self-update; download a
  newer ZIP manually to update. `nxm://` registration is handled at runtime
  via the Nexus destination, not automatically on first launch.

The Windows user-data root (profiles, mods, config, logs) is deliberately
separate from the install root, at `%LOCALAPPDATA%\ModifAmorphic\Modificus Curator\`
(see `config/AppPaths.cs`). The Velopack install root
(`...\ModifAmorphic.ModificusCurator\`) is owned by Velopack and replaced in
place on update, so user data must not live there. The portable ZIP has no
install root; user data is always at the data root.

**Linux AppImage** (`ModificusCurator-linux-x64.AppImage`) is the
recommended installed distribution. It is self-contained, so no host .NET
runtime is required. `scripts/install.sh` (the recommended Linux installer)
installs it per-user at
`~/.local/share/Modificus Curator/appimage/Modificus.Curator.AppImage`, adds a
desktop entry, icon, and command symlink, and uses no root privileges. The
AppImage is a Velopack package on channel `linux-x64`, so the existing startup
check and Download and Restart flow replace the stable installed file and
relaunch it.

The public AppImage asset is renamed after `vpk pack` to keep the download name
short. The Velopack pack ID remains `ModifAmorphic.ModificusCurator`; feed and
nupkg identities are unchanged. Linux updates extract the internal AppImage
from the nupkg and replace the current `$APPIMAGE` path, so the public initial
download filename is not part of the update contract.

**Linux standalone tarball** remains a permanent supported alternative. Users
install the .NET 10 Runtime themselves; the base `Microsoft.NETCore.App` runtime
is sufficient. The tar.gz has a top-level `app/` + `relay/` layout extracted
into `~/.local/share/Modificus Curator/`. It omits Velopack and updates by
re-running `scripts/install-standalone.sh` or installing the archive manually.

Two executables ship together in `app/`: `Modificus.Curator[.exe]` (the
Avalonia UI, `WinExe`) and `Modificus.Curator.NxmHandler[.exe]` (the
OS-registered `nxm://` handler, native AOT, `TrimMode=full`). The NxmHandler
resolves its sibling Curator exe via `AppContext.BaseDirectory`
(`NxmHandlerRelay.ResolveCuratorMainExe`), so both land in one directory in
every bundle.

`CuratorConfig.RelayDir` defaults to `<app-data>/relay/` (the Windows data root
or `~/.local/share/Modificus Curator/` on Linux; see `config/AppPaths.cs`).
`RelayLaunchService.ResolveLauncherPath` looks there first, then on both Windows
and Linux falls back to app-local `relay/` inside a Velopack payload. Windows
alone has a final portable sibling fallback. There is no first-run Relay
provisioning logic; a missing launcher maps to
`LaunchStatus.Error`. Manual `RelayDir` overrides via JSON config are the
user's responsibility.

`nxm://` registration is done at runtime by Curator, not by the Windows
installer or the Linux install script. `WindowsNxmHandlerRegistrar` writes
`HKCU\Software\Classes\nxm` (per-user, no elevation); `LinuxNxmHandlerRegistrar`
writes `~/.local/share/applications/modificus-curator-nxm-handler.desktop` + a
best-effort `xdg-mime default`. The standalone Linux build records the sibling
handler directly. In an AppImage run, Curator copies the handler to a durable
per-user integration directory and creates a sibling symlink to `$APPIMAGE`, so
the desktop entry never records a temporary mount path. Startup maintenance
refreshes those files only while Curator still owns the active registration.
On Windows the Velopack `current\` directory is replaced in place on update, so
the registered path stays stable.

## Supply-chain integrity (artifact attestations)

Each release asset is attested with `actions/attest@v4` in the release
workflow immediately after `gh release upload`. The attestation records which
repo, workflow, commit, and runner produced the asset. Attestation lives in
the release workflow (the workflow that built the artifact) because the
provenance value comes from "this artifact was produced by this workflow run
on this commit"; moving attestation to a separate workflow that just
downloads the artifact would attest to nothing useful.

Windows attests three assets: `modificus-curator-setup.exe`, the
`*-full.nupkg`, and `curator-<tag>-windows-x64.zip`. Linux attests the
standalone tarball, AppImage, full nupkg, and optional delta nupkg. Anyone can
verify an asset:

```
gh attestation verify <file> --repo ModifAmorphic/darktide-modificus-curator
```

What attestation does not do: bypass SmartScreen or Defender, help
non-technical end users (who do not run `gh attestation verify`), or replace
code signing for runtime trust.

## Post-release AV/VT scan

`.github/workflows/curator-post-release-av.yml` is triggered by the
`repository_dispatch` event `curator-release-assets-published` from the
release workflow, and also runs on manual `workflow_dispatch` (which takes
`tag_name` + `windows_asset` inputs). It runs as a single job on
`windows-latest`.

The job downloads the published Windows installer asset
(`modificus-curator-setup.exe`, the Velopack installer) and scans those exact
bytes (not a fresh build), so it scans what users receive:

- **Microsoft Defender**: runs `Start-MpScan -ScanType CustomScan -ScanPath
  <full asset path>` against the downloaded installer. The scan is performed
  using PowerShell cmdlets designed for programmatic use, which work on the
  Windows Server runner. The result is explicitly classified as `clean`,
  `detection`, or `tool_error`. A `tool_error` indicates the scan command
  failed or Defender is unavailable. The runner is Windows Server with Defender
  sometimes cloud-delivery-off, so this is a coarse signal, not a guarantee of
  what a consumer Windows 11 box sees.
- **VirusTotal**: required, gated on the `VIRUSTOTAL_API_KEY` repo secret.
  The workflow fails if the secret is not configured. Submits the installer via the
  pinned Marketplace action `crazy-max/ghaction-virustotal@936d8c5c00afe97d3d9a1af26d017cfdf26800a2`
  with `request_rate: 4` to respect the VirusTotal public API quota (4 requests/minute).
  The action handles large uploads automatically by using the `/files/upload_url`
  endpoint for files 32MB or larger. It returns analysis links in the `analysis` output.
  The workflow does not poll the VirusTotal API for final results or verdicts.
- **Issue creation**: opens a GitHub issue with the title "AV manual review
  for release <tag>" when VirusTotal upload succeeds and returns analysis links.
  The issue is created regardless of VirusTotal detection results, since CI does not
  poll the API to determine them. The issue body carries the release tag, the asset
  name, the Defender output, the VirusTotal analysis links, and a note that the
  operator should open the links and review the results manually. The issue deduplicates
  against an existing open issue for the same title. No issue is created if VirusTotal
  upload fails or returns no analysis links.

The workflow fails (red) when:
- Defender status is `tool_error` (scan command failed or Defender unavailable)
- Defender status is `detection` (threat detected)
- Defender status is empty (scan did not run)
- `VIRUSTOTAL_API_KEY` is not configured
- VirusTotal action upload fails
- VirusTotal analysis output is missing
- A required GitHub issue for manual review could not be created, and no
  existing open issue matched

The workflow passes (green) when both scanners run successfully with no Defender
detections and VirusTotal returns analysis links. Note that the workflow does not
fail on VirusTotal detections, since CI does not poll the API to know them. The
workflow is still post-release and non-gating for publication, but red means the
scan signal is invalid or VirusTotal upload failed.

### Why AV scanning exists

Curator's behavior profile reads as malware-shaped regardless of intent:
launching a process that injects a DLL (Relay), enumerating processes
(SingleInstanceGuard), writing URL-handler registry keys, running a named-pipe
server, downloading and extracting archives that contain executables (Nexus
acquisition, DMF install), and creating staging links
(`ProfileService.PrepareModRoot`: NTFS junctions on Windows, symlinks on Linux).
Each is legitimate for a mod manager and each matches a malware heuristic.
False positives are expected on early releases.

What is automatable in CI is the Defender + VirusTotal scan above. What is
not automatable:

- **Defender whitelist submission.** Microsoft has no public API for
  "submit my app for trust". The path is the manual Security Intelligence
  submission portal (https://www.microsoft.com/en-us/wdsi/filesubmission) per
  file (or per certificate, when cert-based reputation is established), with
  a short written justification.
- **SmartScreen reputation** is built automatically by Microsoft as real
  downloads accumulate against a signed binary; there is no submission form
  that accelerates it for non-EV certs.

The only thing close to "automated Defender trust" is EV code signing plus
accumulated download reputation. Everything else is either a CI signal
(Defender PowerShell scan, VirusTotal) or a manual portal submission.

## PR gate

`.github/workflows/curator-build.yml` runs on `pull_request` targeting `main`
(and `workflow_dispatch`). There is intentionally no `push` trigger; the release
workflow handles push-to-main.

- **Format job** (Ubuntu only): same-repo PRs run `dotnet format` and
  auto-commit any changes as `style: dotnet format [skip ci]`. Fork PRs and
  `workflow_dispatch` invocations run `dotnet format --verify-no-changes` (no commit).
- **Build + test matrix** (Windows + Ubuntu), depends on the format job.
- **AppImage packaging smoke** (Ubuntu 22.04), also depends on format. It
  self-contained-publishes the UI, publishes the native-AOT handler, stages a
  deterministic Relay fixture, packs with `vpk` 1.2.0, extracts without FUSE,
  checks package structure and executable modes, asserts the
  Velopack-generated internal desktop file carries
  `StartupWMClass=ModifAmorphic.ModificusCurator` (matching the pack id and the
  Curator window's WM_CLASS), verifies the feed's filename, size, SHA1, and
  SHA256 against the generated full nupkg, runs shell syntax checks on the four
  production Linux scripts (`install.sh`, `install-standalone.sh`,
  `uninstall.sh`, `uninstall-standalone.sh`), and runs the AppImage
  installer, AppImage uninstaller, and standalone uninstaller test harnesses.
  It uploads no artifact.
- **paths-ignore** skips release-please's bot-authored release PRs (they only
  touch `CHANGELOG.md` and `.release-please-manifest.json`), so those do not
  run the build gate.
- No `actions/upload-artifact` step. Release assets are produced by the
  release workflow.

## Linux installers

`scripts/install.sh` is the recommended Linux installer. It installs the
self-contained AppImage served from `raw/main`. By default it installs the
latest STABLE AppImage; pass `--prerelease` (or set `CURATOR_PRERELEASE=1`) to
install the latest prerelease:

```sh
# stable (default)
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
# prerelease
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh -s -- --prerelease
```

The script does NOT query the GitHub API or infer the asset filename. It
resolves the AppImage URL from `scripts/release.env`, a small `KEY=value`
manifest the release pipeline maintains on every release (see the
`update-manifest` job above). It fetches the manifest from
`https://raw.githubusercontent.com/<repo>/main/scripts/release.env` (CDN, no
auth, no rate limit), parses `APPIMAGE_RELEASE_URL` and
`APPIMAGE_PRE_RELEASE_URL` line by line (not sourced, for safety even though
the file is ours), and downloads the URL matching the selected channel. It
downloads or accepts a local `CURATOR_APPIMAGE`, stages the candidate on the
destination filesystem, sets executable mode, extracts it with
`--appimage-extract`, validates the Velopack desktop/icon metadata plus the UI,
NXM handler, Relay, `UpdateNix`, and `sq.version`, then atomically renames it
over the stable installed AppImage at
`~/.local/share/Modificus Curator/appimage/Modificus.Curator.AppImage`. Only
after validation does it update its user desktop entry (writing
`StartupWMClass=ModifAmorphic.ModificusCurator` so the AppImage's window groups
under Curator, matching the Curator window's WM_CLASS and the Velopack pack id),
icon, and shared command symlink. A failed candidate leaves the previous
AppImage usable. It uses no root privileges.

The AppImage installer supports `INSTALL_ROOT`, `BIN_LINK`, `CURATOR_REPO`,
`CURATOR_APPIMAGE`, and `CURATOR_PRERELEASE`. Its deterministic shell harness
(`scripts/tests/test-install.sh`) uses a fake extractable AppImage and
isolated HOME/XDG paths, so it needs neither FUSE nor network.

`scripts/install-standalone.sh` is the standalone tarball installer. It follows
the same stable-by-default and `--prerelease` contract, but reads `RELEASE_URL`
or `PRE_RELEASE_URL`:

```sh
# stable (default)
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install-standalone.sh | sh
# prerelease
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install-standalone.sh | sh -s -- --prerelease
```

After downloading, the script extracts to a temp dir, validates
`app/Modificus.Curator` + `relay/mod_relay.exe` before touching the
install root, then:

- Installs into `${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/`
  (the space is intentional; it matches `AppPaths.AppDataDir`).
- Replaces only `app/` and `relay/` under that root, never the root itself
  (it also holds `profiles/`, `mods/`, `logs/`, `config.json`).
- Marks `app/Modificus.Curator` and `app/Modificus.Curator.NxmHandler`
  executable.
- Symlinks `Modificus.Curator` into `~/.local/bin/modificus-curator` (creating
  `~/.local/bin` if needed, no sudo); on failure, warns and prints the
  executable path.
- Warns (non-fatal) if `dotnet --list-runtimes` does not list
  `Microsoft.NETCore.App 10.`.

The Nexus destination handles explicit `nxm://` registration;
neither installer duplicates that system association.

The standalone installer supports testing overrides (`INSTALL_ROOT`, `BIN_LINK`,
`CURATOR_REPO`, `CURATOR_ARCHIVE`, `CURATOR_PRERELEASE`) so extraction +
install can be exercised against a fake archive in a temp dir without touching
real user data or hitting the network. The scripts in `main` are authoritative;
users always get the current version, installing the latest stable by default
or the latest prerelease on request. Users wanting a specific release manage
the install themselves.

Both Linux distributions share user data and may coexist; whichever installer
ran most recently controls the common convenience symlink.

## Linux uninstallers

Each distribution has its own per-user uninstaller. Both are self-contained
(POSIX `/bin/sh`, no sourced or downloaded helper) so a raw-piped invocation
stays standalone and network-independent. Both reject root execution, validate
all destructive paths (nonempty, absolute, not `/`) before mutation, use
explicit `--` option terminators on destructive utilities, never follow symlink
targets (a symlink is unlinked, its target left in place), treat absent owned
paths as success (idempotent re-runs), count any owned-item removal failure
toward a nonzero exit, and accept `INSTALL_ROOT`, `BIN_LINK`,
`VELOPACK_STATE_DIR`, `HOME`, and `XDG_DATA_HOME` testing overrides.
`VELOPACK_STATE_DIR`'s final component must be exactly
`ModifAmorphic.ModificusCurator`; in `--purge-data` mode `INSTALL_ROOT`'s final
component must be exactly `Modificus Curator`.

`scripts/uninstall.sh` is the default AppImage uninstaller. With no flags it
removes the installed AppImage, AppImage-owned desktop/icon integration,
AppImage-managed NXM files, and the app-specific Velopack state at
`/var/tmp/velopack/ModifAmorphic.ModificusCurator`. Removing the Velopack state
also clears retained or pending local update packages, preventing them from
immediately advancing a newly installed acceptance-test base. Default mode
preserves profiles, mods, config, logs, app state, and standalone `app/` and
`relay/` payloads. It removes the shared command link only when its immediate
readlink target is the exact installed AppImage path, and removes the NXM
desktop entry only when it contains the exact full line
`Exec="<managed handler path>" %u` (fixed full-line ownership, never a
substring).
It preserves `AppUpdates.SourceOverride` and reminds the user to clear it
separately before testing production update sources.

`scripts/uninstall-standalone.sh` is the standalone uninstaller. With no flags
it removes the standalone `app/` and `relay/` directories (recursively) under
the install root. It removes the shared command link only when its immediate
readlink value is exactly `<install root>/app/Modificus.Curator`, and removes
the NXM desktop entry only when it contains the exact full line
`Exec="<install root>/app/Modificus.Curator.NxmHandler" %u` (fixed-line
ownership via `grep -Fx`, never a substring match, mirroring
`LinuxNxmHandlerRegistrar.FormatExec`). Default mode preserves profiles, mods,
config, logs, app state, the AppImage distribution (`appimage/`,
`nxm-handler/`, the AppImage-owned desktop/icon), and the app-specific Velopack
state (the standalone build does not use Velopack).

Either uninstaller's explicit `--purge-data` mode performs a clean Linux
removal. The purge semantics match between the two scripts (the small
safety-critical purge block is mirrored, not shared, so either raw-piped script
remains standalone): remove the exact main Curator desktop entry and
application icon, remove the app-specific Velopack state, remove the shared
command link when its target is the install root itself or anything under it
(covering both the standalone and AppImage links; external targets and regular
files are preserved), remove the exact Curator NXM desktop entry regardless of
whether its Exec points at the standalone or AppImage-managed handler (no
`xdg-mime` call), and recursively delete the strictly basename-validated
`Modificus Curator` data root. This intentionally removes all user data and
both Linux distributions under that root. Shared XDG parent directories
(`applications/`, the icon hierarchy) are never removed, only the exact
desktop/icon files. The full-removal success banner prints only when there were
no failures. Because the purge semantics match, one `--purge-data` invocation
is a complete Linux removal regardless of which uninstaller runs it.

The isolated harnesses `scripts/tests/test-uninstall.sh` and
`scripts/tests/test-uninstall-standalone.sh` cover default-mode ownership and
preservation, exact desktop/command-link matching, path safety, NXM ownership,
pending Velopack state (AppImage only), purge behavior for both distributions,
BIN_LINK ownership rules, paths with spaces, injected `rm` failure, and
idempotency, all under isolated HOME/XDG and Velopack-state paths with no real
`HOME` or `/var/tmp` use.

## Sandbox rehearsal (operator procedure)

The release pipeline can be rehearsed end to end against a throwaway branch so
no real release or tag lands on `main` until the pipeline is verified. The
rehearsal is temporary and leaves `main` untouched:

1. Create a throwaway target branch (for example `release-sandbox`) from the
   current `main`.
2. Temporarily point release-please at the sandbox branch by setting
   `target-branch` in `.release-please-config.json`, and point the release
   workflow's `on.push.branches` (and `workflow_dispatch`) at the same target
   branch.
3. For a fresh repo (not applicable to Curator, which has already shipped
   v0.1.0), use a one-shot config-level `release-as` override in
   `.release-please-config.json` to force the first release version, then remove
   it immediately after shipping. Curator has already passed this bootstrap
   phase.
4. Push the branch and land a conventional commit so release-please opens a
   release PR against the sandbox branch.
5. Merge the release PR on the sandbox branch and let the release workflow
   run end to end: release-please cuts the release, the Windows artifacts plus
   both Linux distributions are built and attested, and the AV/VT dispatch fires.
6. Inspect the produced release, archives, attestations, and AV/VT result.
7. Delete the sandbox release and its tag.
8. Strip the sandbox settings (revert `target-branch` and the workflow
   trigger-branch changes) before opening the PR to `main`, so the production
   config stays normal and persistent.

To test `curator-post-release-av.yml` after it exists on the default branch:
- Run it via `workflow_dispatch` against an existing release, providing the
   `tag_name` and `windows_asset` inputs manually.

This is an operator rehearsal procedure, not part of the user-facing release
path.

## Scope

What the release pipeline provides today, and what it does not:

- **Code signing.** Releases are unsigned, so Windows SmartScreen warns on the
  installer's first run. SmartScreen reputation is not established.
- **In-app update-check UI.** The Windows installer and Linux AppImage support
  startup/manual checks plus Download and Restart through Velopack. The portable
  Windows and standalone Linux builds do not. Relay is bundled as
  latest-per-Curator-release and is not updated independently in-app.
- **Linux arm64.** Only `win-x64` and `linux-x64` are published. SteamOS on
  Steam Deck uses the common Linux x64 AppImage path where installation and
  self-update work; no Steam Deck-specific UI or launch behavior is provided.
- **Relay pinning or Relay provenance sidecar.** The bundled Relay is whatever
  was the newest non-draft prerelease at workflow-run time.
- **AV/VT scanning for the portable ZIP.** The post-release AV/VT scan only
  covers the Velopack installer; the portable ZIP is not scanned.

## Sources

- Workflows: `.github/workflows/release.yml` (release-please + asset build +
  attestation + dispatch), `.github/workflows/curator-post-release-av.yml`
  (repository_dispatch AV/VT scan), `.github/workflows/curator-build.yml`
  (PR gate).
- `scripts/install.sh` (the recommended AppImage installer) and
  `scripts/install-standalone.sh` (the standalone tarball installer), plus
  `scripts/uninstall.sh` and `scripts/uninstall-standalone.sh` (each
  with preserving and clean-uninstall modes), and the isolated
  install/uninstall harnesses.
- `scripts/release.env` (the install manifest both installers read and the
  `update-manifest` job maintains).
- Release-please config: `.release-please-config.json` +
  `.release-please-manifest.json`.
- GitHub Artifact Attestations (`actions/attest@v4`):
  https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations.
- Architectural details from the codebase: `src/ui/Program.cs`
  (the Velopack `VelopackApp.Build().Run()` lifecycle hook, compiled under
  `CURATOR_VELOPACK`), `src/nxm/NxmHandlerRelay.cs`
  (sibling-exe resolution), `src/nxm/{Windows,Linux}NxmHandlerRegistrar.cs`
  (runtime scheme registration), `src/relay-client/RelayLaunchService.cs`
  (launch-time Relay resolution: configured `RelayDir` first, then the
  cross-platform app-local `relay/` fallback, then the Windows-only portable
  sibling; no first-run provisioning),
  `src/config/AppPaths.cs` (the app-data root and default `RelayDir`).
