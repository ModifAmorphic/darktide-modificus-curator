namespace Modificus.Curator.RelayClient;

/// <summary>
/// The launch façade over Mod Relay. Resolves the profile + Steam
/// discovery, assembles the launcher args, and invokes
/// <c>mod_relay.exe</c> -- directly on Windows, under <c>proton run</c> on
/// Linux. <see cref="Launch"/> starts the launcher and returns without
/// waiting: the only process it observes is the spawned launcher itself
/// (its exit, as a bare completion task on the result); the game process is
/// not tracked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Launch"/> resolves the profile (via
/// <c>IProfileService.PrepareModRoot</c> -- writes <c>mods.lst</c> and returns
/// the staged root) and Steam discovery (via <c>ISteamService.Discover</c>)
/// internally, so the caller just says "launch this profile." The
/// <c>--mod-path</c> handed to Relay is the parent of the <c>mods</c> folder
/// it consumes: the game dir once game-dir hosting is active (the default),
/// otherwise the staged root (the external-hosting preference).</para>
/// <para>
/// Relay-client does NOT prompt -- on incomplete discovery it returns
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> carrying the missing field
/// names, and on a foreign game-dir <c>mods</c> entry it returns
/// <see cref="LaunchStatus.GameDirConflict"/> carrying the detected path, so
/// the caller can drive the matching prompt.</para>
/// </remarks>
public interface IRelayLaunchService
{
    /// <summary>
    /// Launches the given profile modded. Always returns a
    /// <see cref="LaunchResult"/> (never throws for expected conditions):
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>the launcher process was started;
    /// <see cref="LaunchResult.RelayExited"/> completes when it exits.</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term><description>Steam discovery is missing required fields for the current OS; <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</description></item>
    /// <item><term><see cref="LaunchStatus.GameDirConflict"/></term><description>a foreign entry occupies the game-dir <c>mods</c> slot, so hosting was not set up and the game was not launched. <see cref="LaunchResult.Message"/> carries the detected path; <see cref="LaunchResult.GameDirPath"/> carries the game dir. No game-dir mutation happened: the caller surfaces a consent prompt before any retry.</description></item>
    /// <item><term><see cref="LaunchStatus.StagingFailed"/></term><description>the profile's mod root could not be prepared (a staging link could not be created). <see cref="LaunchResult.Message"/> carries the raised exception's body (the runtime/OS error).</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>unknown profile, missing runtime dir, game-dir hosting failure, or process-start failure -- see <see cref="LaunchResult.Message"/>.</description></item>
    /// </list>
    /// </summary>
    LaunchResult Launch(Guid profileId);
}

/// <summary>
/// The outcome of <see cref="IRelayLaunchService.Launch"/>.
/// </summary>
/// <param name="Status">One of <see cref="LaunchStatus.Launched"/>,
/// <see cref="LaunchStatus.DiscoveryIncomplete"/>,
/// <see cref="LaunchStatus.GameDirConflict"/>,
/// <see cref="LaunchStatus.StagingFailed"/>,
/// or <see cref="LaunchStatus.Error"/>.</param>
/// <param name="Message">Human-readable detail; populated for
/// <see cref="LaunchStatus.Error"/> and <see cref="LaunchStatus.StagingFailed"/>
/// (carries the raised exception's body; the runtime/OS error), and for
/// <see cref="LaunchStatus.GameDirConflict"/> (carries the detected game-dir
/// <c>mods</c> path, for the caller's localized consent prompt). Null
/// otherwise.</param>
/// <param name="MissingDiscoveryFields">The discovery fields the current OS
/// requires but could not be resolved; populated only for
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> (empty otherwise). Field names
/// mirror the <c>DiscoveryResult</c> properties so they map to a prompt.</param>
/// <param name="RelayExited">Completes when the spawned launcher process
/// exits: Relay directly on Windows; the Proton wrapper process on Linux,
/// whose exit follows Relay's under <c>proton run</c>. Populated only for
/// <see cref="LaunchStatus.Launched"/> (null otherwise). The task never
/// faults and carries no result value.</param>
/// <param name="GameDirPath">The resolved game directory
/// (<c>dirname(dirname(binary))</c>); populated only for
/// <see cref="LaunchStatus.GameDirConflict"/> (null otherwise), so the caller
/// can perform the consented takeover without re-deriving it.</param>
public sealed record LaunchResult(
    LaunchStatus Status,
    string? Message,
    IReadOnlyList<string> MissingDiscoveryFields,
    Task? RelayExited = null,
    string? GameDirPath = null);

/// <summary>
/// Coarse outcome of a launch attempt.
/// </summary>
public enum LaunchStatus
{
    /// <summary>The launcher process was started; its exit is observable via
    /// <see cref="LaunchResult.RelayExited"/>. The game process is not
    /// tracked.</summary>
    Launched,

    /// <summary>Steam discovery is missing required fields for the current OS;
    /// <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</summary>
    DiscoveryIncomplete,

    /// <summary>A foreign entry occupies the game-dir <c>mods</c> slot, so
    /// game-dir hosting (the default) could not be set up and nothing was
    /// launched or mutated. <see cref="LaunchResult.Message"/> carries the
    /// detected path; the caller surfaces a consent prompt whose Proceed choice
    /// performs the takeover and retries the launch once.</summary>
    GameDirConflict,

    /// <summary>The profile's mod root could not be prepared: a staging link
    /// could not be created (e.g. Windows on a non-NTFS volume, or no write
    /// access to the profile's <c>staged/</c> directory). <see cref="LaunchResult.Message"/>
    /// carries the raised exception's body (a runtime/OS error, not a string we
    /// invented); the full exception is logged.</summary>
    StagingFailed,

    /// <summary>Anything else: unknown profile, missing runtime dir, or a
    /// process-start failure. See <see cref="LaunchResult.Message"/>.</summary>
    Error,
}
