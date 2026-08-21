using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Nxm;

/// <summary>
/// The Curator-side named pipe server that receives framed <c>nxm://</c> URLs
/// from handler-exe invocations and dispatches them to the
/// <see cref="INxmRouter"/>. Cross-platform via <see cref="NamedPipeServerStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Startup is two separate checks with two separate outcomes:</b>
/// <list type="number">
/// <item><b>Single-instance</b> (<see cref="SingleInstanceGuard"/>): "is another
/// Curator process already running?" Answered via process enumeration
/// (<c>Process.GetProcessesByName</c> against the current process's name,
/// excluding self by PID). If another instance is found, <see cref="Bind"/>
/// throws <see cref="NxmSingleInstanceException"/> (fatal; the composition root
/// exits before the window shows).</item>
/// <item><b>IPC pipe bind</b>: only after single-instance passes, the
/// <see cref="NamedPipeServerStream"/> is constructed for <see cref="RunAsync"/>
/// to accept on. On success, the accept loop runs. On <see cref="IOException"/>
/// (a real pipe problem, NOT another instance, which Check 1 settled), the
/// server degrades gracefully: logs a warning and the app continues without the
/// IPC server (nxm click-to-download won't work this session).</item>
/// </list>
/// Separating the two concerns means single-instance is fast (no probe timeout)
/// and the pipe is its own check that degrades on failure rather than being
/// overloaded as a single-instance proxy.</para>
/// <para>
/// <see cref="RunAsync"/> is the accept loop: <c>WaitForConnectionAsync</c>,
/// read one framed URL, route it, <see cref="NamedPipeServerStream.Disconnect"/>
/// (keeps the server instance alive for the next client, unlike dispose +
/// recreate which would have to rebind the pipe name), loop. Per-connection
/// exceptions are logged and swallowed so a single bad client cannot kill the
/// server. The loop processes one connection at a time, which rests on an
/// explicit invariant: <see cref="INxmRouter.RouteAsync"/> must return
/// promptly. Request processing is the routed handler's responsibility and
/// must not run inline on the accept loop (the handler enqueues or refuses;
/// it does not work inline). Cancellation shuts the server down.
/// </para>
/// </remarks>
public sealed class NxmIpcServer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The fixed, cross-user pipe name. No per-user suffix: this is a
    /// single-user gaming-app context. Concurrent OS users colliding on the
    /// pipe is out of scope for v1.
    /// </summary>
    public const string DefaultPipeName = "Modificus.Curator.Nxm";

    private readonly INxmRouter _router;
    private readonly ILogger<NxmIpcServer> _logger;
    private readonly string _pipeName;
    private readonly SingleInstanceGuard _singleInstance;
    private readonly Func<string, NamedPipeServerStream> _createServerStream;

    // The single server stream, created by Bind() after the single-instance
    // check passes, and held for the app's lifetime. The accept loop
    // Disconnect()s between clients to accept the next one on the SAME stream
    // (no rebind). Null when Bind() has not yet been called OR when the pipe
    // bind failed (degraded mode; see IsBound).
    private NamedPipeServerStream? _server;
    private bool _bindAttempted;

    public NxmIpcServer(
        INxmRouter router,
        ILogger<NxmIpcServer> logger,
        string? pipeName = null,
        SingleInstanceGuard? singleInstance = null,
        Func<string, NamedPipeServerStream>? createServerStream = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipeName = pipeName ?? DefaultPipeName;
        _singleInstance = singleInstance ?? new SingleInstanceGuard(logger: _logger);
        _createServerStream = createServerStream ?? DefaultCreateServerStream;
    }

    /// <summary>
    /// True if <see cref="Bind"/> successfully bound the IPC pipe. False before
    /// <see cref="Bind"/> is called, and false after a degraded bind (the pipe
    /// ctor threw <see cref="IOException"/>). The composition root checks this
    /// to decide whether to start the accept loop.
    /// </summary>
    public bool IsBound => _server is not null;

    /// <summary>
    /// Runs the two startup checks. (1) Single-instance: delegates to
    /// <see cref="SingleInstanceGuard"/>, which enumerates processes and throws
    /// <see cref="NxmSingleInstanceException"/> if another Curator is running.
    /// (2) Pipe bind: constructs the <see cref="NamedPipeServerStream"/>; on
    /// <see cref="IOException"/> (a real pipe problem, not another instance),
    /// logs a warning and degrades (<see cref="IsBound"/> stays false, no throw).
    /// Must be called once before <see cref="RunAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why process enumeration, not the pipe bind, for single-instance.</b>
    /// The pipe bind is NOT a reliable cross-platform single-instance claim: on
    /// Linux the transport is a Unix domain socket and two processes can both
    /// bind the same path. And using the pipe as a single-instance proxy (a
    /// probe-as-client) adds a startup tax on Linux (the probe pends when no
    /// server exists). Process enumeration directly answers "is one already
    /// running?" (fast, unprivileged, decoupled from the IPC transport).</para>
    /// <para>
    /// <b>Accepted v1 race.</b> Two instances starting within milliseconds could
    /// both enumerate, both see no other, both proceed. For a desktop
    /// double-launch (the realistic case: seconds apart, not microseconds) this
    /// is negligible; a cross-process mutex / lock-file on top is not worth the
    /// complexity for v1. Documented and accepted.</para>
    /// <para>
    /// <b>Pipe failure is non-fatal.</b> Once single-instance passes, a pipe
    /// bind failure is a real transport problem (leftover socket, permissions,
    /// etc.), not another instance. The app continues without the IPC server;
    /// nxm click-to-download from Nexus won't work this session, but everything
    /// else (profiles, mods, launch) is unaffected.</para>
    /// </remarks>
    public void Bind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bindAttempted)
            throw new InvalidOperationException("NxmIpcServer.Bind() has already been called.");

        _bindAttempted = true;

        // Check 1: single-instance via process enumeration. Throws
        // NxmSingleInstanceException on collision (fatal; propagates to
        // App.axaml.cs which exits before the window shows).
        _singleInstance.EnsureOnlyInstance(_pipeName);

        // Check 2: pipe bind for IPC. Non-fatal on failure (degrade).
        try
        {
            _server = _createServerStream(_pipeName);
        }
        catch (IOException ex)
        {
            // Not a single-instance violation (Check 1 settled that). A real
            // pipe problem: leftover socket, permissions, etc. Degrade: the app
            // continues without the IPC server (nxm click-to-download from Nexus
            // won't work this session). IsBound stays false; the composition
            // root skips the accept loop.
            _logger.LogWarning(ex,
                "Failed to bind the nxm IPC pipe '{Pipe}'. nxm click-to-download from Nexus " +
                "will be unavailable this session; the app continues without the IPC server.",
                _pipeName);
            return;
        }

        _logger.LogInformation("Bound nxm IPC pipe '{Pipe}'.", _pipeName);
    }

    /// <summary>
    /// The accept loop. Assumes <see cref="Bind"/> succeeded (the pipe is bound;
    /// <see cref="IsBound"/> is true). Runs until <paramref name="ct"/> cancels.
    /// Per-connection exceptions are logged and swallowed; the loop
    /// <see cref="NamedPipeServerStream.Disconnect"/>s between clients so the
    /// next <c>WaitForConnectionAsync</c> accepts on the same server instance.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is null)
            throw new InvalidOperationException(
                "NxmIpcServer is not bound. Call Bind() first and check IsBound before starting the accept loop.");

        var server = _server;
        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ProcessConnectionAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex)
            {
                // Accept or transport-level failure on this connection. Log and
                // continue: one bad client must not kill the server.
                _logger.LogWarning(ex, "nxm IPC connection error on '{Pipe}'; continuing accept loop.", _pipeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing an nxm IPC connection; continuing accept loop.");
            }
            finally
            {
                // Disconnect the current client so the next WaitForConnectionAsync
                // accepts a fresh one on the SAME server instance. IsConnected
                // guards the case where WaitForConnection itself threw before a
                // client was accepted. Disconnect (not Dispose) keeps the pipe
                // name claimed for the app's lifetime.
                try
                {
                    if (server.IsConnected)
                        server.Disconnect();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to disconnect the nxm IPC client (best-effort).");
                }
            }
        }
    }

    private async Task ProcessConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        string? url;
        try
        {
            url = await NxmIpcFraming.ReadUrlAsync(server, ct).ConfigureAwait(false);
        }
        catch (NxmIpcFramingException ex)
        {
            _logger.LogWarning(ex, "Malformed nxm IPC frame; ignoring connection.");
            return;
        }

        if (url is null)
        {
            // Clean close, no message. Nothing to do.
            return;
        }

        _logger.LogDebug("Received nxm URL via IPC: {Url}", url);
        await _router.RouteAsync(url, ct).ConfigureAwait(false);
    }

    private static NamedPipeServerStream DefaultCreateServerStream(string pipeName)
        => new(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
               PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private bool _disposed;

    /// <summary>
    /// Disposes the server stream and marks the server disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _server?.Dispose();
        _server = null;
    }

    /// <summary>
    /// Asynchronously disposes the server stream and marks the server disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }
}

/// <summary>
/// Raised when another Curator process is already running. Thrown by
/// <see cref="SingleInstanceGuard.EnsureOnlyInstance"/> (called from
/// <see cref="NxmIpcServer.Bind"/>) when process enumeration finds a live Curator
/// with the current process's name. The composition root catches this and exits
/// cleanly before showing the main window (single-instance enforcement).
/// </summary>
public sealed class NxmSingleInstanceException : Exception
{
    /// <summary>The IPC pipe name carried as context (single-instance is detected via process enumeration in <see cref="SingleInstanceGuard"/>, not via the pipe).</summary>
    public string PipeName { get; }

    public NxmSingleInstanceException(string pipeName, Exception inner)
        : base($"Another Curator instance is already running.", inner)
        => PipeName = pipeName;
}
