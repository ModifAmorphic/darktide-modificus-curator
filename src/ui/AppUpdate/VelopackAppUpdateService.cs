#if CURATOR_VELOPACK
using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Velopack;
using Velopack.Sources;

namespace Modificus.Curator.UI.AppUpdate;

/// <summary>
/// The Velopack-backed <see cref="IAppUpdateService"/>: the implementation that
/// is registered when <c>CURATOR_VELOPACK</c> is defined (a Velopack-packaged
/// build: a Windows install or a Linux AppImage). Wraps a
/// <see cref="UpdateManager"/> pointed at the Curator GitHub releases
/// (anonymous, stable releases only), or at a config-supplied source override
/// for local testing / self-hosted feeds, and exposes the check / download /
/// apply flow through the engine-neutral <see cref="IAppUpdateService"/> surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>The update source is config-driven, not hardcoded.</b> The source is read
/// once from <see cref="Modificus.Curator.Config.AppUpdatesConfig.SourceOverride"/>
/// (via the injected <see cref="IConfigLoader"/>) at construction:
/// <see cref="UpdateManager"/> is built once with its source, so the value is not
/// held beyond the ctor. <c>null</c> (the default) builds the production
/// anonymous <c>GithubSource</c>; a set value (a local directory path or a URL)
/// builds the manager from the <see cref="UpdateManager"/>'s <c>urlOrPath</c>
/// overload. This is how local update testing and self-hosted feeds work, with
/// no code change (set it in <c>config.json</c> under <c>AppUpdates</c>).</para>
/// <para>
/// <b>Construction is defensive.</b> <see cref="UpdateManager"/> throws
/// (notably <c>Velopack.Exceptions.NotInstalledException</c>) when the process
/// is not running from a Velopack install (a dev build launched from bin, or an
/// unpackaged run). The constructor catches that, logs a warning, and leaves the
/// manager <c>null</c>; <see cref="IsUpdateSupported"/> is then <c>false</c> and
/// every member short-circuits to the neutral value. This is the normal path for
/// a non-packaged run, not an error, hence warning rather than error.</para>
/// <para>
/// <b>State holding mirrors
/// <see cref="Modificus.Curator.Integrations.IUpdateCheckService"/>.</b> The
/// last check result, the pending-restart result, and the cached Velopack
/// <see cref="UpdateInfo"/> (the resolved update the download / apply steps hand
/// back to Velopack) are written under an internal lock together with the
/// <see cref="IAppUpdateService.UpdateStateChanged"/> invocation, so a
/// subscriber observes the values that were just published. Reads are lock-free;
/// see <see cref="IAppUpdateService"/> for the threading rationale.</para>
/// <para>
/// <b>Threading: no <c>ConfigureAwait(false)</c> in this class.</b> This is
/// UI-layer code, and the project convention (see AGENTS.md) forbids
/// <c>ConfigureAwait(false)</c> in the UI layer because it hops async
/// continuations off the captured context. Instead, callers invoke the async
/// members from inside <c>Task.Run</c> (the startup runner does; the VM commands
/// will), where there is no <see cref="SynchronizationContext"/> and the awaits
/// resume on the threadpool naturally without any <c>ConfigureAwait</c>. A bare
/// <c>await</c> here is correct under that calling convention.</para>
/// <para>
/// <b>Best-effort check, user-initiated download.</b>
/// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> swallows non-cancellation
/// failures (a transient network error or a GitHub rate limit leaves the prior
/// result unchanged and raises no event); cancellation propagates. By contrast,
/// <see cref="IAppUpdateService.DownloadUpdatesAsync"/> propagates its failures
/// (a checksum mismatch, an update-lock contention, an IO error) because the
/// download is a user action whose errors the user needs to see.</para>
/// <para>
/// <b>Not directly unit-tested.</b> Constructing a real
/// <see cref="UpdateManager"/> requires a Velopack install + a real feed, so the
/// Velopack integration itself is a manual path. The <c>consuming</c> logic (the
/// shell notice, the Settings Updates section, the startup runner) IS unit-tested
/// through <see cref="IAppUpdateService"/> via fakes; see the test assembly's
/// <c>ShellViewModelAppUpdateTests</c> / <c>SettingsViewModelAppUpdateTests</c> /
/// <c>AppUpdateCheckRunnerTests</c>.</para>
/// </remarks>
internal sealed class VelopackAppUpdateService : IAppUpdateService
{
    private const string RepoUrl = "https://github.com/ModifAmorphic/darktide-modificus-curator";

    private readonly UpdateManager? _manager;
    private readonly ILogger<VelopackAppUpdateService> _logger;

    /// <summary>
    /// Guards the writes to <see cref="_lastCheckResult"/>,
    /// <see cref="_updatePendingRestart"/>, and <see cref="_pendingUpdate"/>
    /// together with the
    /// <see cref="IAppUpdateService.UpdateStateChanged"/> invocation, so a
    /// subscriber observes the values that were just published.
    /// </summary>
    /// <remarks>
    /// <see cref="IAppUpdateService.UpdateStateChanged"/> is invoked inside this
    /// lock. Subscribers must not synchronously call back into the service in a
    /// way that re-enters a check (it would contest this lock); the UI marshal
    /// to the UI thread avoids the re-entry. The lock body is a few assignments
    /// plus one invoke, so the hold time is minimal.</remarks>
    private readonly object _stateLock = new();

    private AppUpdateInfo? _lastCheckResult;
    private AppUpdateInfo? _updatePendingRestart;

    /// <summary>
    /// The Velopack <see cref="UpdateInfo"/> resolved by the last successful
    /// check that found an update. Cached (and not exposed) so
    /// <see cref="IAppUpdateService.DownloadUpdatesAsync"/> and
    /// <see cref="IAppUpdateService.ApplyUpdatesAndRestart"/> can hand the
    /// target asset back to Velopack without re-checking. Cleared when a check
    /// finds no update (so a stale download is not applied later).
    /// </summary>
    private UpdateInfo? _pendingUpdate;

    public VelopackAppUpdateService(IConfigLoader configLoader, ILogger<VelopackAppUpdateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (configLoader is null)
        {
            throw new ArgumentNullException(nameof(configLoader));
        }

        try
        {
            // The latest stable release is authoritative even when it is
            // semver-older than the installed version, so an install sitting
            // on a newer pre-release is offered the latest stable in-app and
            // self-heals onto the release track. Velopack applies such an
            // update as a full-package swap (no deltas), so the ordinary
            // download / apply flow needs no other change.
            var options = new UpdateOptions { AllowVersionDowngrade = true };

            // Read the optional source override once at construction:
            // UpdateManager is built once with its source, so the value is not
            // held beyond the ctor. null/whitespace (the default) uses the
            // production GithubSource below.
            var sourceOverride = configLoader.Load().AppUpdates.SourceOverride;
            if (!string.IsNullOrWhiteSpace(sourceOverride))
            {
                // Local testing / self-hosted feed: UpdateManager accepts a local
                // directory path or a URL as its urlOrPath (the string overload;
                // a directory is read straight off disk, expecting a Velopack
                // releases feed alongside the .nupkg). Used only when the
                // operator sets AppUpdates.SourceOverride in config.json; null in
                // production (the GithubSource path below).
                _manager = new UpdateManager(sourceOverride, options);
                _logger.LogInformation("App update source overridden to {Source}.", sourceOverride);
            }
            else
            {
                // Production: anonymous (no token), stable releases only;
                // prereleases are excluded from the feed entirely. The
                // downloader argument is null by design: it is the parameter's
                // documented default in Velopack 1.2.0, and Velopack substitutes
                // its own HttpClient-based downloader.
                var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false, downloader: null);
                _manager = new UpdateManager(source, options);
            }
        }
        catch (Exception ex)
        {
            // NotInstalledException is the expected throw when the process is
            // not running from a Velopack install (a dev run). Treat as
            // unsupported: IsUpdateSupported stays false and the members
            // short-circuit. Warning, not error: this is the normal path for a
            // non-packaged run.
            _logger.LogWarning(ex,
                "Velopack UpdateManager could not be initialized; app self-update is disabled.");
            _manager = null;
        }
    }

    /// <inheritdoc />
    public bool IsUpdateSupported => _manager is not null && _manager.IsInstalled;

    /// <inheritdoc />
    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <inheritdoc />
    public AppUpdateInfo? LastCheckResult => _lastCheckResult;

    /// <inheritdoc />
    public AppUpdateInfo? UpdatePendingRestart => _updatePendingRestart;

    /// <inheritdoc />
    public event EventHandler? UpdateStateChanged;

    /// <inheritdoc />
    public async Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (!IsUpdateSupported)
        {
            return null;
        }

        // Observe cancellation even though Velopack's CheckForUpdatesAsync
        // takes no token: a cancelled caller should not pay for a network round
        // trip.
        ct.ThrowIfCancellationRequested();

        UpdateInfo? info;
        try
        {
            // Bare await by convention (no ConfigureAwait(false)): callers run
            // this inside Task.Run, where there is no SynchronizationContext.
            // CheckForUpdatesAsync returns null when no update is available,
            // despite its non-nullable signature annotation.
            info = await _manager!.CheckForUpdatesAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: a transient failure or a rate limit is not something the
            // caller can act on. Return null (the neutral result) but leave the
            // published LastCheckResult state unchanged and raise no event, so the
            // shell notice is unaffected by a transient failure. Cancellation
            // already propagated above.
            _logger.LogError(ex, "App update availability check failed.");
            return null;
        }

        if (info is null)
        {
            // No update available: clear any prior result and the cached
            // pending update so a stale download cannot be applied later.
            PublishCheck(result: null, pendingUpdate: null);
            return null;
        }

        var poco = new AppUpdateInfo(
            info.TargetFullRelease.Version.ToString(),
            info.TargetFullRelease.NotesMarkdown);
        return PublishCheck(poco, info);
    }

    /// <inheritdoc />
    public async Task DownloadUpdatesAsync(CancellationToken ct = default)
    {
        // _pendingUpdate is null when no check has resolved an update, or when
        // self-update is unsupported (the check short-circuited without
        // populating it). Either way the download cannot proceed: surface it as
        // a programming error (the UI gates the download on IsUpdateSupported
        // and a non-null LastCheckResult).
        var info = _pendingUpdate;
        if (info is null)
        {
            throw new InvalidOperationException(
                "No app update has been resolved. Call CheckForUpdatesAsync first.");
        }

        // Bare await by convention (no ConfigureAwait(false)): callers run this
        // inside Task.Run. Velopack's DownloadUpdatesAsync takes an Action<int>
        // progress callback, which is passed null: the UI runs the download
        // under an indeterminate modal spinner, so no percentage is surfaced.
        // Failures (checksum mismatch, lock contention, IO) propagate to the
        // caller.
        await _manager!.DownloadUpdatesAsync(info, progress: null, ct);

        // Success: surface the pending restart under the lock together with the
        // event raise. The pending-restart value is the same as the last check
        // result (the update that was just downloaded).
        PublishPendingRestart(_lastCheckResult);
    }

    /// <inheritdoc />
    public void ApplyUpdatesAndRestart()
    {
        var info = _pendingUpdate;
        if (_manager is null || info is null || _updatePendingRestart is null)
        {
            _logger.LogInformation(
                "ApplyUpdatesAndRestart called with no downloaded update; nothing to apply.");
            return;
        }

        // ApplyUpdatesAndRestart terminates this process and relaunches under
        // the new version. Hand it the target asset (the restartArgs overload is
        // null here: no custom restart arguments).
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease, restartArgs: null);
    }

    /// <summary>
    /// Publishes a check result + the cached pending Velopack update under
    /// <see cref="_stateLock"/> together with the
    /// <see cref="UpdateStateChanged"/> invocation, so a subscriber observes the
    /// values that were just published. Returns the check result for caller
    /// convenience. A <c>null</c> <paramref name="result"/> clears a prior
    /// result (no update available).
    /// </summary>
    private AppUpdateInfo? PublishCheck(AppUpdateInfo? result, UpdateInfo? pendingUpdate)
    {
        lock (_stateLock)
        {
            _lastCheckResult = result;
            _pendingUpdate = pendingUpdate;
            UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    /// <summary>
    /// Publishes the pending-restart result under <see cref="_stateLock"/>
    /// together with the <see cref="UpdateStateChanged"/> invocation. Called on
    /// a successful download; the pending-restart value is the same as the last
    /// check result.
    /// </summary>
    private void PublishPendingRestart(AppUpdateInfo? pending)
    {
        lock (_stateLock)
        {
            _updatePendingRestart = pending;
            UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
#endif
