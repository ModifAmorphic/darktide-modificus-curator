namespace Modificus.Curator.RelayClient;

/// <summary>
/// Process-launch abstraction used by <see cref="IRelayLaunchService"/> to
/// spawn <c>mod_relay.exe</c> -- directly on Windows, under <c>proton run</c>
/// on Linux. Abstracted so the side-effect (spawning a process) is injectable,
/// leaving the launch service as pure argument assembly + decision logic.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Starts the process described by <paramref name="request"/> (executable
    /// path, argv-form arguments, and requested environment mutations) and
    /// returns without waiting. Returns the spawned process's observation
    /// handle (<see cref="ISpawnedProcess"/>), or <c>null</c> if it could not
    /// be started (file not found, permission denied, etc.: never throws for
    /// those).
    /// </summary>
    /// <param name="request">The immutable launch description. The
    /// implementation must apply each argument verbatim (no re-shelling or
    /// concatenation), strip every key in
    /// <see cref="ProcessLaunchRequest.EnvironmentVariablesToRemove"/> from the
    /// inherited environment, then apply
    /// <see cref="ProcessLaunchRequest.EnvironmentOverrides"/> (overrides win
    /// when a key appears in both sets).</param>
    ISpawnedProcess? Start(ProcessLaunchRequest request);
}
