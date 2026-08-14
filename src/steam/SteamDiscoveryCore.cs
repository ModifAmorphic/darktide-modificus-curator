using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam;

/// <summary>
/// The platform-agnostic mechanics of Steam discovery: candidate-root
/// resolution, <c>libraryfolders.vdf</c> reading, Darktide probing, and the
/// all-null failure result. Shared by <see cref="LinuxSteamDiscoverer"/> and
/// <see cref="WindowsSteamDiscoverer"/> via composition -- each discoverer
/// injects this and layers its own platform-specific steps (Linux: compatdata +
/// Proton; Windows: registry). This is composition, not inheritance.
/// </summary>
internal sealed class SteamDiscoveryCore
{
    private readonly SteamDiscoveryOptions _options;
    private readonly ILogger<SteamDiscoveryCore> _logger;

    public SteamDiscoveryCore(SteamDiscoveryOptions options, ILogger<SteamDiscoveryCore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Picks the first candidate whose path is non-empty and carries a valid
    /// <c>libraryfolders.vdf</c>; returns a null-path <see cref="ResolvedRoot"/>
    /// when none qualifies.
    /// </summary>
    public ResolvedRoot ResolveRoot(params RootCandidate[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && SteamRootIsValid(candidate.Path))
            {
                return new ResolvedRoot(candidate.Path, candidate.IsFlatpak, candidate.FromRegistry);
            }
        }
        return new ResolvedRoot(Path: null, IsFlatpak: false, FromRegistry: false);
    }

    /// <summary>
    /// Reads + parses <c>steamapps/libraryfolders.vdf</c> under
    /// <paramref name="steamRoot"/>; always includes the Steam root itself as a
    /// fallback library (it's normally listed as library "0"). IO/permission
    /// failures degrade to a root-only search + a warning.
    /// </summary>
    public IReadOnlyList<string> ReadLibraries(string steamRoot, List<string> warnings)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            var content = File.ReadAllText(vdf);
            var libs = LibraryFoldersVdf.Parse(content);

            // The Steam install root is always a usable library even if the VDF
            // omits it (it normally lists itself as library "0"); ensure it's
            // probed so a missing/malformed VDF doesn't hide a locally-installed
            // Darktide. De-dup against what the VDF already provided.
            if (!libs.Any(l => string.Equals(l, steamRoot, StringComparison.Ordinal)))
            {
                libs = libs.Append(steamRoot).ToList();
            }

            if (libs.Count > 1)
            {
                warnings.Add($"Searched {libs.Count} Steam libraries for Darktide.");
            }

            return libs;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {Vdf}; falling back to Steam root only.", vdf);
            warnings.Add("Could not read libraryfolders.vdf; searched Steam root only.");
            return new[] { steamRoot };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied reading {Vdf}; falling back to Steam root only.", vdf);
            warnings.Add("Permission denied reading libraryfolders.vdf; searched Steam root only.");
            return new[] { steamRoot };
        }
    }

    /// <summary>
    /// Probes <c>&lt;lib&gt;/steamapps/common/&lt;DarktideCommonDir&gt;/binaries/&lt;GameBinaryName&gt;</c>
    /// across every library; first hit wins. Returns null if Darktide is not
    /// found under any library.
    /// </summary>
    public string? FindDarktide(IReadOnlyList<string> libraries)
    {
        foreach (var lib in libraries)
        {
            var exe = Path.Combine(
                lib, "steamapps", "common",
                _options.DarktideCommonDir, "binaries", _options.GameBinaryName);

            if (File.Exists(exe))
            {
                return exe;
            }
        }

        _logger.LogInformation("Darktide not found under any Steam library.");
        return null;
    }

    /// <summary>A <see cref="DiscoveryStatus.Failed"/> result with all paths null.</summary>
    public static DiscoveryResult Failed(IReadOnlyList<string> warnings) =>
        new(
            SteamInstallPath: null,
            DarktideGameBinaryPath: null,
            CompatdataPath: null,
            ProtonBinaryPath: null,
            ProtonVersion: null,
            Status: DiscoveryStatus.Failed,
            Warnings: warnings);

    /// <summary>
    /// Computes the discovery status from the four nullable path fields for the
    /// given platform. This is the single source of truth for "what counts as
    /// Complete", shared by the discoverers (building their result) and the
    /// manual-mode path in <see cref="SteamService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Linux</b> requires all four fields (Steam + Darktide + compatdata +
    /// Proton): Steam missing is <see cref="DiscoveryStatus.Failed"/>, and any
    /// other required field missing with Steam present is
    /// <see cref="DiscoveryStatus.Partial"/>.</para>
    /// <para>
    /// <b>Windows</b> requires only the Darktide binary. Steam is the
    /// automatic-discovery anchor that locates Darktide, not a launch input
    /// (no <c>STEAM_COMPAT_*</c> env is set on Windows), so a resolved Darktide
    /// path is <see cref="DiscoveryStatus.Complete"/> with or without Steam.
    /// Without Darktide, no Steam resolves <see cref="DiscoveryStatus.Failed"/>
    /// while a present Steam with no game resolves
    /// <see cref="DiscoveryStatus.Partial"/>.</para>
    /// </remarks>
    /// <param name="platform">The platform whose completeness rule applies.</param>
    /// <param name="steamInstallPath">Resolved Steam client dir (null = not found).</param>
    /// <param name="darktideGameBinaryPath">Resolved native Darktide binary path (null = not found).</param>
    /// <param name="compatdataPath">Resolved Wine prefix (Linux only; null on Windows by design).</param>
    /// <param name="protonBinaryPath">Resolved <c>proton</c> script path (Linux only; null on Windows by design).</param>
    public static DiscoveryStatus ComputeStatus(
        DiscoveryPlatform platform,
        string? steamInstallPath,
        string? darktideGameBinaryPath,
        string? compatdataPath,
        string? protonBinaryPath)
    {
        if (platform == DiscoveryPlatform.Windows)
        {
            // Native Windows launch is driven by the game binary alone; Steam is
            // a discovery mechanism, not a launch input. Automatic discovery still
            // anchors on Steam (it can only find Darktide by walking Steam's
            // libraries), so an automatic pass never resolves a Darktide path
            // without one -- the rule below matters for manual mode.
            if (darktideGameBinaryPath is not null)
            {
                return DiscoveryStatus.Complete;
            }
            return steamInstallPath is null ? DiscoveryStatus.Failed : DiscoveryStatus.Partial;
        }

        // Linux requires all four (Steam + Darktide + compatdata + Proton).
        if (steamInstallPath is null)
        {
            return DiscoveryStatus.Failed;
        }

        if (darktideGameBinaryPath is null
            || compatdataPath is null
            || protonBinaryPath is null)
        {
            return DiscoveryStatus.Partial;
        }

        return DiscoveryStatus.Complete;
    }

    private static bool SteamRootIsValid(string root) =>
        Directory.Exists(root)
        && File.Exists(Path.Combine(root, "steamapps", "libraryfolders.vdf"));

    /// <summary>A candidate Steam install root probed by <see cref="ResolveRoot"/>.</summary>
    public sealed record RootCandidate(string? Path, bool IsFlatpak, bool FromRegistry);

    /// <summary>The Steam root <see cref="ResolveRoot"/> settled on (null-path when none qualified).</summary>
    public sealed record ResolvedRoot(string? Path, bool IsFlatpak, bool FromRegistry);
}
