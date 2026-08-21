namespace Modificus.Curator.Nxm;

/// <summary>
/// Handles a routed <c>nxm://</c> mod-download URL (the result of clicking
/// "Mod manager download" on a Nexus Mods file page). A no-op default logs the
/// URL; a real handler downloads via the Nexus client and imports into the
/// unified repository, overriding the default.
/// </summary>
public interface INxmModDownloadHandler
{
    /// <summary>
    /// Handles a parsed mod-download URL. Must return promptly, enqueueing the
    /// download (or refusing it via its gate) rather than working inline: the
    /// call runs inline on the IPC accept loop, so a slow return blocks every
    /// later nxm delivery.
    /// </summary>
    Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default);
}
