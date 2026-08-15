using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Default <see cref="IProcessLauncher"/>. Spawns the child via
/// <see cref="Process.Start(ProcessStartInfo)"/> using
/// <see cref="ProcessStartInfo.ArgumentList"/> (argv-correct, no shell), so paths
/// containing spaces survive verbatim and there is no shell-injection surface.
/// The child starts from the parent's inherited environment, with each key in
/// <see cref="ProcessLaunchRequest.EnvironmentVariablesToRemove"/> stripped and
/// each <see cref="ProcessLaunchRequest.EnvironmentOverrides"/> entry applied on
/// top (overrides win when a key appears in both sets).
/// </summary>
/// <remarks>
/// <para>
/// Non-blocking: <see cref="Start"/> starts the process and returns without
/// waiting. The returned <see cref="ISpawnedProcess"/> handle exposes the
/// spawned process's exit as a bare task + releases its resources; the
/// launcher + injected shell own their own lifecycle and the game process is
/// intentionally not tracked in v1.</para>
/// <para>
/// Registered as a singleton via <c>AddRelayClient()</c> with <c>TryAdd</c>
/// so tests (and hosts with a custom launch hook) can pre-register an override --
/// the same pattern the Steam library uses for <c>IProcessLookup</c>.</para>
/// </remarks>
internal sealed class ProcessLauncher : IProcessLauncher
{
    private readonly ILogger<ProcessLauncher> _logger;

    public ProcessLauncher(ILogger<ProcessLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ISpawnedProcess? Start(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = BuildStartInfo(request);

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                // Process.Start returns null only when a new process is reusing an
                // already-running one's resources -- rare, but treat as "not started."
                _logger.LogWarning("Process.Start returned null for {File}.", request.FilePath);
                return null;
            }

            _logger.LogInformation(
                "Started process {Pid} for {File} ({Count} arguments).",
                process.Id, request.FilePath, request.Arguments.Count);
            return new SpawnedProcess(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or FileNotFoundException
                                       or Win32Exception
                                       or PlatformNotSupportedException)
        {
            // The common start failures: missing file, permission denied
            // (Win32Exception), or no platform support. Never throw -- the service
            // maps a null return to LaunchStatus.Error with a clear message.
            _logger.LogWarning(ex, "Failed to start process {File}.", request.FilePath);
            return null;
        }
    }

    /// <summary>
    /// Builds the deterministic <see cref="ProcessStartInfo"/> for a request:
    /// <see cref="ProcessStartInfo.UseShellExecute"/> set <c>false</c> (required
    /// to modify the environment and to use <see cref="ProcessStartInfo.ArgumentList"/>),
    /// each argument added separately to <see cref="ProcessStartInfo.ArgumentList"/>,
    /// each requested key removed from <see cref="ProcessStartInfo.Environment"/>
    /// (which lazily snapshots the inherited parent block on first access), and
    /// then each override applied on top so an override wins for a key in both
    /// sets. Pure: no process is started.
    /// </summary>
    /// <remarks>
    /// Factored as an internal pure helper so tests can inspect the final
    /// environment + argument layout without spawning a real process. Production
    /// <see cref="Start"/> uses this exact path.
    /// </remarks>
    internal static ProcessStartInfo BuildStartInfo(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // FilePath is guaranteed non-blank by ProcessLaunchRequest's constructor;
        // no redundant whitespace check here.

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FilePath,
            // UseShellExecute=false is required both to mutate the environment
            // and to use ArgumentList (no shell). The launcher is an .exe even on
            // Linux (Proton runs it), so we never want the OS shell in the middle.
            UseShellExecute = false,
            // CreateNoWindow suppresses the child's console window (honored only
            // when UseShellExecute=false). Used to hide Relay's console window
            // unless the global ShowRelayConsole preference opts in; a harmless
            // no-op on Linux where no console appears regardless. The game's own
            // window is unaffected.
            CreateNoWindow = request.CreateNoWindow,
        };

        // ArgumentList quotes/escapes per-platform; callers hand us argv form, so
        // add each entry verbatim. A null entry would corrupt the argv layout --
        // coerce to "" defensively rather than throw (the launch façade never
        // produces nulls, but this stays safe for any IProcessLauncher caller).
        foreach (var arg in request.Arguments)
        {
            startInfo.ArgumentList.Add(arg ?? string.Empty);
        }

        // ProcessStartInfo.Environment lazily snapshots the parent's environment
        // on first access. Remove every requested key before applying overrides;
        // Remove is a no-op for a key the parent did not carry.
        foreach (var key in request.EnvironmentVariablesToRemove)
        {
            startInfo.Environment.Remove(key);
        }

        // Overrides applied AFTER removals so a key listed in both sets ends up
        // with the override's value (the override intentionally wins).
        foreach (var pair in request.EnvironmentOverrides)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    /// <summary>
    /// The <see cref="ISpawnedProcess"/> over the real <see cref="Process"/>:
    /// exit observation via <see cref="Process.WaitForExitAsync(CancellationToken)"/>
    /// and resource release via <see cref="Process.Dispose"/>. Private + sealed
    /// so only <see cref="ProcessLauncher"/> creates it; the handle's owner
    /// (whoever observes the exit) is its single disposer.
    /// </summary>
    private sealed class SpawnedProcess(Process process) : ISpawnedProcess
    {
        public Task WaitForExitAsync() => process.WaitForExitAsync(CancellationToken.None);

        public void Dispose() => process.Dispose();
    }
}
