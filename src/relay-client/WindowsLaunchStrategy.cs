using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Windows <see cref="IPlatformLaunchStrategy"/>. Invokes
/// <c>mod_relay.exe</c> directly -- no Proton, no path translation (native
/// Windows paths). Selected at DI registration when the host is Windows.
/// </summary>
internal sealed class WindowsLaunchStrategy : IPlatformLaunchStrategy
{
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<WindowsLaunchStrategy> _logger;

    public WindowsLaunchStrategy(IProcessLauncher launcher, ILogger<WindowsLaunchStrategy> logger)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Windows";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery)
    {
        var missing = new List<string>();

        // Windows needs Steam + the game binary (compatdata/Proton are unused -- native).
        if (discovery.SteamInstallPath is null) missing.Add(nameof(DiscoveryResult.SteamInstallPath));
        if (discovery.DarktideGameBinaryPath is null) missing.Add(nameof(DiscoveryResult.DarktideGameBinaryPath));

        return missing;
    }

    /// <inheritdoc />
    public bool Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile, LaunchSettings launchSettings, bool createNoWindow)
    {
        ArgumentNullException.ThrowIfNull(launchSettings);

        // Direct invocation -- no Proton, no path translation (native Windows paths).
        // `discovery` is unused on Windows: the caller already extracted gameBinary
        // from it, and Windows needs no Proton/compat context. Game args append a
        // bare -- then one argv entry each (Relay's -- contract; empty emits no --).
        var args = BuildLauncherArgs(gameBinary, modPath, logFile, launchSettings);

        // Profile env as overrides on the Relay process. No Steam-compat vars, no
        // AppImage removals (Windows never runs from an AppImage mount). Relay
        // creates Darktide with an inherited environment, so the values reach the
        // game. The dictionary is ordinal to match ProcessLaunchRequest.
        Dictionary<string, string>? env = null;
        if (launchSettings.EnvironmentVariables.Count > 0)
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in launchSettings.EnvironmentVariables)
            {
                env[entry.Name] = entry.Value;
            }
        }

        _logger.LogInformation("Launching (Windows) {Launcher} {Args}", launcherPath, FormatArgs(args));
        var request = new ProcessLaunchRequest(launcherPath, args, environmentOverrides: env, createNoWindow: createNoWindow);
        return _launcher.Start(request);
    }

    /// <summary>
    /// Builds the launcher's own argument list (the flags AFTER
    /// <c>mod_relay.exe</c>). Paths pass through verbatim -- Windows needs no
    /// <c>Z:\</c> translation. A bare <c>--log-append</c> is emitted
    /// unconditionally right after <c>--log-file</c> (Relay writes a per-day
    /// file shared across launches, so it must append rather than overwrite on
    /// each launch). When <see cref="LaunchSettings.EnableLuaLogs"/> is true,
    /// appends a bare <c>--log-lua</c> flag after <c>--log-append</c> (a
    /// Relay-owned logging flag with no value, not path-translated), teeing Lua
    /// <c>print</c> output into the log file. When
    /// <see cref="LaunchSettings.SkipSplash"/> is true, appends a bare
    /// <c>--skip-splash</c> flag after <c>--log-lua</c> (a Relay-owned flag
    /// with no value, not path-translated), skipping Darktide's intro splash
    /// state. When <see cref="LaunchSettings.GameArguments"/> is non-empty,
    /// appends a single bare <c>--</c> separator then each game arg as its own
    /// argv entry (Relay's <c>--</c> contract); empty game args emit no
    /// <c>--</c>.
    /// </summary>
    /// <remarks>
    /// <c>--log-level</c> is intentionally NOT emitted: <c>CuratorConfig.Logging.Level</c>
    /// is a Serilog level name for Curator's own log, but the Relay shell's level
    /// vocabulary differs, so forwarding the Serilog name silently mis-resolved levels.
    /// The two logs are decoupled; the launcher's <c>info</c> default is used.
    /// </remarks>
    internal static List<string> BuildLauncherArgs(string gameBinary, string modPath, string logFile, LaunchSettings launchSettings)
    {
        ArgumentNullException.ThrowIfNull(launchSettings);

        var args = new List<string>
        {
            "--game-binary", gameBinary,
            "--mod-path", modPath,
            "--log-file", logFile,
            "--log-append",
        };
        if (launchSettings.EnableLuaLogs)
        {
            args.Add("--log-lua");
        }
        if (launchSettings.SkipSplash)
        {
            args.Add("--skip-splash");
        }
        return LinuxLaunchStrategy.AppendGameArguments(args, launchSettings.GameArguments);
    }

    private static string FormatArgs(IReadOnlyList<string> args) => string.Join(' ', args);
}
