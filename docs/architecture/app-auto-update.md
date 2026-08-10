# App auto-update: architecture

Curator can update itself in place when it runs from a
[Velopack](https://github.com/velopack/velopack) package: the Windows installer
or the self-contained Linux AppImage. On startup the app
checks GitHub Releases for a newer version of itself; when one is available a
dismissible pill appears in the shell status strip and an "Updates" section in
Settings surfaces the current version, a manual check, and a Download and
Restart action. The download, apply, and relaunch are handled by Velopack, so
routine updates need no installer re-run. On Windows, a manual installer upgrade works too:
running a newer `Setup.exe` over an existing install offers an Upgrade button
(an older version offers Downgrade, the same version offers Repair), so users
who download the latest installer get the latest version the conventional way.
The portable Windows ZIP and standalone Linux tarball remain non-Velopack
distributions and update manually.

> Public surface, exact signatures, and DI registration are documented in the
> [UI reference](../reference/ui.md). This doc covers the architecture and
> the why.

## Architecture

```
 startup  ─►  AppUpdateCheckRunner.Start()  ─►  fire-and-forget, thread-pool task
                                                          │
                                                          ▼
                          IAppUpdateService.CheckForUpdatesAsync()
                          (VelopackAppUpdateService wraps UpdateManager + GithubSource)
                                                          │
              ┌───────────────────────────────────────────┴───────────────────────────┐
              │ no update / unsupported / swallowed failure                            │ update available
              ▼                                                                       ▼
   publishes null on LastCheckResult + raises                          publishes AppUpdateInfo on LastCheckResult
   UpdateStateChanged once (clears any prior)                          + raises UpdateStateChanged
                                                                                      │
                                                                                      ▼
                          ShellViewModel (status-strip pill) + SettingsViewModel (Updates section)
                          read LastCheckResult, marshaled to the UI thread via the Action<Action> seam
                                                                                      │
                                          user clicks the pill / "Download and Restart"
                                                                                      ▼
                          ConfirmAsync ─► ShowProgressAsync(DownloadUpdatesAsync) ─► ApplyUpdatesAndRestart()
                                                                             (process exits; Velopack relaunches)
```

## Packaged-build scope

Self-update is meaningful only when the running app is itself a Velopack
package. The Windows installer and Linux AppImage meet that condition. The
Velopack package (1.2.0) is
conditionally referenced in `src/ui/Modificus.Curator.UI.csproj`, gated on the
MSBuild property `CuratorUseVelopack=true`, which defines the
`CURATOR_VELOPACK` compilation symbol. The Velopack lifecycle hook
(`VelopackApp.Build().Run()`) is the first thing in `Program.Main` behind that
symbol, so Velopack can manage the app lifecycle (install hooks, an update
applied on a previous shutdown, fast-startup hooking) before Avalonia starts.

The release workflow produces platform-specific feeds and payloads. Windows
publishes `releases.win.json` and its nupkg. Linux publishes
`releases.linux-x64.json`, the self-contained AppImage, a full nupkg, and a
delta when a prior Linux package exists. Velopack embeds the `linux-x64`
channel in the AppImage, so `UpdateManager` selects that feed automatically.

The Linux standalone publish omits `CuratorUseVelopack`, as does the portable
Windows publish. Those builds register the no-op update service and continue to
update manually.

## The update source

Updates come from the Curator GitHub repository's releases. The Velopack source
is constructed anonymously:

```csharp
var source = new GithubSource(
    "https://github.com/ModifAmorphic/darktide-modificus-curator",
    accessToken: null,
    prerelease: true,
    downloader: null);
var manager = new UpdateManager(source);
```

`GithubSource` lives in the `Velopack.Sources` namespace. The token is `null`
(anonymous), which is subject to GitHub's unauthenticated rate limit (60
requests/hour per IP). That is ample for a single check per startup plus the
occasional manual check; the check is best-effort anyway (a rate-limit hit just
means the notice does not appear this session). Prereleases are included because
every release today is a prerelease; excluding them would hide every published
build.

`downloader: null` is the documented default in Velopack 1.2.0; Velopack
substitutes its own HttpClient-based downloader. `new UpdateManager(source)` is
the `IUpdateSource` overload (the manager takes the source, not a URL).

The source is config-driven, not hardcoded. `VelopackAppUpdateService` reads
`CuratorConfig.AppUpdates.SourceOverride` once at construction, via the injected
`IConfigLoader` (the same pattern every other service uses to read config;
`UpdateManager` is built once with its source, so the value is not held beyond
the constructor). When `SourceOverride` is null or whitespace (the default, and
the production value), the manager is built from the `GithubSource` above. When
it is set (a local directory path or a URL, in `config.json` under
`AppUpdates`), the manager is built from `UpdateManager`'s `urlOrPath` overload
instead:

```csharp
// A directory path is read straight off disk (expecting the running package's
// channel feed alongside the .nupkg); a URL is fetched.
var manager = new UpdateManager(sourceOverride);
```

This is how local update testing and self-hosted update feeds work, with no code
change: set the field in `config.json`, run, and clear it to revert. Both
overloads are documented in Velopack 1.2.0:
`new UpdateManager(string urlOrPath, UpdateOptions? options = null, IVelopackLocator? locator = null)`
and `new UpdateManager(IUpdateSource source, UpdateOptions? options = null, IVelopackLocator? locator = null)`.

## `IAppUpdateService`: an engine-neutral interface

`IAppUpdateService` exposes the check, download, and apply flow without leaking
a single Velopack type. The UI layer (the shell, the Settings destination) depends
on this interface and on the small `AppUpdateInfo` record, never on Velopack
directly. This keeps the update engine swappable and lets every consumer gate
its affordances on `IsUpdateSupported` rather than scattering Velopack
conditionals across the UI.

The public surface:

```csharp
public sealed record AppUpdateInfo(string TargetVersion, string? Notes);

public interface IAppUpdateService
{
    bool IsUpdateSupported { get; }
    string? CurrentVersion { get; }
    AppUpdateInfo? LastCheckResult { get; }
    AppUpdateInfo? UpdatePendingRestart { get; }
    event EventHandler? UpdateStateChanged;

    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);
    Task DownloadUpdatesAsync(CancellationToken ct = default);
    void ApplyUpdatesAndRestart();
}
```

The shape deliberately mirrors `IUpdateCheckService` (Integrations), the mod
update check: a best-effort availability check that never throws to the caller
for non-cancellation failures, plus a state-holding
`LastCheckResult` / `UpdatePendingRestart` surface published under a lock
together with the `UpdateStateChanged` event.

- `IsUpdateSupported`: `true` only when the running app is a Velopack install
  and the `UpdateManager` initialized. The entire UI update surface (the
  notice, the download button, apply) is gated on this, so a non-Velopack build
  (the standalone Linux tarball, portable Windows ZIP, or a dev run from
  `bin/`) simply shows nothing in the pill and a
  disabled check in Settings.
- `CurrentVersion`: the installed app version as a string
  (`UpdateManager.CurrentVersion.ToString()`), or `null` when unsupported. The
  UI shows it next to the available version so the user can compare. This is
  `UpdateManager.CurrentVersion` (a `SemanticVersion`), not
  `VelopackRuntimeInfo`, which carries the Velopack library version only.
- `LastCheckResult`: the most recent check result, or `null` before the first
  check, when no update was found, or when a check failed (a failure leaves the
  prior value untouched).
- `UpdatePendingRestart`: the update that has been downloaded and is waiting
  for the next restart, or `null` until a download succeeds. Set by
  `DownloadUpdatesAsync`; consumed by `ApplyUpdatesAndRestart`.
- `UpdateStateChanged`: raised on the completing thread when `LastCheckResult`
  or `UpdatePendingRestart` changes (once per successful check, plus once on a
  successful download). Never raised on a swallowed check failure (nothing
  changed). UI handlers marshal to the UI thread (see Threading).
- `CheckForUpdatesAsync`: returns the available update, or `null` when
  unsupported, no update is available, or the check failed. Never throws for
  non-cancellation failures; cancellation propagates as
  `OperationCanceledException`.
- `DownloadUpdatesAsync`: downloads the update the last check resolved,
  staging it for apply. Propagates its failures (the download is user-initiated
  and the user needs to see a checksum mismatch, a lock contention, or an IO
  error).
- `ApplyUpdatesAndRestart`: exits the process, applies the staged update, and
  relaunches under the new version. A no-op when no update has been downloaded.

### Conditional implementation behind `CURATOR_VELOPACK`

Two implementations live behind the one interface, selected at compile time in
the composition root:

- **`VelopackAppUpdateService`** (`#if CURATOR_VELOPACK`): the real impl,
  registered in the packaged Windows installer and Linux AppImage. Wraps a
  Velopack `UpdateManager`.
- **`NoopAppUpdateService`**: the default, registered everywhere else. Every
  member returns the neutral value: `IsUpdateSupported` is `false`,
  `CurrentVersion` / `LastCheckResult` / `UpdatePendingRestart` are `null`, the
  check returns a completed `null` task, `ApplyUpdatesAndRestart` is a no-op,
  and `UpdateStateChanged` is never raised. `DownloadUpdatesAsync` throws
  `NotSupportedException` rather than silently no-op-ing, because the UI gates
  the download on `IsUpdateSupported` (always `false` here): reaching the
  download path in an unsupported build is a wiring mistake worth surfacing.

```csharp
#if CURATOR_VELOPACK
services.AddSingleton<IAppUpdateService>(sp => new VelopackAppUpdateService(
    sp.GetRequiredService<IConfigLoader>(),
    sp.GetRequiredService<ILogger<VelopackAppUpdateService>>()));
#else
services.AddSingleton<IAppUpdateService, NoopAppUpdateService>();
#endif
```

Construction is defensive. `UpdateManager` throws
`Velopack.Exceptions.NotInstalledException` when the process is not running
from a Velopack install (a dev build launched from `bin`, or an unpackaged
run). `VelopackAppUpdateService`'s constructor catches that, logs a warning,
and leaves the manager `null`; `IsUpdateSupported` is then `false` and every
member short-circuits. This is the normal path for a non-packaged run, not an
error, hence warning rather than error.

### State holding, lock-protected write, lock-free read

`LastCheckResult`, `UpdatePendingRestart`, and the cached Velopack `UpdateInfo`
(the resolved update the download and apply steps hand back to Velopack) are
written from a background task and read by the UI thread. The write is taken
under an internal lock together with the `UpdateStateChanged` invocation, so a
subscriber observes the values that were just published. Reads are lock-free:
reference assignment is atomic on every target runtime, so a lock-free read can
at worst observe a one-check-stale value, corrected on the next event.

The cached `UpdateInfo` is cleared when a check finds no update, so a stale
download cannot be applied later.

## The check trigger: startup only, no periodic

`AppUpdateCheckRunner` (UI session glue) fires exactly one availability check
on startup, fire-and-forget. The composition root resolves the runner singleton
and calls `Start()` after the provider is built; `Start()` reads the
`CuratorConfig.AppUpdates.CheckOnStartup` toggle live and, when it is on,
dispatches the check on a thread-pool task and discards the returned `Task`.
The result lands through `IAppUpdateService.UpdateStateChanged`; the runner
itself surfaces nothing.

App updates are profile-independent, so unlike `UpdateCheckRunner` (the mod
update check) this class has no profile dependency and no periodic timer: one
check per startup. The mod-update feature has a periodic timer; app updates
deliberately do not. The manual check is a UI concern (the Settings
"Check for Updates" button) and calls `CheckForUpdatesAsync` directly, so it
always works regardless of the `CheckOnStartup` toggle.

The `CheckOnStartup` toggle gates ONLY the automatic startup check. When it is
off, no startup check runs and the status-strip update notice is suppressed
entirely (the notice is itself gated on the toggle, so even a manual check that
populates `LastCheckResult` cannot surface it; the manual Settings check stays
self-contained with its own Download-and-Restart button). The manual "Check for
Updates" button in Settings is always available. The toggle is surfaced in the
Settings Updates section (read-modify-save through `IConfigLoader`, no caching)
and read live on startup; the shell also re-reads it when Settings closes so the
notice visibility tracks a runtime toggle.

The startup fire is best-effort and never blocks startup. A wiring failure is
logged and swallowed by the composition root; the user sees nothing, and the
self-update notice simply never appears. `CheckForUpdatesAsync` is documented
to swallow its own non-cancellation failures, but a fire-and-forget `Task`
must never leak an unobserved exception, so the runner wraps the call in its
own try/catch as belt-and-suspenders. `OperationCanceledException` is expected
on shutdown (not an error); anything else is logged and swallowed.

## The UI surfaces

Two surfaces read `IAppUpdateService`: the shell status strip and the Settings
"Updates" section. Both subscribe to `UpdateStateChanged` and also reflect any
result that already landed during construction, so a check that completed
before the view model existed is shown immediately.

### The shell status-strip notice

A dismissible pill in the shell status strip, shown only when
`ShowAppUpdateNotice` is true: self-update must be supported, the automatic
startup check must be enabled (`CuratorConfig.AppUpdates.CheckOnStartup`), a
check must have found an update (`LastCheckResult` non-null), and the user must
not have dismissed it this session. The notice is gated on `CheckOnStartup`:
when automatic checks are disabled the notice is suppressed entirely, even if a
manual check populated `LastCheckResult` (the manual Settings check is the only
remaining path and is self-contained, with its own inline result plus a
Download-and-Restart button). The shell re-reads the toggle when leaving the Settings
destination, so turning the toggle off dismisses a showing notice immediately
and turning it back on re-enables it, without a restart. Clicking the pill is
the notice flow:

1. `ConfirmAsync` asks "vX is available, download and restart?".
2. On confirm, `ShowProgressAsync` runs `DownloadUpdatesAsync` under a
   buttonless, non-closeable modal spinner (the same `ProgressDialog` the DMF
   download uses).
3. On success, `ApplyUpdatesAndRestart` exits the process and Velopack
   relaunches under the new version.
4. On a download failure, an alert surfaces the error and the apply step is
   skipped.

Cancel on the confirm dismisses the notice for this session (cancel means
"dismiss for now"); the explicit dismiss button (the drawn close `<Path>` on
the pill) also dismisses for the session. Dismissal is session-only, not
persisted: a persisted dismissal would wrongly hide a later update, so the
notice re-shows next startup if an update is still available.

### The Settings "Updates" section

The Settings destination adds an "Updates" section that always renders (so
standalone, portable, and dev builds still see their version) with: the current version (or a
localized "unknown" when it cannot be resolved), a "Check for Updates" button
with an inline indeterminate spinner while a check runs, an inline status line
("up to date" or "vX is available"), and a "Download and Restart" button
visible only when an update is available. The manual check runs
`CheckForUpdatesAsync` off the UI thread and refreshes the inline status from
the result; a throw there surfaces a localized "check failed" status (the check
is best-effort, so a throw is a wiring problem, handled defensively).

"Download and Restart" runs the same flow as the shell pill without the
confirm step (the user is already in the dedicated Updates section): the
download under the `ProgressDialog` spinner, then
`ApplyUpdatesAndRestart`, with download failures surfacing an alert.

## Threading

The project convention forbids `ConfigureAwait(false)` in UI-layer code (it
hops async continuations to the threadpool and breaks UI-thread affinity for
`Window.ShowDialog`, `ObservableCollection` mutations, and INPC setters). The
self-update code follows it:

- The view models (`ShellViewModel`, `SettingsViewModel`) use no
  `ConfigureAwait(false)`. Their network calls run inside `Task.Run` (the
  startup runner's check, the manual check, and the download inside the
  `ShowProgressAsync` work delegate), where there is no `SynchronizationContext`
  and a bare `await` resumes on the threadpool naturally.
- `AppUpdateCheckRunner.RunAsync` is the one place
  `ConfigureAwait(false)` appears, inside its `Task.Run` block. This is the
  rule's narrow, documented exception for explicit background-task code (it
  mirrors `UpdateCheckRunner.RunAsync`): the work has no UI-thread affinity and
  the rule's exception is for exactly this shape.

`UpdateStateChanged` fires on a threadpool thread (the service publishes from
its background check). The shell and Settings handlers marshal to the UI
thread via an injectable `Action<Action>` seam before touching
`ObservableObject` bindings. This is the same seam `ModListViewModel` uses for
its `CheckCompleted` handler: registered in `CuratorComposition` as
`action => Dispatcher.UIThread.Post(action)`, and a synchronous
`action => action()` in tests. The Settings view model also flips a `volatile`
"has checked" flag from the threadpool thread before the marshal, so the
UI-thread refresh observes it.

## Error handling

Two policies, split by who initiated the action:

- **The availability check is silent.** A transient network failure or a GitHub
  rate limit during `CheckForUpdatesAsync` is swallowed and logged; the prior
  `LastCheckResult` is left unchanged and no event is raised. The caller has
  nothing to gain from catching (the worst case is the notice does not appear
  this session). Cancellation propagates so a cancelled check is not
  misreported as "no updates".
- **The download surfaces its failures.** `DownloadUpdatesAsync` propagates
  non-cancellation exceptions because the download is a user-initiated action
  whose errors the user needs to see. Velopack's download can raise
  `Velopack.Exceptions.ChecksumFailedException` (a payload integrity failure)
  and `Velopack.Exceptions.AcquireLockFailedException` (another Curator holds
  the update lock), plus IO errors; the UI catches these and shows an alert,
  and never proceeds to apply on a download failure.

## The Velopack lifecycle interaction

Velopack's default applies a downloaded update before the window shows on the
next launch. Curator keeps that default on: if a download completed but
`ApplyUpdatesAndRestart` was never called (the user dismissed the notice and
quit), the next launch applies it during `VelopackApp.Build().Run()`, before
Avalonia starts. `UpdateManager.IsUpdatePendingRestart` (and the staged
`VelopackAsset` on `UpdatePendingRestart`) reflect that state; no extra
Curator-side wiring is needed for it.

The download and apply hand Velopack the **asset**, not the `UpdateInfo`:
`DownloadUpdatesAsync(info, ...)` takes the `UpdateInfo` the check resolved,
but `ApplyUpdatesAndRestart(info.TargetFullRelease, restartArgs: null)` takes
`TargetFullRelease` (the `VelopackAsset`). The process exits when
`ApplyUpdatesAndRestart` is called; Velopack relaunches the new version.

## What is deliberately not done

- **Delta policy in the UI.** The UI does not choose full versus delta packages;
  Velopack does. The Linux release workflow seeds the prior `linux-x64` feed and
  full package so `vpk` can emit a delta. The first AppImage update downloads a
  full package because no local base nupkg exists yet.
- **Channel switching.** There is one feed (the repo's releases, prereleases
  included). No stable/beta channel toggle and no user-facing feed switcher. The
  feed source itself is operator-configurable for local testing and self-hosting
  via `CuratorConfig.AppUpdates.SourceOverride` (a machine-config field set in
  `config.json`, not a UI control), but there is no channel concept the user can
  pick from.
- **Code signing.** The Windows installer and the update payload are unsigned.
  SmartScreen warns on the first install of the setup exe (a known first-install
  UX issue); in-app updates do not re-trigger it, because Velopack applies them
  from the already-installed `Update.exe` rather than from a fresh download the
  OS vets. Signing is a separate release concern.
- **Download progress reporting.** The download runs under the indeterminate
  `ProgressDialog` (no percentage is surfaced). That is the intended design:
  Velopack's `Action<int>` progress callback is passed `null`.
- **Release notes.** `AppUpdateInfo.Notes` is populated from
  `info.TargetFullRelease.NotesMarkdown` (the notes live on the asset, not on
  `UpdateInfo`, which has no notes property). It is currently empty because
  `vpk pack` is not yet given `--releaseNotes`; the notice does not render
  notes. Plumbing the release body into the pack step is the follow-up.
- **Periodic polling.** One check per startup, plus the manual check. No
  background timer.
- **Portable distributions.** The standalone Linux tarball and portable
  Windows ZIP intentionally register the no-op service and retain their manual
  update paths.

## Verifying

The check, download, and apply path cannot be unit-tested end to end (it needs
a real Velopack package and a real feed), so it is verified manually on Windows
and with an installed Linux AppImage.
The `VelopackAppUpdateService` *consuming* logic IS unit-tested through the
`IAppUpdateService` interface (fakes drive the shell and Settings view models);
the Velopack integration itself is the manual path.

To stage a newer version locally:

1. Install an older version (for example, the setup exe at v0.3.0).
2. Publish and pack a newer version (run from the repo root). The `vpk`
   install uses a local tool path, mirroring the release workflow, so no global
   tool install or PATH change is needed:
   ```
   dotnet publish src\ui -c Release -r win-x64 --self-contained false -o publish -p:CuratorUseVelopack=true
   dotnet tool install vpk --version 1.2.0 --tool-path .vpk
   .vpk\vpk pack --packId ModifAmorphic.ModificusCurator --packVersion 0.3.1 --packDir publish --mainExe Modificus.Curator.exe --framework net10.0-x64-runtime -o releases
   ```
   (The release workflow also stages the Relay app-local under `relay/` before
   packing; that is optional for testing the update mechanism itself, needed
   only if you want the post-update app fully functional.)
3. Point the installed app at the local `releases` directory by setting
   `AppUpdates.SourceOverride` to that directory path in Curator's `config.json`
   (`VelopackAppUpdateService` reads it once at construction via the injected
   `IConfigLoader`; `UpdateManager`'s `urlOrPath` overload reads
   the package's channel feed straight off disk, so the local feed is tested without
   GitHub and without any code edit). Clear the field to revert to the
   production source. Alternatively, upload the platform feed and nupkg to a
   GitHub prerelease.

Then verify:

- The startup check finds the update and the status-strip pill appears.
- The pill flow (confirm, download under the progress dialog, apply + restart)
  lands on the new version, which Settings reports as the current version.
- The manual Settings "Check for Updates" and "Download and Restart" path works.
- Turning "Check for Curator updates on startup" off suppresses the startup
  check and the pill entirely (the manual Settings check still works).
- Auto-apply-on-startup: download, then quit without applying; on the next
  launch the update applies before the window shows.
- The `nxm://` handler still works after an update. Windows keeps its stable
  install path; Linux startup maintenance refreshes the persistent copied
  handler and its AppImage symlink only when Curator already owns the
  registration.
- The app-local Relay still resolves after an update (`current\relay\` on
  Windows, the mounted AppImage payload's `relay/` on Linux).
- In a standalone Linux, portable Windows, or dev run (`bin/Debug`, no Velopack
  package), the check never fires: no crash, `IsUpdateSupported` is false, the
  pill never shows, and the Settings check is disabled.
- A rate-limited GitHub API response does not crash the app (the pill simply
  does not appear that session).

## See also

- [UI reference](../reference/ui.md): `IAppUpdateService` and
  `AppUpdateInfo` public surface, `AppUpdateCheckRunner`, DI registration.
- [UI architecture](ui-architecture.md): where the self-update notice and the
  Settings "Updates" section sit in the shell, and the shared `Action<Action>`
  marshaling seam.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level
  tie-together.
