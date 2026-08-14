namespace Modificus.Curator.Config;

/// <summary>
/// The global Modificus Curator configuration: system-level settings shared
/// across all profiles. Per-profile settings live with the profile, not here.
/// Bound from JSON by the config loader in <c>Modificus.Curator.General</c>.
/// </summary>
/// <remarks>
/// Every field carries a sensible platform-appropriate default, so an absent
/// (or partially-populated) config file always yields a usable object.
/// </remarks>
public sealed class CuratorConfig
{
    /// <summary>Logging settings (level + file).</summary>
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Where profiles, per-profile mods, and profile settings are stored.</summary>
    public string ProfilesBaseFolder { get; set; } = AppPaths.DefaultProfilesBaseFolder;

    /// <summary>
    /// The unified mod repository root. One UUID container per (source, identity),
    /// holding opaque-ID version subfolders; see <c>docs/architecture/MODIFICUS-CURATOR.md</c>
    /// (Mod repository).
    /// </summary>
    public string ModsFolder { get; set; } = AppPaths.DefaultModsFolder;

    /// <summary>
    /// The Mod Relay directory: where <c>mod_relay.exe</c>,
    /// <c>relay_shell.dll</c>, and <c>mod_loader/</c> live.
    /// </summary>
    public string RelayDir { get; set; } = AppPaths.DefaultRelayDir;

    /// <summary>
    /// The Steam/Darktide discovery snapshot + mode. <c>SteamService.Discover()</c>
    /// reads this live per call (via <see cref="General.IConfigLoader"/>) and
    /// persists the automatic snapshot back here. See
    /// <see cref="DiscoveryConfig"/> for the mode + per-field semantics.
    /// </summary>
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>External-service (mod-source) integration settings.</summary>
    public IntegrationsConfig Integrations { get; set; } = new();

    /// <summary>App self-update settings (the in-app Curator update check).</summary>
    public AppUpdatesConfig AppUpdates { get; set; } = new();

    /// <summary>
    /// User-facing global preferences (theme, font scale, language). The
    /// Preferences dialog reads + writes this section via the
    /// <c>IPreferencesService</c>; persistence is through
    /// <see cref="General.ConfigLoader"/>.<c>Save</c>.
    /// </summary>
    public PreferencesConfig Preferences { get; set; } = new();

    /// <summary>A fully-defaulted config instance.</summary>
    public static CuratorConfig CreateDefault() => new();
}
