using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam;

/// <summary>
/// Linux <see cref="ISteamDiscoverer"/>. Resolves the Steam install (native
/// default, then Flatpak), derives the Darktide install, Proton prefix
/// (compatdata), and Proton version. All platform-specific steps live here; the
/// shared mechanics (root resolution, library reading, Darktide probing) come
/// from <see cref="SteamDiscoveryCore"/>, and Proton resolution is delegated to
/// <see cref="ProtonResolver"/>. Selected at DI registration when
/// <see cref="SteamDiscoveryOptions.Platform"/> is <see cref="DiscoveryPlatform.Linux"/>.
/// </summary>
internal sealed class LinuxSteamDiscoverer : ISteamDiscoverer
{
    private readonly SteamDiscoveryCore _core;
    private readonly SteamDiscoveryOptions _options;
    private readonly ProtonResolver _protonResolver;
    private readonly ILogger<LinuxSteamDiscoverer> _logger;

    public LinuxSteamDiscoverer(
        SteamDiscoveryCore core,
        SteamDiscoveryOptions options,
        ILogger<LinuxSteamDiscoverer> logger)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _protonResolver = new ProtonResolver(options, logger);
    }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        var warnings = new List<string>();

        // Ordered candidates: native default first, then Flatpak. The first one
        // whose libraryfolders.vdf exists wins; Flatpak is flagged for a warning.
        var resolved = _core.ResolveRoot(
            new SteamDiscoveryCore.RootCandidate(_options.LinuxDefaultSteamRoot, IsFlatpak: false, FromRegistry: false),
            new SteamDiscoveryCore.RootCandidate(_options.LinuxFlatpakSteamRoot, IsFlatpak: true, FromRegistry: false));

        if (resolved.Path is null)
        {
            _logger.LogWarning("Steam install not found (no candidate carried a valid libraryfolders.vdf).");
            return SteamDiscoveryCore.Failed(warnings);
        }

        if (resolved.IsFlatpak)
        {
            warnings.Add("Flatpak Steam detected; some Steam integrations may be limited.");
        }

        var libraries = _core.ReadLibraries(resolved.Path, warnings);
        var darktide = _core.FindDarktide(libraries);
        var compatdata = FindCompatdata(resolved.Path, libraries);
        var proton = _protonResolver.Resolve(resolved.Path, libraries, warnings);

        var status = SteamDiscoveryCore.ComputeStatus(
            _options.Platform, resolved.Path, darktide, compatdata, proton?.Path);
        _logger.LogInformation(
            "Linux discovery: {Status} (steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
            status, resolved.Path, darktide ?? "(missing)", compatdata ?? "(missing)", proton?.Path ?? "(missing)");

        return new DiscoveryResult(
            SteamInstallPath: resolved.Path,
            DarktideGameBinaryPath: darktide,
            CompatdataPath: compatdata,
            ProtonBinaryPath: proton?.Path,
            ProtonVersion: proton?.Version,
            Status: status,
            Warnings: warnings);
    }

    /// <summary>
    /// Resolves the Darktide compatdata (Proton prefix) for the configured app
    /// id. Probes the main Steam install first, then each library declared in
    /// <c>libraryfolders.vdf</c> (in order); the first existing dir wins.
    /// </summary>
    /// <remarks>
    /// The prefix is created on whichever drive Steam chose at install time, so
    /// it frequently lives under a Steam *library* rather than the main install
    /// (e.g. <c>/games/steamapps/compatdata/&lt;appid&gt;/</c>). Probing the
    /// main install first preserves prior behavior, and the library scan is
    /// deterministic (VDF order).
    /// </remarks>
    private string? FindCompatdata(string steamRoot, IReadOnlyList<string> libraries)
    {
        var appId = _options.DarktideAppId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Main install first, then each library in VDF order; the main install
        // is yielded explicitly so it is probed first even if the VDF lists it
        // later (or omits it); the explicit duplicate is skipped below.
        foreach (var root in CompatdataCandidateRoots(steamRoot, libraries))
        {
            var dir = Path.Combine(root, "steamapps", "compatdata", appId);
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> CompatdataCandidateRoots(string steamRoot, IReadOnlyList<string> libraries)
    {
        yield return steamRoot;
        foreach (var lib in libraries)
        {
            // Skip the main install when the VDF lists it: yielded first above.
            if (!string.Equals(lib, steamRoot, StringComparison.Ordinal))
            {
                yield return lib;
            }
        }
    }
}
