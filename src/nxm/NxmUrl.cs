namespace Modificus.Curator.Nxm;

/// <summary>
/// A parsed <c>nxm://</c> URL. The base type carries the raw URL string; the
/// sealed derived records carry the fields relevant to each URL kind. Parsed by
/// <see cref="NxmUrlParser"/>, dispatched on by <see cref="INxmRouter"/>.
/// </summary>
/// <remarks>
/// Three URL kinds are recognized: mod downloads, OAuth-callback shapes (parsed
/// so the router can route them, but no longer dispatched to a handler, see
/// <see cref="NxmOAuthCallbackUrl"/>), and collections (parsed but not routed to
/// a handler in v1). Anything that does not match one of these shapes fails to
/// parse and is dropped by the router with a warning.
/// </remarks>
public abstract record NxmUrl
{
    /// <summary>The verbatim URL string as received over IPC (or from a test).</summary>
    public string Raw { get; init; }

    private protected NxmUrl(string raw) => Raw = raw;
}

/// <summary>
/// A mod-download URL: <c>nxm://&lt;game&gt;/mods/&lt;modId&gt;/files/&lt;fileId&gt;</c>
/// with optional query parameters <c>key</c>, <c>expires</c> (epoch seconds),
/// and <c>user_id</c>. The "Mod manager download" button on a Nexus Mods file
/// page produces one of these.
/// </summary>
/// <param name="Raw">The verbatim URL string.</param>
/// <param name="Game">The Nexus game domain (e.g. the Darktide domain in
/// <c>NexusGameIdentity.DarktideDomain</c>).</param>
/// <param name="ModId">The numeric mod id (positive).</param>
/// <param name="FileId">The numeric file id (positive).</param>
/// <param name="Key">The download key, or null when absent / empty.</param>
/// <param name="Expires">The expiry epoch seconds, or null when absent / non-numeric.</param>
/// <param name="UserId">The user id, or null when absent / non-numeric.</param>
public sealed record NxmModDownloadUrl(
    string Raw,
    string Game,
    int ModId,
    int FileId,
    string? Key,
    long? Expires,
    long? UserId) : NxmUrl(Raw);

/// <summary>
/// An OAuth callback URL: <c>nxm://oauth/callback?code=&lt;code&gt;&amp;state=&lt;state&gt;</c>.
/// Kept as a parsed type so <see cref="NxmUrlParser"/> continues to recognize
/// the shape (rather than classifying it as unknown). The router logs + drops
/// these: Nexus OAuth in Curator uses a loopback HTTP redirect (RFC 8252),
/// independent of the <c>nxm://</c> handler, so this URL kind is never actually
/// delivered over the IPC pipe in normal operation.
/// </summary>
/// <param name="Raw">The verbatim URL string.</param>
/// <param name="Code">The authorization code (required, non-empty).</param>
/// <param name="State">The OAuth state token (required, non-empty).</param>
public sealed record NxmOAuthCallbackUrl(
    string Raw,
    string Code,
    string State) : NxmUrl(Raw);

/// <summary>
/// A collection URL: <c>nxm://&lt;game&gt;/collections/&lt;id&gt;/revisions/&lt;rev&gt;</c>.
/// Collections parse to a distinct type so the router can recognize them and log
/// "unsupported in v1" rather than "unknown URL". No handler is invoked.
/// </summary>
/// <param name="Raw">The verbatim URL string.</param>
/// <param name="Game">The Nexus game domain.</param>
/// <param name="CollectionId">The collection slug / id.</param>
/// <param name="Revision">The positive revision number.</param>
public sealed record NxmCollectionUrl(
    string Raw,
    string Game,
    string CollectionId,
    int Revision) : NxmUrl(Raw);
