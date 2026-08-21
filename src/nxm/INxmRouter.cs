namespace Modificus.Curator.Nxm;

/// <summary>
/// Parses a raw <c>nxm://</c> URL and dispatches it to the appropriate handler.
/// The IPC server delegates every incoming connection to the router; the router
/// owns URL semantics (parse, classify, dispatch) and catches handler failures
/// at the boundary so a single bad handler invocation cannot kill the accept
/// loop.
/// </summary>
public interface INxmRouter
{
    /// <summary>
    /// Parses <paramref name="rawUrl"/> and dispatches to the resolved mod-download
    /// handler. Collection URLs, OAuth-callback URLs (Curator OAuth uses loopback,
    /// so these are not routed to a handler), and unparseable URLs are logged and
    /// dropped. Handler exceptions are caught and logged. Must return promptly:
    /// routing runs inline on the IPC accept loop, and request processing is the
    /// routed handler's responsibility, not work to await inline.
    /// </summary>
    Task RouteAsync(string rawUrl, CancellationToken ct = default);
}
