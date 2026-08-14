namespace Modificus.Curator.Config;

/// <summary>
/// The Steam/Darktide discovery snapshot + mode. Bound from the
/// <c>Discovery</c> section of <see cref="CuratorConfig"/> by the config loader
/// in <c>Modificus.Curator.General</c>. <see cref="General.IConfigLoader"/>
/// persists it, and <see cref="Steam.ISteamService"/> reads it live (one
/// <c>Load()</c> per call) so a Settings write is visible on the next discovery
/// pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mode:</b> <see cref="OverrideAutomaticDiscovery"/> selects between
/// automatic and manual discovery. <c>false</c> (the default) is automatic:
/// every <see cref="Steam.ISteamService.Discover"/> call runs the platform
/// discoverer and atomically replaces the active-platform fields below with that
/// result (including nulls that clear stale values). <c>true</c> is manual: the
/// discoverer is not invoked, the stored paths are validated on disk as-is, and
/// invalid/missing values surface as null result fields without rewriting the
/// stored input. <see cref="Steam.ISteamService.Rediscover"/> forces one
/// automatic pass regardless of the mode and leaves the mode unchanged.</para>
/// <para>
/// <b>Platform ownership:</b> the Steam root + Darktide binary are
/// active on every platform; CompatdataPath + ProtonBinaryPath are Linux-only.
/// An automatic pass on Windows writes only the two Windows fields and leaves
/// the Linux-only fields untouched (and vice versa). A manual pass validates
/// only the active platform's fields.</para>
/// </remarks>
public sealed class DiscoveryConfig
{
    /// <summary>
    /// When <c>true</c>, <see cref="Steam.ISteamService.Discover"/> skips the
    /// platform discoverer and validates the stored paths below as static manual
    /// values. When <c>false</c> (the default), every <see cref="Steam.ISteamService.Discover"/>
    /// call runs full platform discovery and replaces the active-platform fields.
    /// </summary>
    public bool OverrideAutomaticDiscovery { get; set; }

    /// <summary>
    /// The Steam client install directory (the value for
    /// <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>). In automatic mode this holds the
    /// discoverer's snapshot; in manual mode it holds the user's static value.
    /// </summary>
    public string? SteamInstallPath { get; set; }

    /// <summary>
    /// The native path to <c>Darktide.exe</c>. In automatic mode this holds the
    /// discoverer's snapshot; in manual mode it holds the user's static value.
    /// </summary>
    public string? DarktideGameBinaryPath { get; set; }

    /// <summary>
    /// The Wine prefix (compatdata) directory (the value for
    /// <c>STEAM_COMPAT_DATA_PATH</c>). Linux only; never active on Windows.
    /// </summary>
    public string? CompatdataPath { get; set; }

    /// <summary>
    /// The <c>proton</c> script path used for <c>proton run</c>. Linux only;
    /// never active on Windows.
    /// </summary>
    public string? ProtonBinaryPath { get; set; }
}
