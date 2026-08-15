using System.Net;
using System.Net.Sockets;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.General;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The loopback <see cref="IBrowser"/> for the Nexus OAuth flow. Pre-grabs an
/// ephemeral loopback port (exposed via <see cref="RedirectUri"/> so the caller
/// can set it on <c>OidcClientOptions.RedirectUri</c> before OidcClient builds
/// the authorize URL), then in <see cref="InvokeAsync"/> binds an
/// <see cref="HttpListener"/> on that port, opens the user's default browser at
/// the authorize URL OidcClient built, and awaits the redirect.
/// </summary>
/// <remarks>
/// <para>
/// This is the RFC 8252 native-app loopback pattern: the user's browser is the
/// only consent surface, and the flow is INDEPENDENT of the
/// <c>nxm://</c> scheme handler, which is not involved in OAuth.</para>
/// <para>
/// <b>Two-phase construction (matches the OidcClient sample pattern).</b>
/// <list type="bullet">
/// <item><term>Constructor</term><description>Grabs an ephemeral loopback port
/// (one TcpListener bind + close) so <see cref="RedirectUri"/> is available
/// immediately. The caller sets it on <c>OidcClientOptions.RedirectUri</c>
/// before constructing <c>OidcClient</c>, so the authorize URL OidcClient
/// builds carries this redirect_uri.</description></item>
/// <item><term><see cref="InvokeAsync"/></term><description>Binds an
/// <see cref="HttpListener"/> on the pre-grabbed port, opens the browser at the
/// supplied authorize URL (which has the redirect_uri baked in), waits for the
/// callback, returns the authorization response. Stops the listener on
/// exit.</description></item>
/// </list>
/// The tiny race (the port could be taken between the grab and the bind) is
/// vanishingly rare; a bind failure surfaces to the OAuth flow as
/// <see cref="BrowserResultType.UnknownError"/>, which the service maps to a
/// user-visible "Login failed". Not worth a retry loop in v1.</para>
/// <para>
/// <b>Browser launch goes through <see cref="IExternalLauncher"/>.</b> The
/// shell-open (the OS default browser) is the shared General-library launcher;
/// a launch that could not start maps to
/// <see cref="BrowserResultType.UnknownError"/> (the flow cannot proceed
/// without a browser), as does any other exception thrown during
/// <see cref="InvokeAsync"/>.</para>
/// <para>
/// <b>Testability.</b> The <see cref="IBrowser"/> seam is the production
/// boundary; OAuth-flow tests inject a fake <see cref="IBrowser"/> that returns
/// a preset authorization response (no real listener). The
/// <see cref="HttpListenerLoopbackListener"/> is independently testable against
/// an ephemeral port; the LoopbackBrowser tests inject a fake
/// <see cref="IExternalLauncher"/> and hit the real listener with an
/// <c>HttpClient</c>.</para>
/// </remarks>
internal sealed class LoopbackBrowser : IBrowser
{
    private readonly TimeSpan _timeout;
    private readonly Func<LoopbackListenerOptions, ILoopbackListener> _createListener;
    private readonly IExternalLauncher _externalLauncher;

    /// <summary>
    /// The loopback redirect URI (<c>http://127.0.0.1:&lt;port&gt;/callback</c>)
    /// for this browser instance. Set in the constructor (the port is grabbed
    /// eagerly so the caller can pass this URI to
    /// <c>OidcClientOptions.RedirectUri</c> before OidcClient builds the
    /// authorize URL).
    /// </summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Creates the loopback browser with the default 3-minute flow timeout +
    /// the production <see cref="HttpListener"/> factory + the shared OS
    /// shell-open launcher. Pre-grabs the ephemeral port.
    /// </summary>
    public LoopbackBrowser(IExternalLauncher externalLauncher)
        : this(NexusOAuthConstants.DefaultFlowTimeout, CreateHttpListener, externalLauncher)
    {
    }

    /// <summary>
    /// Creates the loopback browser with explicit seams + pre-grabs the ephemeral
    /// port. Production uses the defaults; tests inject fakes for the listener
    /// factory + the external launcher so the OAuth flow runs entirely offline.
    /// </summary>
    /// <param name="timeout">How long to wait for the user to complete the
    /// browser consent. On expiry, returns
    /// <see cref="BrowserResultType.Timeout"/>.</param>
    /// <param name="createListener">Factory that builds the loopback listener
    /// for the given options (port + path). Production wires
    /// <see cref="CreateHttpListener"/>; tests inject a fake.</param>
    /// <param name="externalLauncher">Opens the authorize URL OidcClient built
    /// in the user's default browser. Production wires the shared
    /// <see cref="IExternalLauncher"/>; tests inject a fake.</param>
    internal LoopbackBrowser(
        TimeSpan timeout,
        Func<LoopbackListenerOptions, ILoopbackListener> createListener,
        IExternalLauncher externalLauncher)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive");
        }
        _timeout = timeout;
        _createListener = createListener ?? throw new ArgumentNullException(nameof(createListener));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));

        // Pre-grab the port + build the redirect URI. The HttpListener itself
        // binds in InvokeAsync (so the listener is alive only while a flow is
        // in flight).
        var port = EphemeralPortGrab();
        RedirectUri = $"http://127.0.0.1:{port}{NexusOAuthConstants.CallbackPath}";
    }

    /// <inheritdoc />
    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        // The listener binds on the same port the constructor pre-grabbed (the
        // authorize URL in options.StartUrl already carries this redirect_uri).
        var port = new Uri(RedirectUri).Port;
        var listenerOpts = new LoopbackListenerOptions(port, NexusOAuthConstants.CallbackPath);
        var listener = _createListener(listenerOpts);
        listener.Start();

        try
        {
            // A browser launch that could not start (false) or threw lands in
            // the catch below as UnknownError; both mean the flow cannot
            // proceed without a browser.
            if (!_externalLauncher.OpenUri(new Uri(options.StartUrl)))
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UnknownError,
                    Error = "Could not open the user's default browser.",
                };
            }

            // Combine the flow timeout + the caller's cancellation token. The
            // listener returns null on timeout (no callback arrived in time) or
            // on cancellation.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_timeout);

            var callback = await listener.WaitForCallbackAsync(linkedCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(callback))
            {
                // Distinguish timeout from caller-cancel by checking the caller's
                // token. A caller-cancel maps to UserCancel; the internal
                // timeout maps to Timeout.
                var resultType = cancellationToken.IsCancellationRequested
                    ? BrowserResultType.UserCancel
                    : BrowserResultType.Timeout;
                return new BrowserResult
                {
                    ResultType = resultType,
                    Error = resultType == BrowserResultType.UserCancel
                        ? "OAuth login cancelled."
                        : "OAuth login timed out waiting for the browser callback.",
                };
            }

            return new BrowserResult
            {
                Response = callback,
                ResultType = BrowserResultType.Success,
            };
        }
        catch (Exception ex)
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = ex.Message,
            };
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---- production listener factory ---------------------------------------

    /// <summary>
    /// Builds the production <see cref="HttpListener"/>-backed
    /// <see cref="ILoopbackListener"/>. The listener binds
    /// <c>http://127.0.0.1:&lt;port&gt;/&lt;path&gt;/</c> (loopback only).
    /// </summary>
    private static ILoopbackListener CreateHttpListener(LoopbackListenerOptions options)
        => new HttpListenerLoopbackListener(options);

    /// <summary>
    /// Reserves an ephemeral loopback port via a one-shot TcpListener, then
    /// releases it so the HttpListener can bind it on the same port during
    /// <see cref="InvokeAsync"/>. The standard .NET pattern for "give me any
    /// free port."
    /// </summary>
    private static int EphemeralPortGrab()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }
        finally
        {
            tcp.Stop();
        }
    }
}

/// <summary>
/// Options for the loopback listener: the port (pre-grabbed) + the path. Carried
/// as a record so the factory delegate signature is stable + test-side options
/// are clear.
/// </summary>
internal sealed record LoopbackListenerOptions(int Port, string Path);

/// <summary>
/// The loopback listener abstraction. Production uses
/// <see cref="HttpListenerLoopbackListener"/>; tests inject a fake that returns
/// a preset callback (no real socket). The listener binds + waits + stops; it
/// does NOT open the browser (that is <see cref="LoopbackBrowser"/>'s job).
/// </summary>
internal interface ILoopbackListener
{
    /// <summary>The absolute redirect URI the listener is bound on (carries the
    /// OS-assigned port). Available once <see cref="Start"/> has bound.</summary>
    Uri RedirectUri { get; }

    /// <summary>Binds the listener on its configured port + path.</summary>
    void Start();

    /// <summary>
    /// Waits for the OAuth redirect. Returns the callback's query string (the
    /// authorization response OidcClient parses), or <c>null</c> on cancellation
    /// / timeout.
    /// </summary>
    Task<string?> WaitForCallbackAsync(CancellationToken ct);

    /// <summary>Stops the listener + releases the socket. Safe to call after
    /// <see cref="Start"/> or after a failed start.</summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// The production <see cref="ILoopbackListener"/> backed by an in-box
/// <see cref="HttpListener"/>. Binds loopback on the supplied port; serves a
/// minimal "you can return to the app" page to the browser on callback; returns
/// the callback's query string to the caller via a
/// <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
/// <remarks>
/// The listener is intentionally minimal: it accepts exactly one request (the
/// OAuth redirect) and returns the query string. It does NOT validate the
/// <c>state</c> parameter here (OidcClient does that). It does NOT close the
/// response stream until the page is fully written, so the browser shows the
/// "return to app" message before the listener stops.
/// </remarks>
internal sealed class HttpListenerLoopbackListener : ILoopbackListener
{
    private readonly LoopbackListenerOptions _options;
    private HttpListener? _listener;
    private readonly TaskCompletionSource<string?> _callback =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HttpListenerLoopbackListener(LoopbackListenerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Uri RedirectUri
    {
        get
        {
            if (_listener is null)
            {
                throw new InvalidOperationException("Listener has not been started.");
            }
            var path = _options.Path.TrimStart('/');
            return new Uri($"http://127.0.0.1:{_options.Port}/{path}");
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        var path = _options.Path.TrimStart('/');
        var prefix = $"http://127.0.0.1:{_options.Port}/{path}/";
        _listener = new HttpListener { Prefixes = { prefix } };
        _listener.Start();

        // Accept exactly one request (the OAuth redirect). The accept runs on a
        // background task so Start returns immediately + the caller can open the
        // browser while the listener waits.
        _ = Task.Run(AcceptSingleRequestAsync);
    }

    /// <inheritdoc />
    public Task<string?> WaitForCallbackAsync(CancellationToken ct)
    {
        // Wire the cancellation: when the caller cancels (timeout / user
        // cancel), resolve the TCS with null so the await completes promptly.
        ct.Register(() => _callback.TrySetResult(null));
        return _callback.Task;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        _callback.TrySetResult(null);
        var listener = _listener;
        _listener = null;
        if (listener is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
        catch (HttpListenerException)
        {
            // Best-effort shutdown; the listener is going away.
        }

        return Task.CompletedTask;
    }

    private async Task AcceptSingleRequestAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Listener stopped before a request arrived (timeout / cancel). The
            // TCS is already resolved via StopAsync / the cancel registration.
            return;
        }

        try
        {
            // Respond to the browser with a minimal page so the user sees
            // something + the connection closes cleanly.
            const string html = "<!doctype html><html><body style=\"font-family:sans-serif;text-align:center;padding:2em\">"
                + "<h2>You can now return to Modificus Curator.</h2>"
                + "<p>It is safe to close this tab.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length))
                .ConfigureAwait(false);
            ctx.Response.Close();

            // The query string (with the leading '?') is what OidcClient parses
            // into the authorization response. Trim a trailing null guard.
            var query = ctx.Request.Url?.Query ?? string.Empty;
            _callback.TrySetResult(query);
        }
        catch (Exception)
        {
            // A failed response write is unrecoverable here; surface as null
            // (timeout-equivalent) so the caller does not hang.
            _callback.TrySetResult(null);
        }
    }
}
