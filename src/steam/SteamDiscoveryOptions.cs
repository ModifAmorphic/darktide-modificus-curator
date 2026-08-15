namespace Modificus.Curator.Steam;

/// <summary>
/// OS-specific inputs to Steam discovery. Carries the candidate Steam install
/// roots + auxiliary paths so the discoverer never hardcodes
/// <c>~/.local/share/Steam</c>; production wires the real OS defaults via
/// <see cref="CreateDefault"/>, tests inject fixture paths. <see cref="Platform"/>
/// controls which fields are consulted and whether Proton/compatdata are
/// discovered (Linux only).
/// </summary>
/// <remarks>
/// This is the testability seam mandated by the architecture: by injecting the
/// search roots + platform, the full discovery pipeline can be exercised
/// against synthetic layouts in temp dirs on any OS.
/// </remarks>
public sealed class SteamDiscoveryOptions
{
    /// <summary>The platform to discover for. Production: detected at runtime.</summary>
    public DiscoveryPlatform Platform { get; set; } = DetectPlatform();

    /// <summary>
    /// The default native Linux Steam root (typically
    /// <c>~/.local/share/Steam</c>). Probed first on Linux.
    /// </summary>
    public string? LinuxDefaultSteamRoot { get; set; }

    /// <summary>
    /// The Flatpak Linux Steam root (typically
    /// <c>~/.var/app/com.valvesoftware.Steam/data/Steam</c>). Probed as a Linux
    /// fallback; resolving here raises a Flatpak warning.
    /// </summary>
    public string? LinuxFlatpakSteamRoot { get; set; }

    /// <summary>
    /// The primary user compatibility-tool root (typically
    /// <c>~/.local/share/Steam/compatibilitytools.d</c>), searched for custom
    /// Proton builds. Probed after the resolved Steam root's own
    /// <c>compatibilitytools.d</c>. Linux only. Overridable for tests.
    /// </summary>
    public string? LinuxCompatibilityToolsDir { get; set; }

    /// <summary>
    /// System-wide compatibility-tool roots searched last for custom Proton
    /// builds. Defaults to the two standard Steam system directories. Linux only.
    /// Overridable for tests so they can point at fixture paths instead of host
    /// state.
    /// </summary>
    public IList<string> LinuxSystemCompatibilityToolsDirs { get; set; } = new List<string>
    {
        "/usr/share/steam/compatibilitytools.d",
        "/usr/local/share/steam/compatibilitytools.d",
    };

    /// <summary>
    /// Whether the host is a Steam Deck. <see cref="CreateDefault"/> detects it
    /// from host OS-release metadata (<c>ID=steamos</c> +
    /// <c>VARIANT_ID=steamdeck</c>); callers constructing non-default options
    /// may set or pin the value.
    /// </summary>
    public bool IsSteamDeck { get; set; }

    /// <summary>
    /// The Windows Steam install fallback path used when the registry yields
    /// nothing (typically <c>C:\Program Files (x86)\Steam</c>). Windows only.
    /// </summary>
    public string? WindowsDefaultSteamRoot { get; set; }

    /// <summary>Steam's Darktide app id. Constant; overridable for tests.</summary>
    public int DarktideAppId { get; set; } = 1361210;

    /// <summary>
    /// Darktide's directory name under <c>steamapps/common/</c>. Overridable for
    /// tests so fixtures can use a short name.
    /// </summary>
    public string DarktideCommonDir { get; set; } = "Warhammer 40,000 DARKTIDE";

    /// <summary>
    /// The game binary name under <c>&lt;common&gt;/binaries/</c>. Overridable
    /// for tests.
    /// </summary>
    public string GameBinaryName { get; set; } = "Darktide.exe";

    /// <summary>
    /// The process-name stem used by <see cref="ISteamService.IsGameRunning"/>.
    /// Matched against process comm on Windows and the
    /// <c>/proc/&lt;pid&gt;/cmdline</c> <c>argv[0]</c> stem on Linux (the kernel
    /// <c>comm</c> is unreliable under Proton). Overridable for tests.
    /// </summary>
    public string GameProcessName { get; set; } = "Darktide";

    /// <summary>
    /// Builds the default options for the current OS. Resolves the real user
    /// profile + standard Steam locations; picks <see cref="Platform"/> from the
    /// runtime OS and <see cref="IsSteamDeck"/> from the OS release metadata.
    /// </summary>
    public static SteamDiscoveryOptions CreateDefault() => new()
    {
        Platform = DetectPlatform(),
        IsSteamDeck = SteamDeckDetector.IsSteamDeck(),
        LinuxDefaultSteamRoot = HomeSubpath(".local/share/Steam"),
        LinuxFlatpakSteamRoot = HomeSubpath(".var/app/com.valvesoftware.Steam/data/Steam"),
        LinuxCompatibilityToolsDir = HomeSubpath(".local/share/Steam/compatibilitytools.d"),
        WindowsDefaultSteamRoot = @"C:\Program Files (x86)\Steam",
    };

    private static DiscoveryPlatform DetectPlatform() =>
        // Darktide ships on Windows + Linux only; treat anything else as Windows
        // (the only other realistic host) so discovery is well-defined.
        OperatingSystem.IsLinux() ? DiscoveryPlatform.Linux : DiscoveryPlatform.Windows;

    private static string? HomeSubpath(string subpath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.None);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, subpath);
    }
}
