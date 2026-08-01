namespace Modificus.Curator.Integrations;

/// <summary>
/// Build-time constants for the Nexus OAuth flow. The <c>client_id</c>
/// (<c>modificus_curator</c>) is the SSO slug Nexus issued when Curator was
/// registered for public use. No client secret is used: Nexus accepts this
/// client as a public client, and PKCE S256 protects the authorize leg.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientId"/> is a build-time const (not config, not an env var):
/// the OAuth client id is public by spec, so it ships as a const here, the same
/// native-client model every desktop OAuth app uses. It is posted in the token
/// request body (<c>TokenClientCredentialStyle = PostBody</c>), which is
/// OidcClient's default with no client secret set.
/// </para>
/// </remarks>
internal static class NexusOAuthConstants
{
    /// <summary>The OAuth client identifier (the SSO slug Nexus issued), sent on
    /// authorize + token requests.</summary>
    public const string ClientId = "modificus_curator";

    /// <summary>
    /// The OAuth/OIDC scope. <c>openid</c> is the OIDC scope OidcClient needs to
    /// issue the id_token. The user's display name + Premium state are read from
    /// the access token's JWT payload, not from userinfo or claim scopes, so no
    /// additional scopes are requested.
    /// </summary>
    public const string Scope = "openid";

    /// <summary>
    /// The OAuth/OIDC protocol version sent in the <c>Protocol-Version</c> header
    /// on every API request (the MO2/NMA convention).
    /// </summary>
    public const string ProtocolVersion = "1.0.0";

    /// <summary>The application name sent in <c>Application-Name</c> + the
    /// <c>User-Agent</c> prefix on every API request.</summary>
    public const string ApplicationName = "Modificus-Curator";

    /// <summary>The application version sent in <c>Application-Version</c> +
    /// the <c>User-Agent</c> on every API request. Derived from the assembly;
    /// falls back to <c>"0.0.0"</c> when the version is unavailable (tests).</summary>
    public static string ApplicationVersion { get; } =
        typeof(NexusOAuthConstants).Assembly.GetName().Version?.ToString(fieldCount: 3) ?? "0.0.0";

    /// <summary>The path appended to the OAuth base URL for the loopback
    /// callback. The loopback listener binds the port; the URL fragment is
    /// fixed.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>The default OAuth flow timeout (matches NMA's
    /// <c>OAuthJob</c>). The user has this long to complete the browser
    /// consent; on expiry the loopback listener stops + the service surfaces a
    /// "Login timed out" error.</summary>
    public static readonly TimeSpan DefaultFlowTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The User-Agent header value, combining the application name +
    /// version (the MO2/NMA convention).</summary>
    public static string UserAgent => $"{ApplicationName}/{ApplicationVersion}";
}
