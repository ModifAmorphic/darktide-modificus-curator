using System.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Default <see cref="IRelayLaunchService"/>. A thin orchestrator: it runs
/// the platform-agnostic launch flow (discover → check completeness → prepare
/// mod root → launcher-exists → spawn → result mapping) and delegates the
/// platform-varying pieces to an <see cref="IPlatformLaunchStrategy"/> selected
/// once at DI registration from the runtime OS. Contains no per-launch OS branch.
/// </summary>
/// <remarks>
/// <para>
/// The strategy owns the spawn (direct <c>Process.Start</c> on Windows;
/// <c>proton run</c> + both <c>STEAM_COMPAT_*</c> env vars + <c>Z:\</c>-translated
/// args on Linux), the per-platform required discovery fields, and the launch
/// label for logging.</para>
/// <para>
/// Registered as a singleton: it holds no per-launch state. The active strategy
/// does not change at runtime. Reads <see cref="CuratorConfig"/> live from
/// <see cref="IConfigLoader"/> on each launch (one snapshot per op). Tests inject
/// the concrete Windows/Linux strategy (with a fake
/// <see cref="IProcessLauncher"/>) to exercise either path on any CI OS.</para>
/// </remarks>
internal sealed class RelayLaunchService : IRelayLaunchService
{
    /// <summary>The launcher executable filename (a Windows binary, run under
    /// Proton on Linux). Lives in <see cref="CuratorConfig.RelayDir"/>.</summary>
    internal const string LauncherExecutableName = "mod_relay.exe";

    /// <summary>
    /// The app-local Relay folder name. A Velopack-packaged build ships Relay
    /// alongside the app under <c>&lt;AppContext.BaseDirectory&gt;/relay/</c>
    /// (a Windows install in place, or a Linux AppImage payload mounted under a
    /// temporary AppDir). This is the fallback when no Relay is deployed at
    /// <see cref="CuratorConfig.RelayDir"/> (the data-root default used by the
    /// standalone Linux layout).
    /// </summary>
    internal const string AppLocalRelayFolderName = "relay";

    /// <summary>
    /// The Steam app id for Darktide. The launcher defaults to this value when
    /// <c>--steam-app-id</c> is omitted; Curator relies on that default and only
    /// emits <c>--steam-app-id</c> to override it (which the current config does
    /// not surface (see <c>ServiceCollectionExtensions</c> / future config work).
    /// </summary>
    internal const int DarktideSteamAppId = 1361210;

    private readonly IProfileService _profiles;
    private readonly ISteamService _steam;
    private readonly IConfigLoader _configLoader;
    private readonly IPlatformLaunchStrategy _strategy;
    private readonly ILogger<RelayLaunchService> _logger;

    public RelayLaunchService(
        IProfileService profiles,
        ISteamService steam,
        IConfigLoader configLoader,
        IPlatformLaunchStrategy strategy,
        ILogger<RelayLaunchService> logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public LaunchResult Launch(Guid profileId)
    {
        try
        {
            // One live config snapshot for the whole launch. RelayDir is read
            // once here; a runtime config change via the Settings window takes
            // effect on the next launch. The log file is the bootstrap-resolved,
            // process-pinned path (LoggingBootstrap.CurrentLogFile), not the raw
            // template, read once here too.
            var config = _configLoader.Load();

            // Discovery first: if we cannot launch, do not touch the profile's
            // mod root (no point writing mods.lst for a launch that won't happen).
            var discovery = _steam.Discover();
            var missing = _strategy.RequiredDiscoveryFields(discovery);
            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "Discovery incomplete ({Platform}); missing: {Fields}.",
                    _strategy.Name, string.Join(", ", missing));
                return new LaunchResult(
                    LaunchStatus.DiscoveryIncomplete,
                    Message: $"Steam discovery is missing required fields: {string.Join(", ", missing)}.",
                    MissingDiscoveryFields: missing);
            }

            // PrepareModRoot writes mods.lst + ensures the mod root exists and
            // returns the --mod-path. A staging-link creation failure surfaces as
            // the raised built-in exception (Win32Exception from the junction
            // path on Windows, IOException / UnauthorizedAccessException from the
            // symlink path on Linux) and is mapped to StagingFailed here; the
            // exception's message is carried on the result so the UI can append
            // it to the localized framing. KeyNotFoundException (unknown profile)
            // is caught below and mapped to LaunchStatus.Error.
            string modPath;
            try
            {
                modPath = _profiles.PrepareModRoot(profileId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _logger.LogError(ex, "Staging failed for profile {Id}.", profileId);
                return new LaunchResult(
                    LaunchStatus.StagingFailed,
                    Message: ex.Message,
                    MissingDiscoveryFields: Array.Empty<string>());
            }

            var launcherPath = ResolveLauncherPath(
                config.RelayDir, AppContext.BaseDirectory, OperatingSystem.IsWindows());
            if (launcherPath is null)
            {
                // The configured RelayDir and the packaged fallbacks
                // (the app-local <base>/relay/ payload on both OSes, plus the
                // <base>/../relay/ sibling archive layout on Windows) all
                // missed. Report the configured path in the error: that is where
                // Curator looks first and what a user would reconfigure. The
                // packaged fallbacks are internal, not something to surface.
                var configuredPath = Path.Combine(config.RelayDir, LauncherExecutableName);
                _logger.LogError(
                    "Relay launcher not found at {Path} (nor under the packaged fallbacks below {Base}).",
                    configuredPath, AppContext.BaseDirectory);
                return ErrorResult($"Relay launcher not found at '{configuredPath}'.");
            }

            var gameBinary = discovery.DarktideGameBinaryPath!;
            // The log file is resolved once at startup (a datetime-named,
            // process-pinned path) by LoggingBootstrap; Relay writes the same
            // per-process file. Fall back to the configured value if the
            // bootstrap has not run (tests, edge hosts).
            var logFile = LoggingBootstrap.CurrentLogFile ?? config.Logging.LogFile;

            // Read the profile's launch settings fresh on each launch (they apply next launch; editing
            // is unlocked while Darktide runs). GetLaunchSettings throws KeyNotFoundException for an
            // unknown profile, caught below as Error (PrepareModRoot would have thrown it first).
            var launchSettings = _profiles.GetLaunchSettings(profileId);

            var started = _strategy.Start(launcherPath, discovery, gameBinary, modPath, logFile, launchSettings);

            if (!started)
            {
                return ErrorResult($"Failed to start the Relay launcher at '{launcherPath}'.");
            }

            _logger.LogInformation("Launched profile {Id} via the {Platform} path.", profileId, _strategy.Name);
            return new LaunchResult(LaunchStatus.Launched, Message: null, MissingDiscoveryFields: Array.Empty<string>());
        }
        catch (KeyNotFoundException ex)
        {
            // Unknown profile (PrepareModRoot): surfaced as Error, not thrown.
            _logger.LogError(ex, "Launch failed: profile {Id} not found.", profileId);
            return ErrorResult($"Profile '{profileId}' was not found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Launch failed for profile {Id}: I/O error.", profileId);
            return ErrorResult($"Launch failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Catch-all: the façade's contract is to always return a result
            // rather than push failure handling onto the caller.
            _logger.LogError(ex, "Launch failed for profile {Id}: unexpected error.", profileId);
            return ErrorResult($"Launch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the Relay launcher path with a deliberate precedence:
    /// <list type="number">
    /// <item><term>Configured <c>configRelayDir</c> first.</term>
    /// <description>If the launcher exists there, use it. This honors an
    /// explicit user override and the data-root default once Relay is deployed
    /// there (the standalone Linux layout, and the Windows dev/data layout).
    /// Honored on every OS.</description></item>
    /// <item><term>App-local fallback (both OSes).</term>
    /// <description>If the configured dir's launcher is missing, look under
    /// <c>&lt;baseDirectory&gt;/relay/</c>. A Velopack-packaged build ships
    /// Relay there (app-local inside the Windows install, or inside the Linux
    /// AppImage mount); the payload is replaced in place on update, so the path
    /// is stable. On Linux this is the AppImage case; the standalone layout
    /// keeps Relay at the data root and never reaches this fallback.</description></item>
    /// <item><term>Sibling-folder fallback (Windows only).</term>
    /// <description>If the app-local launcher is also missing and
    /// <paramref name="isWindows"/> is true, look under
    /// <c>&lt;baseDirectory&gt;/../relay/</c>. The portable Windows archive ships
    /// Curator under <c>&lt;root&gt;/app/</c> and Relay under
    /// <c>&lt;root&gt;/relay/</c> (a sibling of the app folder, mirroring the
    /// standalone Linux layout), so Relay resolves without a config override.
    /// Linux does NOT use this fallback: the AppImage payload mounts flat (no
    /// <c>app/</c> wrapper), so the sibling layout does not apply.</description></item>
    /// <item><term>Otherwise <c>null</c>.</term>
    /// <description>The caller reports not-found against the configured
    /// path.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Factored as a pure function of its inputs so the precedence is
    /// unit-testable on any CI OS. The production call passes
    /// <see cref="AppContext.BaseDirectory"/> and
    /// <see cref="OperatingSystem.IsWindows"/>.
    /// </remarks>
    internal static string? ResolveLauncherPath(string configRelayDir, string baseDirectory, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(configRelayDir);
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var configLauncher = Path.Combine(configRelayDir, LauncherExecutableName);
        if (File.Exists(configLauncher))
        {
            return configLauncher;
        }

        // App-local fallback (both OSes): <base>/relay/. A Velopack-packaged
        // build ships Relay here. On Linux this is the AppImage mount; on
        // Windows this is the in-place install. The standalone Linux layout
        // keeps Relay at the data root (config.RelayDir) and never reaches here.
        var appLocalLauncher = Path.Combine(baseDirectory, AppLocalRelayFolderName, LauncherExecutableName);
        if (File.Exists(appLocalLauncher))
        {
            return appLocalLauncher;
        }

        if (isWindows)
        {
            // Portable Windows archive: Curator lives under <root>/app/ and
            // Relay under <root>/relay/ -- a sibling of the app folder. Resolve
            // one level up from the app dir, then normalize so the returned
            // path carries no ".." segment.
            var siblingLauncher = Path.GetFullPath(Path.Combine(
                baseDirectory, "..", AppLocalRelayFolderName, LauncherExecutableName));
            if (File.Exists(siblingLauncher))
            {
                return siblingLauncher;
            }
        }

        return null;
    }

    private static LaunchResult ErrorResult(string message) =>
        new(LaunchStatus.Error, Message: message, MissingDiscoveryFields: Array.Empty<string>());
}
