using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam;

/// <summary>
/// <see cref="ISteamService"/> implementation. Holds the discovery-mode policy
/// (automatic vs. manual + the forced <see cref="Rediscover"/> path) and
/// delegates platform-specific discovery to <see cref="ISteamDiscoverer"/> and
/// the game-running check to <see cref="IProcessLookup"/>. Contains no platform
/// dispatch; every OS-specific concern lives behind a polymorphic collaborator
/// wired at the composition root.
/// </summary>
/// <remarks>
/// Registered as a singleton. <see cref="Discover"/> and <see cref="Rediscover"/>
/// never throw on missing pieces; those are reported via
/// <see cref="DiscoveryResult.Status"/> + the nullable fields.
/// </remarks>
internal sealed class SteamService : ISteamService
{
    private readonly ISteamDiscoverer _discoverer;
    private readonly SteamDiscoveryOptions _options;
    private readonly IProcessLookup _processes;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<SteamService> _logger;

    public SteamService(
        ISteamDiscoverer discoverer,
        SteamDiscoveryOptions options,
        IProcessLookup processes,
        IConfigLoader configLoader,
        ILogger<SteamService> logger)
    {
        _discoverer = discoverer ?? throw new ArgumentNullException(nameof(discoverer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        var discovery = _configLoader.Load().Discovery;
        return discovery.OverrideAutomaticDiscovery
            ? DiscoverManual(discovery)
            : DiscoverAutomatic();
    }

    /// <inheritdoc />
    public DiscoveryResult Rediscover() => DiscoverAutomatic();

    /// <summary>
    /// Automatic mode: run the platform discoverer, atomically replace the
    /// active-platform path snapshot in config (including nulls that clear stale
    /// values), and return the result. The mode bool + inactive-platform fields
    /// (Linux-only fields on Windows) are preserved. Skips the write when the
    /// snapshot is already current.
    /// </summary>
    private DiscoveryResult DiscoverAutomatic()
    {
        var auto = _discoverer.Discover();
        var platform = _options.Platform;

        // Read-modify-save: start from the current config so the mode bool and
        // the inactive-platform fields survive untouched, then overwrite only
        // the active-platform fields with the discoverer's snapshot.
        var fresh = _configLoader.Load();
        var d = fresh.Discovery;

        var changed = !StringEquals(d.SteamInstallPath, auto.SteamInstallPath);
        d.SteamInstallPath = auto.SteamInstallPath;

        changed |= !StringEquals(d.DarktideGameBinaryPath, auto.DarktideGameBinaryPath);
        d.DarktideGameBinaryPath = auto.DarktideGameBinaryPath;

        // Linux owns all four fields; Windows is native and never touches the
        // Linux-only compatdata + Proton fields.
        if (platform == DiscoveryPlatform.Linux)
        {
            changed |= !StringEquals(d.CompatdataPath, auto.CompatdataPath);
            d.CompatdataPath = auto.CompatdataPath;

            changed |= !StringEquals(d.ProtonBinaryPath, auto.ProtonBinaryPath);
            d.ProtonBinaryPath = auto.ProtonBinaryPath;
        }

        if (changed)
        {
            _configLoader.Save(fresh);
            _logger.LogInformation(
                "Automatic discovery snapshot persisted (steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
                auto.SteamInstallPath ?? "(missing)",
                auto.DarktideGameBinaryPath ?? "(missing)",
                platform == DiscoveryPlatform.Linux ? (auto.CompatdataPath ?? "(missing)") : "n/a",
                platform == DiscoveryPlatform.Linux ? (auto.ProtonBinaryPath ?? "(missing)") : "n/a");
        }

        LogIfIncomplete(auto);
        return auto;
    }

    /// <summary>
    /// Manual mode: validate each stored active-platform path by kind (directory
    /// for Steam + compatdata; file for Darktide + Proton). Valid paths pass
    /// through; invalid/missing ones surface as null fields. The stored input is
    /// never rewritten. ProtonVersion is null (no discoverer label is available).
    /// </summary>
    private DiscoveryResult DiscoverManual(DiscoveryConfig discovery)
    {
        var platform = _options.Platform;

        var steam = IsValidPath(discovery.SteamInstallPath, isDirectory: true)
            ? discovery.SteamInstallPath : null;
        var darktide = IsValidPath(discovery.DarktideGameBinaryPath, isDirectory: false)
            ? discovery.DarktideGameBinaryPath : null;

        string? compatdata = null;
        string? proton = null;
        if (platform == DiscoveryPlatform.Linux)
        {
            compatdata = IsValidPath(discovery.CompatdataPath, isDirectory: true)
                ? discovery.CompatdataPath : null;
            proton = IsValidPath(discovery.ProtonBinaryPath, isDirectory: false)
                ? discovery.ProtonBinaryPath : null;
        }

        var status = SteamDiscoveryCore.ComputeStatus(platform, steam, darktide, compatdata, proton);
        var result = new DiscoveryResult(
            steam, darktide, compatdata, proton,
            ProtonVersion: null, status, Warnings: Array.Empty<string>());

        _logger.LogInformation(
            "Manual discovery: {Status} (steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
            status,
            steam ?? "(missing)",
            darktide ?? "(missing)",
            platform == DiscoveryPlatform.Linux ? (compatdata ?? "(missing)") : "n/a",
            platform == DiscoveryPlatform.Linux ? (proton ?? "(missing)") : "n/a");

        return result;
    }

    /// <inheritdoc />
    public bool IsGameRunning() => _processes.IsRunning(_options.GameProcessName);

    /// <summary>
    /// Whether a path is usable of the given kind: non-null/non-whitespace AND
    /// exists on disk as a directory (when <paramref name="isDirectory"/> is
    /// <c>true</c>) or a file (otherwise).
    /// </summary>
    private static bool IsValidPath(string? path, bool isDirectory) =>
        !string.IsNullOrWhiteSpace(path)
        && (isDirectory ? Directory.Exists(path) : File.Exists(path));

    private void LogIfIncomplete(DiscoveryResult result)
    {
        if (result.Status != DiscoveryStatus.Complete)
        {
            _logger.LogWarning(
                "Discovery is {Status}: steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}.",
                result.Status,
                result.SteamInstallPath ?? "(missing)",
                result.DarktideGameBinaryPath ?? "(missing)",
                result.CompatdataPath ?? "(missing)",
                result.ProtonBinaryPath ?? "(missing)");
        }
    }

    private static bool StringEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal);
}
