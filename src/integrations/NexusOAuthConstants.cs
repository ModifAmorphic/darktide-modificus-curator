namespace Modificus.Curator.Integrations;

/// <summary>
/// Build-time constants for the Nexus OAuth flow. The <c>client_id</c>
/// (<c>modificus_curator</c>) is the SSO slug Nexus issued when Curator was
/// registered for public use; the matching <see cref="ClientSecret"/> is
/// build-injected (see the remarks below).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientId"/> is a build-time const (not config, not an env var):
/// the OAuth client id is public by spec, so it ships as a const here, the same
/// native-client model every desktop OAuth app uses.
/// </para>
/// <para>
/// <see cref="ClientSecret"/> is generated at build time from the
/// <c>NEXUS_CLIENT_SECRET</c> environment variable by the
/// <c>GenerateNexusClientSecret</c> target in this project's <c>.csproj</c>.
/// When the env var is unset (local dev, PR-gate builds, <c>dotnet test</c>),
/// the const is empty and the OAuth token exchange will not succeed in those
/// builds. The release workflow supplies the real value from a GitHub repo
/// secret. This is temporary: Nexus is expected to reclassify Curator as a
/// public client (PKCE, no secret), at which point the secret requirement, the
/// generation target, and the const are removed.
/// </para>
/// </remarks>
internal static partial class NexusOAuthConstants
{
    /// <summary>The OAuth client identifier (the SSO slug Nexus issued), sent on
    /// authorize + token requests.</summary>
    public const string ClientId = "modificus_curator";

    // ClientSecret is generated at build time from the NEXUS_CLIENT_SECRET
    // env var by the GenerateNexusClientSecret target (see the class remarks
    // above). Empty when unset; temporary, pending Nexus dropping the secret
    // requirement for this public client.

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
