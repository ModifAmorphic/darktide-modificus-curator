namespace Modificus.Curator.Steam;

/// <summary>
/// Steam discovery + game-running detection. Steam <b>discovers</b> everything
/// needed to launch Darktide modded on the current OS (Steam install, Darktide
/// install, compatdata, Proton version) and reports missing pieces via
/// <see cref="DiscoveryResult.Status"/>; it does NOT set env vars or invoke
/// Proton (that is the launch layer's job, consuming the
/// <see cref="DiscoveryResult"/>).
/// </summary>
/// <remarks>
/// The discovery result is a flat record of nullables: the null fields are the
/// missing pieces a caller should prompt for (the escape hatch).
/// </remarks>
public interface ISteamService
{
    /// <summary>
    /// Runs Steam discovery and returns the result, honoring the configured
    /// discovery mode (see <see cref="Config.DiscoveryConfig.OverrideAutomaticDiscovery"/>).
    /// Never throws on missing pieces: those are reported via
    /// <see cref="DiscoveryResult.Status"/> + the nullable fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Automatic mode</b> (default, <c>OverrideAutomaticDiscovery = false</c>):
    /// runs the platform discoverer every call and atomically replaces the
    /// active-platform path snapshot in config with the result (including nulls
    /// that clear stale values). On Windows only Steam + Darktide are written
    /// (Linux-only fields are left untouched); on Linux all four fields are
    /// written.</para>
    /// <para>
    /// <b>Manual mode</b> (<c>OverrideAutomaticDiscovery = true</c>): the
    /// discoverer is not invoked. The stored paths are validated on disk by kind
    /// (directory for Steam + compatdata; file for Darktide + Proton); valid
    /// values are returned and invalid/missing ones surface as null fields. The
    /// stored input is never rewritten or cleared.
    /// <see cref="DiscoveryResult.ProtonVersion"/> is null in manual mode.</para>
    /// </remarks>
    DiscoveryResult Discover();

    /// <summary>
    /// Forces one automatic discovery pass regardless of the configured mode,
    /// replaces the active-platform path snapshot (including nulls), leaves
    /// <see cref="Config.DiscoveryConfig.OverrideAutomaticDiscovery"/> unchanged,
    /// and returns the discoverer's result.
    /// </summary>
    DiscoveryResult Rediscover();

    /// <summary>
    /// Whether Darktide is currently running. Cross-platform best-effort check
    /// against the game's process name.
    /// </summary>
    bool IsGameRunning();
}

/// <summary>
/// The outcome of a Steam discovery pass. Fields are nullable: a null means
/// "couldn't resolve this; the UI should prompt for it" (the escape hatch).
/// <see cref="Status"/> summarizes whether everything critical for the current
/// OS was found.
/// </summary>
/// <param name="SteamInstallPath">Steam client dir, the value for
/// <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>.</param>
/// <param name="DarktideGameBinaryPath">Native path to <c>Darktide.exe</c>
/// (Relay-client Z:\-translates on Linux for <c>--game-binary</c>).</param>
/// <param name="CompatdataPath">Wine prefix, the value for
/// <c>STEAM_COMPAT_DATA_PATH</c> (Linux only).</param>
/// <param name="ProtonBinaryPath">The <c>proton</c> script for <c>proton run</c>
/// (Linux only).</param>
/// <param name="ProtonVersion">An informational label for the resolved Proton
/// (the compatibility tool's display name, or its internal name). Null in manual
/// mode.</param>
/// <param name="Status">Complete / Partial / Failed, see <see cref="DiscoveryStatus"/>.</param>
/// <param name="Warnings">Non-fatal notes (e.g. Flatpak detection, an unresolvable
/// compatibility tool).</param>
public sealed record DiscoveryResult(
    string? SteamInstallPath,
    string? DarktideGameBinaryPath,
    string? CompatdataPath,
    string? ProtonBinaryPath,
    string? ProtonVersion,
    DiscoveryStatus Status,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Coarse status of a discovery pass:
/// <list type="bullet">
/// <item><term>Complete</term><description>Every critical field for the current OS is non-null.</description></item>
/// <item><term>Partial</term><description>Some critical field resolved but the result is not
/// launchable (the nullables indicate what the UI should prompt for).</description></item>
/// <item><term>Failed</term><description>No critical field resolved (the UI prompts for the
/// entry-point field).</description></item>
/// </list>
/// </summary>
public enum DiscoveryStatus
{
    Complete,
    Partial,
    Failed,
}

/// <summary>
/// The platform discovery runs against. Production picks this from the runtime
/// OS; tests can force a platform to exercise cross-platform logic on one OS.
/// Darktide ships on Windows (native) and Linux (Proton) only.
/// </summary>
public enum DiscoveryPlatform
{
    /// <summary>Linux: discovers Steam + Darktide + compatdata + Proton.</summary>
    Linux,

    /// <summary>Windows: discovers Steam + Darktide only (native; Proton/compatdata unused).</summary>
    Windows,
}
