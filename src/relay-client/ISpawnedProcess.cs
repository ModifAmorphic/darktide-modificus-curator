namespace Modificus.Curator.RelayClient;

/// <summary>
/// The process spawned by <see cref="IProcessLauncher.Start"/>: the capability
/// to observe its exit and release its resources. An observation handle only:
/// callers cannot start, stop, signal, or inspect the process through it.
/// </summary>
public interface ISpawnedProcess : IDisposable
{
    /// <summary>
    /// Completes when the spawned process exits (any exit code). Never starts
    /// or signals the process. Implementations may throw if the handle is
    /// already disposed; callers that only need the exit signal should absorb
    /// faults.
    /// </summary>
    Task WaitForExitAsync();
}
