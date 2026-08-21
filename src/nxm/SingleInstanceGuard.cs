using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Nxm;

/// <summary>
/// Enforces single-instance by enumerating live processes that share this
/// process's name. Used by <see cref="NxmIpcServer.Bind"/> before any pipe work,
/// so single-instance enforcement is decoupled from the IPC pipe (which is its
/// own check with its own graceful-degradation outcome).
/// </summary>
/// <remarks>
/// <para>
/// <b>How it matches.</b> The current process's name is obtained via
/// <see cref="Process.ProcessName"/> from <see cref="Process.GetCurrentProcess"/>
/// (the executable name on Linux; the exe name without extension on Windows),
/// and the PID via <see cref="Process.Id"/>. The injected enumerator lists live
/// processes with that name; the default production implementation uses
/// <see cref="Process.GetProcessesByName(string)"/> and excludes self by PID. If
/// any other process remains, another Curator is running.</para>
/// <para>
/// <b>Why process enumeration, not a pipe probe.</b> Process enumeration directly
/// answers "is another Curator already running?" without the startup tax a
/// pipe probe-as-client imposes on Linux (where it pends when no server exists).
/// It is unprivileged (process enumeration needs no elevation) and decoupled from
/// the IPC transport: the pipe is its own check, not a single-instance proxy.</para>
/// <para>
/// <b>Test seam.</b> The enumerator is an injectable delegate
/// (<see cref="OtherInstanceEnumerator"/>) so tests inject a fake that reports
/// "found" or "not found" deterministically, without spawning real processes.</para>
/// <para>
/// <b>Accepted race.</b> Two instances starting within milliseconds could both
/// enumerate, both see no other, both proceed. For a desktop double-launch (the
/// realistic case: seconds apart, not microseconds) this is negligible; a cross-
/// process mutex / lock-file on top is not worth the complexity for v1.
/// Documented and accepted.</para>
/// </remarks>
public sealed class SingleInstanceGuard
{
    /// <summary>
    /// Returns the PIDs of live processes (other than self) whose name matches
    /// <paramref name="processName"/>. The default production implementation
    /// (<see cref="EnumerateOthers"/>) uses
    /// <see cref="Process.GetProcessesByName(string)"/> and excludes self by PID;
    /// tests inject a fake that returns a populated array (another instance
    /// exists) or an empty array (alone).
    /// </summary>
    public delegate int[] OtherInstanceEnumerator(string processName, int ownPid);

    private readonly OtherInstanceEnumerator _enumerate;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the guard. <paramref name="enumerate"/> defaults to the production
    /// <see cref="Process.GetProcessesByName"/> implementation; tests inject a
    /// fake. <paramref name="logger"/> defaults to a null logger.
    /// </summary>
    public SingleInstanceGuard(OtherInstanceEnumerator? enumerate = null, ILogger? logger = null)
    {
        _enumerate = enumerate ?? EnumerateOthers;
        _logger = logger ?? NullLogger<SingleInstanceGuard>.Instance;
    }

    /// <summary>
    /// Throws <see cref="NxmSingleInstanceException"/> if another live process
    /// shares this process's name. <paramref name="ipcPipeName"/> is carried on
    /// the exception as the IPC context (the single-instance check guards the
    /// IPC server bind that follows in <see cref="NxmIpcServer.Bind"/>).
    /// </summary>
    public void EnsureOnlyInstance(string ipcPipeName)
    {
        var current = Process.GetCurrentProcess();
        string name;
        int ownPid;
        try
        {
            name = current.ProcessName;
            ownPid = current.Id;
        }
        finally
        {
            current.Dispose();
        }

        var others = _enumerate(name, ownPid);
        if (others.Length > 0)
        {
            _logger.LogInformation(
                "Single-instance check found another Curator process (pid {Pid}, name '{Name}').",
                others[0], name);
            throw new NxmSingleInstanceException(
                ipcPipeName,
                new InvalidOperationException(
                    $"Another Curator process (pid {others[0]}) with name '{name}' is already running."));
        }
    }

    /// <summary>
    /// Non-throwing query over the same process enumeration
    /// <see cref="EnsureOnlyInstance"/> enforces with: true when any live
    /// process other than <paramref name="excludingPid"/> shares
    /// <paramref name="processName"/>. Callers checking for "another me" pass
    /// the current process's name and PID (what
    /// <see cref="EnsureOnlyInstance"/> derives internally); callers checking
    /// for a different executable pass that executable's name (the nxm
    /// handler relay asks whether Curator is up before its cold-start launch).
    /// </summary>
    public bool IsAnotherInstanceRunning(string processName, int excludingPid)
        => _enumerate(processName, excludingPid).Length > 0;

    /// <summary>
    /// The production enumerator. Uses
    /// <see cref="Process.GetProcessesByName(string)"/>, excludes self by PID,
    /// and disposes each <see cref="Process"/> handle. Enumeration failures
    /// (denied, or the runner lacks process-query rights) degrade to an empty
    /// result so a process-enumeration glitch does not block a legitimate launch
    /// under a false negative.
    /// </summary>
    private int[] EnumerateOthers(string processName, int ownPid)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex,
                "Process enumeration for the single-instance check was denied; proceeding without it.");
            return Array.Empty<int>();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex,
                "Process enumeration for the single-instance check failed; proceeding without it.");
            return Array.Empty<int>();
        }

        var others = new List<int>(processes.Length);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.Id != ownPid)
                        others.Add(p.Id);
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and the Id access; skip.
                }
            }
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }

        return others.ToArray();
    }
}
