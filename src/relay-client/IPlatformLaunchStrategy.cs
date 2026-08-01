using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// One platform's launch strategy: the spawn (via <see cref="IProcessLauncher"/>),
/// the discovery fields that platform requires, and a label for logging. One
/// implementation per runtime OS; the launch orchestrator stays
/// platform-agnostic and contains no per-launch OS branch.
/// </summary>
/// <remarks>
/// The strategy owns exactly what varies by platform; everything else
/// (discovery, <c>PrepareModRoot</c>, the launcher-existence check, result
/// mapping, the try/catch contract) stays in the orchestrator.
/// </remarks>
internal interface IPlatformLaunchStrategy
{
    /// <summary>A short label ("Windows" / "Linux") for log messages.</summary>
    string Name { get; }

    /// <summary>
    /// The discovery fields this platform requires but discovery could not
    /// resolve. Field names mirror <see cref="DiscoveryResult"/>'s properties so
    /// they map to prompt fields. Equivalent to
    /// <see cref="DiscoveryStatus"/> != <see cref="DiscoveryStatus.Complete"/>
    /// for this platform: derived from the fields directly so the result and
    /// the missing-field list cannot diverge.
    /// </summary>
    IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery);

    /// <summary>
    /// Spawns <c>mod_relay.exe</c> for this platform. Windows: a direct
    /// invocation of <paramref name="launcherPath"/> with native (untranslated)
    /// args. Linux: <c>&lt;proton&gt; run &lt;launcherPath&gt; &lt;args&gt;</c>
    /// with both <c>STEAM_COMPAT_*</c> env vars and the path-valued flags
    /// <c>Z:\</c>-translated (the launcher runs under Wine). Fire-and-forget:
    /// returns <c>true</c> if the process started.
    /// </summary>
    /// <param name="launcherPath">Native path to <c>mod_relay.exe</c>.</param>
    /// <param name="discovery">The resolved discovery (Linux reads the Proton +
    /// compat paths + Steam install from it; Windows ignores it: it already has
    /// <paramref name="gameBinary"/>).</param>
    /// <param name="gameBinary">The resolved Darktide game binary (non-null:
    /// discovery completeness was checked by the caller).</param>
    /// <param name="modPath">The prepared mod root (the <c>--mod-path</c>).</param>
    /// <param name="logFile">The shell log file (the <c>--log-file</c>).</param>
    /// <param name="launchSettings">The profile's launch settings. Environment
    /// variables are merged into the spawn request (Linux: inherited -> AppImage
    /// removals -> profile env -> Curator-owned <c>STEAM_COMPAT_*</c> last;
    /// Windows: profile env as overrides on the Relay process). Game arguments
    /// are appended after the launcher's own flags as a single bare <c>--</c>
    /// separator then one argv entry each (Relay's <c>--</c> contract); empty
    /// game args emit no <c>--</c>. A bare <c>--log-append</c> is emitted
    /// unconditionally right after <c>--log-file</c> (Relay's per-day file is
    /// shared across launches, so it appends; no value, not path-translated).
    /// <see cref="LaunchSettings.EnableLuaLogs"/> controls emission of Relay's
    /// bare <c>--log-lua</c> logging flag (appended after <c>--log-append</c>,
    /// no value, not path-translated). <see cref="LaunchSettings.SkipSplash"/>
    /// controls emission of Relay's bare <c>--skip-splash</c> flag (skips
    /// Darktide's intro splash state; appended after <c>--log-lua</c>, no value,
    /// not path-translated).
    /// </param>
    /// <param name="createNoWindow">When <c>true</c>, suppresses the spawned
    /// Relay process's console window (flows through to the launch request's
    /// <see cref="ProcessLaunchRequest.CreateNoWindow"/>). The orchestrator
    /// derives this from the global <c>ShowRelayConsole</c> preference.</param>
    bool Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile, LaunchSettings launchSettings, bool createNoWindow);
}
