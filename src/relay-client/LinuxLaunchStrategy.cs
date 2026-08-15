using System.Collections.Immutable;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Linux <see cref="IPlatformLaunchStrategy"/>. Curator runs natively (not
/// Proton-wrapped); <c>mod_relay.exe</c> is a Windows binary, so this
/// invokes it under <c>&lt;proton&gt; run</c> using Darktide's own compatdata as
/// the Wine prefix, sets both <c>STEAM_COMPAT_*</c> env vars, and
/// <c>Z:\</c>-translates the launcher's path-valued flags. Selected at DI
/// registration when the host is Linux.
/// </summary>
/// <remarks>
/// <para>
/// When Curator itself is launched from its installed AppImage, the AppImage
/// runtime exports a handful of variables into Curator's environment
/// (<c>APPDIR</c>, <c>APPIMAGE</c>, <c>ARGV0</c>, <c>OWD</c>, plus the desktop
/// hint <c>BAMF_DESKTOP_FILE_HINT</c>). KDE Plasma's task manager reads
/// <c>BAMF_DESKTOP_FILE_HINT</c> and then <c>APPDIR</c> from
/// <c>/proc/&lt;pid&gt;/environ</c> to resolve a child's desktop identity, so
/// if those leak through <c>proton run</c> into Relay and Darktide, the game
/// window is grouped under Curator's launcher.</para>
/// <para>
/// To stop that, this strategy requests the launcher strip those five keys from
/// the inherited environment. Only the AppImage-identity variables are removed;
/// every unrelated inherited variable, the two required Steam compat vars
/// (overridden below), and the desktop-activation tokens (<c>DESKTOP_STARTUP_ID</c>,
/// <c>XDG_ACTIVATION_TOKEN</c>, <c>GIO_LAUNCHED_DESKTOP_FILE</c>) pass through
/// unchanged.</para>
/// </remarks>
internal sealed class LinuxLaunchStrategy : IPlatformLaunchStrategy
{
    /// <summary>
    /// The exact set of inherited environment variables Curator asks the
    /// launcher to strip before invoking <c>proton run</c>: the four AppImage
    /// runtime variables (<c>APPDIR</c>, <c>APPIMAGE</c>, <c>ARGV0</c>,
    /// <c>OWD</c>) plus <c>BAMF_DESKTOP_FILE_HINT</c> (the desktop-file hint a
    /// parent passes its children). Removing these stops KDE Plasma's task
    /// manager from resolving Curator's desktop identity for Darktide, while
    /// leaving every unrelated inherited variable intact.
    /// </summary>
    internal static readonly ImmutableArray<string> AppImageIdentityVariables =
        ImmutableArray.Create(new[]
        {
            "APPDIR",
            "APPIMAGE",
            "ARGV0",
            "OWD",
            "BAMF_DESKTOP_FILE_HINT",
        });

    private readonly IProcessLauncher _launcher;
    private readonly ILogger<LinuxLaunchStrategy> _logger;

    public LinuxLaunchStrategy(IProcessLauncher launcher, ILogger<LinuxLaunchStrategy> logger)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Linux";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery)
    {
        var missing = new List<string>();

        // Linux needs Steam + the game binary AND the Wine prefix (compatdata) + Proton.
        if (discovery.SteamInstallPath is null) missing.Add(nameof(DiscoveryResult.SteamInstallPath));
        if (discovery.DarktideGameBinaryPath is null) missing.Add(nameof(DiscoveryResult.DarktideGameBinaryPath));
        if (discovery.CompatdataPath is null) missing.Add(nameof(DiscoveryResult.CompatdataPath));
        if (discovery.ProtonBinaryPath is null) missing.Add(nameof(DiscoveryResult.ProtonBinaryPath));

        return missing;
    }

    /// <inheritdoc />
    public ISpawnedProcess? Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile, LaunchSettings launchSettings, bool createNoWindow)
    {
        ArgumentNullException.ThrowIfNull(launchSettings);

        // The launcher's OWN args (--game-binary, --mod-path, --log-file) are
        // Windows paths (the launcher runs under Wine); the proton command + the
        // launcher.exe path are native Linux (Proton resolves the .exe from a
        // native path). Game args append a bare -- then one argv entry each
        // (Relay's -- contract; empty game args emit no --).
        var launcherArgs = BuildLauncherArgs(gameBinary, modPath, logFile, launchSettings);

        var arguments = new List<string>(capacity: launcherArgs.Count + 2)
        {
            "run",          // proton's "run this Windows binary" subcommand
            launcherPath,   // native Linux path -- Proton resolves it
        };
        arguments.AddRange(launcherArgs);

        // Env merge, layered so Curator-owned wins (item-1 invariants + the
        // profile launch-settings brief):
        //   1. inherited Curator environment (the request's implicit base,
        //      snapshotted lazily by ProcessLauncher);
        //   2. AppImage identity removals (EnvironmentVariablesToRemove);
        //   3. profile env values (EnvironmentOverrides);
        //   4. Curator-owned STEAM_COMPAT_* layered AFTER profile values so they
        //      win even though validation already blocks reserved names (defense
        //      in depth). The dictionary is ordinal to match ProcessLaunchRequest.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in launchSettings.EnvironmentVariables)
        {
            env[entry.Name] = entry.Value;
        }
        // Both Steam compat vars are required for Proton to use the right Wine
        // prefix + find the Steam client; RequiredDiscoveryFields guaranteed both
        // non-null above.
        env["STEAM_COMPAT_DATA_PATH"] = discovery.CompatdataPath!;
        env["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = discovery.SteamInstallPath!;

        _logger.LogInformation(
            "Launching (Linux) {Proton} run {Launcher} {Args}",
            discovery.ProtonBinaryPath, launcherPath, FormatArgs(arguments));

        var request = new ProcessLaunchRequest(
            discovery.ProtonBinaryPath!,
            arguments,
            environmentOverrides: env,
            environmentVariablesToRemove: AppImageIdentityVariables,
            createNoWindow: createNoWindow);
        return _launcher.Start(request);
    }

    /// <summary>
    /// Builds the launcher's own argument list (the flags AFTER
    /// <c>... proton run launcher.exe</c>). The path-valued flags are converted
    /// to Wine <c>Z:\</c> form so the launcher-under-Wine can resolve them. A
    /// bare <c>--log-append</c> is emitted unconditionally right after
    /// <c>--log-file</c>'s value (Relay writes a per-day file shared across
    /// launches, so it must append rather than overwrite on each launch); it is
    /// a bare flag, NOT path-valued, so it is not <c>Z:\</c>-translated. When
    /// <see cref="LaunchSettings.EnableLuaLogs"/> is true, appends a bare
    /// <c>--log-lua</c> flag after <c>--log-append</c> (a Relay-owned logging
    /// flag with no value, NOT path-valued, so it is not <c>Z:\</c>-translated),
    /// teeing Lua <c>print</c> output into the log file. When
    /// <see cref="LaunchSettings.SkipSplash"/> is true, appends a bare
    /// <c>--skip-splash</c> flag after <c>--log-lua</c> (a Relay-owned flag
    /// with no value, NOT path-valued, so it is not <c>Z:\</c>-translated),
    /// skipping Darktide's intro splash state. When
    /// <see cref="LaunchSettings.GameArguments"/> is non-empty, appends a single
    /// bare <c>--</c> separator then each game arg as its own argv entry
    /// (Relay's <c>--</c> contract); empty game args emit no <c>--</c> (legacy
    /// launch).
    /// </summary>
    /// <remarks>
    /// <c>--log-file</c> is a path the launcher-under-Wine opens, so it must be
    /// <c>Z:\</c>-translated too (otherwise the Relay shell log can't be
    /// written where Curator expects). <c>--log-level</c> is intentionally NOT
    /// emitted (the shell's level vocabulary differs from Serilog's). Game args
    /// are NOT <c>Z:\</c>-translated: they are Darktide's own args, opaque to
    /// Curator, forwarded verbatim; any path-like arg is the game's concern.
    /// </remarks>
    internal static List<string> BuildLauncherArgs(string gameBinary, string modPath, string logFile, LaunchSettings launchSettings)
    {
        ArgumentNullException.ThrowIfNull(launchSettings);

        var args = new List<string>
        {
            "--game-binary", WinePath.ToWine(gameBinary),
            "--mod-path", WinePath.ToWine(modPath),
            "--log-file", WinePath.ToWine(logFile),
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
        return AppendGameArguments(args, launchSettings.GameArguments);
    }

    /// <summary>
    /// Appends the profile's game arguments to <paramref name="args"/> per
    /// Relay's bare-<c>--</c> contract: when non-empty, one <c>--</c> element
    /// then each game arg as its own element (verbatim, in order). When empty
    /// (or null), no <c>--</c> is emitted (legacy launch). Shared by the Linux
    /// + Windows <c>BuildLauncherArgs</c> so the two paths cannot drift.
    /// </summary>
    internal static List<string> AppendGameArguments(List<string> args, IReadOnlyList<string>? gameArguments)
    {
        if (gameArguments is null || gameArguments.Count == 0)
        {
            return args;
        }

        args.Add("--");
        foreach (var arg in gameArguments)
        {
            // A null entry would corrupt the argv layout; coerce to "" so the
            // element count matches the profile list length (defense in depth;
            // LaunchSettings stores non-null entries).
            args.Add(arg ?? string.Empty);
        }
        return args;
    }

    private static string FormatArgs(IReadOnlyList<string> args) => string.Join(' ', args);
}
