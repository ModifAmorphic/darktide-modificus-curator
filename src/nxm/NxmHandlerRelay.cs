using System.ComponentModel;
using System.Diagnostics;

namespace Modificus.Curator.Nxm;

/// <summary>
/// The testable core of the OS-registered handler exe. Forwards the raw
/// <c>nxm://</c> URL to the running Curator app over the fixed named pipe, and
/// when Curator is not running, launches it (no args) and retries the pipe until
/// it comes up, then delivers the URL via the same IPC path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Behavior:</b>
/// <list type="number">
/// <item>Extract the URL: the first non-flag arg. If none, log to stderr and
/// return non-zero.</item>
/// <item>Try <c>pipeConnect</c>: on success, write the framed URL, close,
/// return 0.</item>
/// <item>On refused connect: consult <c>curatorRunningCheck</c>. When a
/// Curator process is already running, skip the launch and go straight to the
/// retry loop (a burst of handler invocations must not each launch a
/// duplicate; the losers only die at the single-instance guard). Otherwise
/// locate the sibling Curator exe, launch it detached (no args), then either
/// way retry <c>pipeConnect</c> every <paramref name="retryInterval"/> until
/// success or <paramref name="retryTimeout"/> elapses, then deliver + exit 0.</item>
/// <item>On timeout: log "Curator did not start within Ns" to stderr, return non-zero.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cold start is owned by the handler, not Curator.</b> Curator's startup is
/// untouched by the nxm handler: no <c>--nxm</c> arg, no cold-start branch. The handler
/// owns the entire orchestration.
/// </para>
/// <para>
/// <b>The pre-launch check is advisory.</b> <paramref name="curatorRunningCheck"/>
/// runs only after a refused connect and only to avoid a duplicate launch; it
/// adds no synchronization, and any race (Curator exiting between the check
/// and the connect, or the check under-detecting) falls through to the
/// existing retry/timeout semantics. On Linux, <see cref="Process.GetProcessesByName"/>
/// compares the exact full executable name recovered from /proc cmdline when
/// available (the kernel's 15-character comm field is only the fallback), so
/// the check is effective there in the normal case; under-detection is
/// confined to degraded shapes (a zombie's empty cmdline, an unreadable
/// cmdline, or an argv0-rewriting launcher). A missed detection means one
/// extra launch attempt, which the single-instance guard already handles.
/// </para>
/// <para>
/// <b>AOT-safe.</b> No DI, no Avalonia, no JSON, no reflection. Only raw byte /
/// UTF-8 IO via <see cref="NxmIpcFraming"/>, <see cref="Console"/> for stderr,
/// and <see cref="Process"/> for the Curator launch. The handler exe publishes
/// native AOT.
/// </para>
/// <para>
/// Every external dependency is an injectable seam so the relay is fully
/// testable without real processes or pipes: <paramref name="pipeConnect"/>,
/// <paramref name="launchCuratorFactory"/>, <paramref name="curatorRunningCheck"/>,
/// the retry interval, and the retry timeout all default to production
/// behavior and are overridden by tests.
/// </para>
/// </remarks>
public static class NxmHandlerRelay
{
    /// <summary>
    /// Default connect attempt timeout for the default <paramref name="pipeConnect"/>.
    /// 1s, not less: a connected pipe frees quickly once the routed handler
    /// returns promptly, but the enqueue gates read config from disk inside
    /// the connected window; the headroom is cheap insurance against
    /// slow-disk false cold-starts.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>Default interval between pipe-connect retries during cold start.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Default total time to wait for Curator to come up during cold start.</summary>
    public static readonly TimeSpan DefaultRetryTimeout = TimeSpan.FromSeconds(30);

    // The Curator exe name without extension: one source of truth for the
    // launch path (which appends the Windows extension) and the running check
    // (GetProcessesByName matches bare names on both platforms).
    private const string CuratorProcessName = "Modificus.Curator";

    /// <summary>
    /// Runs the relay. Returns the process exit code (0 on success, non-zero on
    /// no-arg / unrecoverable failure). Seams default to the real
    /// implementations; tests inject fakes.
    /// </summary>
    /// <param name="args">The handler exe's command-line args (the OS passes the <c>nxm://</c> URL).</param>
    /// <param name="pipeConnect">
    /// Connects to the named pipe. Returns <c>(connected: true, stream)</c> on
    /// success; <c>(connected: false, null)</c> on a refused / timed-out
    /// connect. The default uses <see cref="System.IO.Pipes.NamedPipeClientStream"/>
    /// with a short connect timeout.
    /// </param>
    /// <param name="launchCuratorFactory">
    /// Builds the <see cref="ProcessStartInfo"/> used to launch Curator when the
    /// pipe is not owned. The default derives the sibling Curator exe from
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="curatorRunningCheck">
    /// Reports whether a Curator process is already running. Consulted before
    /// the cold-start launch so a burst of handler invocations (each seeing the
    /// same refused pipe) does not each launch a duplicate Curator. The default
    /// reuses <see cref="SingleInstanceGuard"/>'s process enumeration over the
    /// Curator exe name.
    /// </param>
    /// <param name="retryInterval">Cold-start retry interval (default 250ms).</param>
    /// <param name="retryTimeout">Cold-start total timeout (default 30s).</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<(bool connected, Stream? stream)>>? pipeConnect = null,
        Func<ProcessStartInfo>? launchCuratorFactory = null,
        Func<bool>? curatorRunningCheck = null,
        TimeSpan? retryInterval = null,
        TimeSpan? retryTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var connect = pipeConnect ?? DefaultPipeConnect;
        var launchFactory = launchCuratorFactory ?? DefaultLaunchCuratorFactory;
        var curatorRunning = curatorRunningCheck ?? DefaultCuratorRunningCheck;
        var interval = retryInterval ?? DefaultRetryInterval;
        var timeout = retryTimeout ?? DefaultRetryTimeout;

        // 1. Extract the URL: the first non-flag arg.
        var url = ExtractUrl(args);
        if (url is null)
        {
            Console.Error.WriteLine("curator nxm handler: no nxm:// URL argument was provided.");
            return ExitNoUrl;
        }

        // 2. Hot path: Curator is running. Connect, deliver, exit.
        if (await ConnectAndDeliverAsync(connect, url, ct).ConfigureAwait(false))
            return ExitOk;

        // 3. Cold start: launch Curator (skipped when the advisory pre-check
        // sees one already running; races just fall through to the retry
        // semantics below), then retry the pipe.
        if (curatorRunning())
        {
            Console.Error.WriteLine(
                "curator nxm handler: Curator is already running; waiting for the pipe.");
        }
        else
        {
            Console.Error.WriteLine("curator nxm handler: Curator is not running; launching it.");
            if (!LaunchCurator(launchFactory))
                return ExitLaunchFailed;
        }

        // 4. Retry connect until Curator binds the pipe or the timeout elapses.
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            // Wait out the retry interval (clamped to the remaining time so the
            // final attempt doesn't overshoot the deadline by a full interval).
            var delay = remaining < interval ? remaining : interval;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);

            if (await ConnectAndDeliverAsync(connect, url, ct).ConfigureAwait(false))
                return ExitOk;
        }

        Console.Error.WriteLine(
            $"curator nxm handler: Curator did not start within {timeout.TotalSeconds:F0}s; giving up.");
        return ExitTimeout;
    }

    // ---- seams -----------------------------------------------------------

    private static async Task<bool> ConnectAndDeliverAsync(
        Func<string, CancellationToken, Task<(bool connected, Stream? stream)>> connect,
        string url, CancellationToken ct)
    {
        var (connected, stream) = await connect(NxmIpcServer.DefaultPipeName, ct).ConfigureAwait(false);
        if (!connected || stream is null)
            return false;

        // Own the stream's lifecycle: write the framed URL, then close (the
        // server reads one message per connection, then closes too).
        try
        {
            await NxmIpcFraming.WriteUrlAsync(stream, url, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"curator nxm handler: failed to send the URL: {ex.Message}");
            return false;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool LaunchCurator(Func<ProcessStartInfo> launchFactory)
    {
        try
        {
            var psi = launchFactory()
                ?? throw new InvalidOperationException("launchCuratorFactory returned null.");
            // Fire-and-forget: do not wait. The launched Curator outlives the
            // handler (no job object ties them).
            Process.Start(psi);
            return true;
        }
        catch (CuratorMainExeNotFoundException ex)
        {
            // The sibling Curator exe is absent. Nothing to retry against: log a
            // clear stderr line and bail (RunAsync returns ExitLaunchFailed
            // immediately, before the retry loop).
            Console.Error.WriteLine($"curator nxm handler: {ex.Message}");
            return false;
        }
        catch (Win32Exception ex)
        {
            // Backstop: Process.Start throws Win32Exception (e.g. file not
            // found, access denied) if the existence pre-check in the default
            // factory was somehow bypassed (a custom factory). Also a hard
            // stop, no retry.
            Console.Error.WriteLine($"curator nxm handler: failed to launch Curator: {ex.Message}");
            return false;
        }
    }

    // ---- defaults --------------------------------------------------------

    private static async Task<(bool connected, Stream? stream)> DefaultPipeConnect(
        string pipeName, CancellationToken ct)
    {
        var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        try
        {
            // A short, cancellation-backed timeout per attempt. Cross-platform:
            // ConnectAsync(CancellationToken) honors cancellation reliably on
            // Windows + Linux (the int-timeout overloads have historical Linux
            // quirks).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultConnectTimeout);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            return (true, client);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our per-attempt timeout fired (the linked token, not the caller's).
            await client.DisposeAsync().ConfigureAwait(false);
            return (false, null);
        }
        catch (System.IO.IOException)
        {
            // No server listening (Curator not up yet).
            await client.DisposeAsync().ConfigureAwait(false);
            return (false, null);
        }
        catch (TimeoutException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return (false, null);
        }
    }

    private static bool DefaultCuratorRunningCheck()
    {
        // The same detection SingleInstanceGuard enforces with, reused rather
        // than reimplemented. The handler's own name differs from Curator's on
        // both platforms, so excluding self is symmetry, not a necessity.
        using var current = Process.GetCurrentProcess();
        return new SingleInstanceGuard().IsAnotherInstanceRunning(CuratorProcessName, current.Id);
    }

    private static ProcessStartInfo DefaultLaunchCuratorFactory()
    {
        var path = ResolveCuratorMainExe(AppContext.BaseDirectory);

        // UseShellExecute MUST stay false on both OSes: launch the exe directly.
        // Do NOT "fix" this to true on Linux for detached launch: that routes
        // through xdg-open, which pops a desktop error dialog if the path is
        // ever missing (it blocked a smoke test for hours). Detached fire-and-
        // forget needs no UseShellExecute=true: Process.Start without
        // WaitForExit already detaches. CreateNoWindow=true on Windows keeps the
        // secondary launch quiet.
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = OperatingSystem.IsWindows(),
        };
        return psi;
    }

    /// <summary>
    /// Resolves the sibling Curator main exe under <paramref name="baseDirectory"/>
    /// and verifies it exists, returning the full path. Throws
    /// <see cref="CuratorMainExeNotFoundException"/> if the sibling is absent, so
    /// the relay can log a clear stderr line and exit non-zero without entering
    /// the retry loop (a headless handler must never hand a bad path to the
    /// shell).
    /// </summary>
    /// <remarks>
    /// Factored out of <see cref="DefaultLaunchCuratorFactory"/> so the existence
    /// check is testable against an arbitrary base directory; the production
    /// call passes <see cref="AppContext.BaseDirectory"/>.
    /// </remarks>
    internal static string ResolveCuratorMainExe(string baseDirectory)
    {
        // The handler exe is a sibling of the main Curator exe (both ship in the
        // same dir). Modificus.Curator on Linux, Modificus.Curator.exe on Windows.
        var exeName = OperatingSystem.IsWindows() ? CuratorProcessName + ".exe" : CuratorProcessName;
        var path = Path.Combine(baseDirectory, exeName);
        if (!File.Exists(path))
            throw new CuratorMainExeNotFoundException(path, baseDirectory);
        return path;
    }

    // ---- helpers ---------------------------------------------------------

    private static string? ExtractUrl(string[] args)
    {
        // The OS passes the nxm:// URL as a single arg; take the first non-flag
        // value (one that does not start with '-' or '/') so incidental flags
        // do not get mistaken for the URL.
        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg))
                continue;
            if (arg[0] == '-' || arg[0] == '/')
                continue;
            return arg;
        }
        return null;
    }

    private const int ExitOk = 0;
    private const int ExitNoUrl = 1;
    private const int ExitLaunchFailed = 2;
    private const int ExitTimeout = 3;
}

/// <summary>
/// Raised by <see cref="NxmHandlerRelay.ResolveCuratorMainExe"/> (called by the
/// default launch factory) when the sibling Curator main executable cannot be
/// found next to the handler exe. The relay logs this to stderr and exits
/// non-zero immediately, without entering the cold-start retry loop: there is
/// nothing to retry against if Curator could not be launched.
/// </summary>
public sealed class CuratorMainExeNotFoundException : Exception
{
    /// <summary>The resolved path the handler looked at and did not find.</summary>
    public string ExpectedPath { get; }

    /// <summary>The handler base directory the path was resolved from.</summary>
    public string BaseDirectory { get; }

    public CuratorMainExeNotFoundException(string expectedPath, string baseDirectory)
        : base($"Could not find the Curator main executable at '{expectedPath}'. " +
               $"Expected the handler ({baseDirectory}) to ship alongside Modificus.Curator.")
    {
        ExpectedPath = expectedPath;
        BaseDirectory = baseDirectory;
    }
}
