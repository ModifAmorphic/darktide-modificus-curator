namespace Modificus.Curator.Profiles;

/// <summary>
/// A profile's launch settings: the ordered environment-variable entries and
/// Darktide command-line arguments a profile asks Curator to apply at launch.
/// Environment values reach Proton before it starts on Linux (inherited by
/// Proton/Relay/Darktide) and the Relay launcher process on Windows; game
/// arguments flow through Relay's bare-<c>--</c> contract verbatim, in order.
/// </summary>
/// <remarks>
/// <para>
/// Immutable value type, matching the existing <see cref="ModListEntry"/>
/// pattern: the collections are <see cref="IReadOnlyList{T}"/> so JSON order is
/// explicit and game-argument order and duplicates survive persistence. Changes
/// go through <see cref="IProfileService.SetLaunchSettings"/>, which validates,
/// rebuilds the profile aggregate preserving Name/Id/CreatedAt/Mods, and
/// persists; callers cannot mutate persisted state in place.</para>
/// <para>
/// <b>Backward compatibility:</b> an existing <c>profile.json</c> without the
/// property, and an explicit JSON <c>null</c>, both deserialize to an empty
/// (non-null) instance. <see cref="EnvironmentVariables"/> defaults to an empty
/// array so a freshly-created profile serializes an empty, non-null list. The
/// collections never deserialize to <c>null</c> because
/// <see cref="ProfileService"/>'s read path coerces a null
/// <see cref="Profile.LaunchSettings"/> to <c>new()</c> (mirroring the existing
/// <c>Mods ??= Empty</c> normalization).</para>
/// </remarks>
public sealed record LaunchSettings
{
    /// <summary>
    /// The environment-variable names a profile may NOT set, case-insensitive.
    /// Two groups:
    /// <list type="bullet">
    /// <item><term>Curator-owned OS/launch env (7).</term>
    /// <description>Curator sets or removes these itself; a profile value would
    /// fight Curator or break the AppImage-identity invariant:
    /// <c>STEAM_COMPAT_DATA_PATH</c>, <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>,
    /// <c>APPDIR</c>, <c>APPIMAGE</c>, <c>ARGV0</c>, <c>OWD</c>,
    /// <c>BAMF_DESKTOP_FILE_HINT</c>.</description></item>
    /// <item><term>Relay config env (7).</term>
    /// <description>Curator owns these knobs and supplies them as flags (Relay's
    /// config model is flag &gt; env &gt; default):
    /// <c>MODIFICUS_GAME_BINARY</c>, <c>MODIFICUS_MOD_PATH</c>,
    /// <c>RELAY_LOG_FILE</c>, <c>RELAY_LOG_LEVEL</c>,
    /// <c>MODIFICUS_STEAM_APP_ID</c> (the env fallback is inert; blocked to
    /// avoid a silently-ignored value), <c>RELAY_LUA_LOGS</c> (owned by the
    /// per-profile <see cref="EnableLuaLogs"/> toggle; the env form is reserved
    /// so a profile value can't double-control or silently bypass that
    /// toggle), and <c>RELAY_SKIP_SPLASH</c> (owned by the per-profile
    /// <see cref="SkipSplash"/> toggle; the env form is reserved for the same
    /// reason, so a profile value can't double-control or silently bypass that
    /// toggle).</description></item>
    /// </list>
    /// Exposed publicly so the launch-settings UI can pre-validate and show a
    /// localized inline error before the authoritative check at
    /// <see cref="IProfileService.SetLaunchSettings"/>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReservedEnvironmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Curator-owned OS/launch env.
        "STEAM_COMPAT_DATA_PATH",
        "STEAM_COMPAT_CLIENT_INSTALL_PATH",
        "APPDIR",
        "APPIMAGE",
        "ARGV0",
        "OWD",
        "BAMF_DESKTOP_FILE_HINT",
        // Relay config env (Curator owns these knobs and supplies them as flags).
        "MODIFICUS_GAME_BINARY",
        "MODIFICUS_MOD_PATH",
        "RELAY_LOG_FILE",
        "RELAY_LOG_LEVEL",
        "MODIFICUS_STEAM_APP_ID",
        "RELAY_LUA_LOGS",
        "RELAY_SKIP_SPLASH",
    };

    /// <summary>
    /// The profile's environment-variable entries, in storage order. Validated
    /// (names + values + duplicates + reserved names) at
    /// <see cref="IProfileService.SetLaunchSettings"/>. Defaults to an empty
    /// array.
    /// </summary>
    public IReadOnlyList<EnvVar> EnvironmentVariables { get; init; } = Array.Empty<EnvVar>();

    /// <summary>
    /// The profile's Darktide command-line arguments, in order, one exact argv
    /// value each. Duplicates are preserved (each is a distinct argv entry).
    /// Curator appends a single bare <c>--</c> separator then each entry as its
    /// own <c>ArgumentList</c> element at launch (Relay owns the final
    /// <c>CreateProcess</c> quoting). When empty, no <c>--</c> is emitted
    /// (legacy launch). Defaults to an empty array.
    /// </summary>
    public IReadOnlyList<string> GameArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether to emit Relay's <c>--lua-logs</c> flag at launch, teeing Lua
    /// <c>print</c> output (the mod loader, DMF, and mods) into the same log file
    /// Relay writes (the <c>--log-file</c> Curator always emits). It is a tee,
    /// not a redirect: Darktide's console log stays complete and authoritative.
    /// Off by default. The Relay env form <c>RELAY_LUA_LOGS</c> is reserved (see
    /// <see cref="ReservedEnvironmentNames"/>) so this toggle is the single
    /// source of truth. Applies at launch; editing is unlocked while Darktide
    /// runs.
    /// </summary>
    public bool EnableLuaLogs { get; init; }

    /// <summary>
    /// Whether to emit Relay's <c>--skip-splash</c> flag at launch, skipping
    /// Darktide's intro splash state (the splash screens and intro video) so the
    /// game advances directly to the title screen. Off by default. The Relay env
    /// form <c>RELAY_SKIP_SPLASH</c> is reserved (see
    /// <see cref="ReservedEnvironmentNames"/>) so this toggle is the single
    /// source of truth. Applies at launch; editing is unlocked while Darktide
    /// runs.
    /// </summary>
    public bool SkipSplash { get; init; }
}
