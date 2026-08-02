using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The Nexus OAuth token store + the loopback login orchestrator. Owns the
/// <see cref="OidcClient"/> + the live token persistence. Implements both
/// <see cref="INexusTokenStore"/> (the small read-only view the OAuth message
/// factory depends on) and the larger OAuth-session surface (login + sign-out
/// of OAuth tokens) the auth service uses.
/// </summary>
/// <remarks>
/// <para>
/// Splitting this out of <see cref="NexusAuthService"/> breaks the DI cycle
/// (AuthService -> <see cref="INexusClient"/> -> the auth message factory ->
/// <see cref="INexusTokenStore"/>; if AuthService itself were the token store,
/// that would be a construction-time cycle). The store + the service both depend
/// on the live config; nothing depends on the service here, so the cycle is
/// broken.</para>
/// <para>
/// <b>Never holds a cached config or cached tokens.</b> Each read goes through
/// <see cref="IConfigLoader"/>; the file is tiny, and live-read matches the
/// established pattern (#31).</para>
/// <para>
/// <b>OidcClient is constructed fresh per login/refresh call</b> from the live
/// config (the authority + client id + scope are cheap to set; a cached instance
/// would freeze them at first use, so a config change mid-session would not take
/// effect).</para>
/// </remarks>
public sealed class NexusOAuthTokenStore : INexusTokenStore
{
    private readonly IConfigLoader _configLoader;
    private readonly IBrowser _browser;
    private readonly ILogger<NexusOAuthTokenStore> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public NexusOAuthTokenStore(
        IConfigLoader configLoader,
        IBrowser browser,
        ILogger<NexusOAuthTokenStore> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Test seam: an optional callback invoked after the production
    /// <see cref="OidcClientOptions"/> are built, before the
    /// <see cref="OidcClient"/> is constructed. Tests use this to inject a
    /// <c>BackchannelHandler</c> (a stub <see cref="HttpMessageHandler"/>) so
    /// discovery + token calls run offline. Production leaves this null.
    /// </summary>
    internal Action<OidcClientOptions>? ConfigureOidcOptions { get; set; }

    /// <inheritdoc />
    public NexusOAuthTokens? GetOAuthTokens() => _configLoader.Load().Integrations.Nexus.OAuth;

    /// <inheritdoc />
    public async Task<NexusOAuthTokens?> RefreshAsync(CancellationToken ct)
    {
        // Serialize concurrent refreshes (the OAuth message factory's
        // OnUnauthorizedAsync may fire concurrently for in-flight requests).
        // The first caller performs the refresh + persists; subsequent callers
        // re-read the now-current token + skip the network call when the token
        // has changed.
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _configLoader.Load().Integrations.Nexus.OAuth;
            if (current?.RefreshToken is null)
            {
                _logger.LogWarning(
                    "Nexus OAuth refresh requested but no refresh token is persisted; re-login required.");
                return null;
            }

            var oidcClient = new OidcClient(BuildOidcOptions());
            var result = await oidcClient.RefreshTokenAsync(current.RefreshToken, cancellationToken: ct)
                .ConfigureAwait(false);
            if (result.IsError)
            {
                _logger.LogWarning(
                    "Nexus OAuth refresh failed: {Error}. Re-login required.", result.Error);
                return null;
            }

            var refreshed = new NexusOAuthTokens(
                result.AccessToken,
                result.RefreshToken ?? current.RefreshToken,
                /* scope */ current.Scope,
                result.AccessTokenExpiration);
            SaveOAuthTokens(refreshed);
            return refreshed;
        }
        catch (Exception ex)
        {
            // A network failure, discovery failure, or other exception during
            // refresh. Surface as "refresh failed"; the caller prompts re-login.
            _logger.LogWarning(ex, "Nexus OAuth refresh threw; re-login required.");
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---- OAuth-session surface (the auth service's view) -------------------

    /// <summary>
    /// Runs the OAuth loopback login flow: opens the browser, awaits the
    /// callback, exchanges the authorization code for tokens. Returns the new
    /// tokens (NOT yet persisted; the caller persists via
    /// <see cref="SaveOAuthTokens"/>). Returns <c>null</c> on failure (browser
    /// error, timeout, consent denied, token exchange failure).
    /// </summary>
    public async Task<NexusOAuthTokens?> LoginAsync(CancellationToken ct)
    {
        try
        {
            var oidcClient = new OidcClient(BuildOidcOptions());
            var login = await oidcClient.LoginAsync(new LoginRequest(), ct).ConfigureAwait(false);
            if (login.IsError)
            {
                _logger.LogWarning("Nexus OAuth login failed: {Error}", login.Error);
                return null;
            }

            return new NexusOAuthTokens(
                login.AccessToken,
                login.RefreshToken,
                NexusOAuthConstants.Scope,
                login.AccessTokenExpiration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus OAuth login threw.");
            return null;
        }
    }

    /// <summary>
    /// Persists the supplied OAuth tokens to the live config (read-modify-save:
    /// load, overwrite OAuth, save). Used by the auth service after a successful
    /// login to flip <c>AuthMethod</c> + clear any API key in one write.
    /// </summary>
    public void SaveOAuthTokens(NexusOAuthTokens tokens)
    {
        var config = _configLoader.Load();
        config.Integrations.Nexus.OAuth = tokens;
        _configLoader.Save(config);
    }

    /// <summary>
    /// Clears any persisted OAuth tokens from the live config (read-modify-save
    /// with a null write). Idempotent.
    /// </summary>
    public void ClearOAuthTokens()
    {
        var config = _configLoader.Load();
        config.Integrations.Nexus.OAuth = null;
        _configLoader.Save(config);
    }

    // ---- OidcClient configuration ------------------------------------------

    private OidcClientOptions BuildOidcOptions()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        // Authority must be the OIDC ISSUER root (https://users.nexusmods.com),
        // NOT the /oauth path. OidcClient resolves discovery at
        // <Authority>/.well-known/openid-configuration + reads the authorize /
        // token / jwks endpoints out of the doc; the issuer root is the path
        // Nexus serves discovery at. Pointing Authority at the /oauth path 404s
        // the discovery fetch (the bug fixed here).
        var authority = NormalizeOAuthBaseUrl(nexus.OAuthBaseUrl);

        var options = new OidcClientOptions
        {
            Authority = authority,
            ClientId = NexusOAuthConstants.ClientId,
            // Nexus accepts this client as a public client, so no client secret
            // is sent. The client_id is posted in the token request body
            // (TokenClientCredentialStyle = PostBody), which is OidcClient's
            // default with no ClientSecret set. PKCE S256 protects the authorize
            // leg. (Nexus's discovery doc still lists only client_secret_basic /
            // client_secret_post under token_endpoint_auth_methods_supported,
            // but the token endpoint accepts the no-secret request for this
            // client.)
            TokenClientCredentialStyle = ClientCredentialStyle.PostBody,
            RedirectUri = ResolveBrowserRedirectUri(),
            Scope = NexusOAuthConstants.Scope,
            Browser = _browser,
            // Curator reads the display name + Premium state from the access
            // token's JWT payload (Nexus embeds them in a `user` claim), so
            // OidcClient's built-in userinfo profile load is disabled.
            LoadProfile = false,
        };

        // Test seam: tests inject a BackchannelHandler here so discovery + token
        // calls run offline against a stub handler. Production leaves
        // ConfigureOidcOptions null.
        ConfigureOidcOptions?.Invoke(options);

        return options;
    }

    /// <summary>
    /// Resolves the loopback browser's redirect URI. The LoopbackBrowser
    /// pre-grabs the port in its constructor; tests inject a fake IBrowser that
    /// does not, in which case a fixed loopback URI is used (the redirect_uri
    /// value matters only for OidcClient's authorize-URL construction in tests;
    /// no listener actually binds).
    /// </summary>
    private string ResolveBrowserRedirectUri() =>
        _browser is LoopbackBrowser lb
            ? lb.RedirectUri
            : $"http://127.0.0.1:0{NexusOAuthConstants.CallbackPath}";

    private static string NormalizeOAuthBaseUrl(string? baseUrl)
    {
        // Strip a trailing /oauth so a user (reasonably) pointing OAuthBaseUrl
        // at the OAuth path lands on the canonical issuer root, not at
        // https://users.nexusmods.com/oauth (which 404s discovery).
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        const string OauthSuffix = "/oauth";
        if (trimmed.EndsWith(OauthSuffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^OauthSuffix.Length];
        }
        return trimmed.Length == 0 ? "https://users.nexusmods.com" : trimmed;
    }
}

/// <summary>
/// The Nexus auth orchestrator: the OAuth login + the API-key validate + sign
/// out, plus the current-auth-state read for the Integrations dialog's status
/// line. Delegates OAuth token storage + refresh to
/// <see cref="NexusOAuthTokenStore"/> (the smaller surface the OAuth message
/// factory depends on, breaking the DI cycle).
/// </summary>
/// <remarks>
/// <para>
/// <b>AuthMethod is the user's explicit choice, set by the Integrations
/// dialog.</b> <see cref="LoginWithOAuthAsync"/> sets <c>AuthMethod = OAuth</c>;
/// <see cref="LoginWithApiKeyAsync"/> sets <c>AuthMethod = ApiKey</c>;
/// <see cref="SignOutAsync"/> sets <c>AuthMethod = None</c>. Switching methods
/// clears the OTHER method's credentials (clean transition, no leftovers).</para>
/// <para>
/// <b>No fallback.</b> If the selected method fails (OAuth login cancelled or
/// refresh token revoked, API key invalid), the service surfaces the error for
/// THAT method; it does not silently try the other.</para>
/// </remarks>
public interface INexusAuthService
{
    /// <summary>
    /// Raised whenever an auth action changes the persisted
    /// <see cref="NexusAuthMethod"/> (OAuth login, API-key validate, or
    /// sign-out). Carries no payload; a subscriber re-reads what it needs from
    /// <see cref="GetCurrentStateAsync"/> or the live config. Signals persisted
    /// auth-method changes for any consumer that needs to react to them; there
    /// is currently no production subscriber (the DMF prompt was the last one
    /// and is now profile-creation-only).
    /// </summary>
    /// <remarks>
    /// Fires from inside the auth call, so a subscriber still in the call
    /// chain (e.g. a dialog driving the auth action) sees it synchronously.
    /// </remarks>
    event EventHandler? AuthStateChanged;

    /// <summary>Runs the OAuth loopback login flow (browser + token exchange +
    /// persist + display-name + Premium resolution from the access token's JWT
    /// payload). Returns the user-facing summary.</summary>
    Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default);

    /// <summary>Validates the supplied API key + sets <c>AuthMethod = ApiKey</c>
    /// on success. Speculative-write + revert-on-failure. Returns the
    /// user-facing summary.</summary>
    Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>Signs out: clears OAuth tokens + API key + sets
    /// <c>AuthMethod = None</c>. Idempotent.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>The current auth state for the Integrations dialog's status
    /// line. Returns <c>null</c> when <c>AuthMethod == None</c>.</summary>
    Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="INexusAuthService"/>. The OAuth login + API-key validate +
/// sign-out orchestrator. Delegates OAuth token storage + refresh to the
/// injected <see cref="NexusOAuthTokenStore"/>.
/// </summary>
public sealed class NexusAuthService : INexusAuthService
{
    private readonly IConfigLoader _configLoader;
    private readonly INexusClient _client;
    private readonly NexusOAuthTokenStore _tokens;
    private readonly ILogger<NexusAuthService> _logger;

    /// <inheritdoc />
    public event EventHandler? AuthStateChanged;

    public NexusAuthService(
        IConfigLoader configLoader,
        INexusClient client,
        NexusOAuthTokenStore tokens,
        ILogger<NexusAuthService> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the OAuth loopback login flow: delegates to
    /// <see cref="NexusOAuthTokenStore.LoginAsync"/> for the browser + token
    /// exchange, persists the tokens + flips <c>AuthMethod = OAuth</c> (clearing
    /// any API key), then reads the user's display name + Premium state from
    /// the access token's JWT payload. Returns the user-facing summary (or an
    /// error).
    /// </summary>
    public async Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default)
    {
        var tokens = await _tokens.LoginAsync(ct).ConfigureAwait(false);
        if (tokens is null)
        {
            return NexusAuthResult.Failed("Nexus OAuth login failed. Try again.");
        }

        // Persist tokens + flip AuthMethod (clearing any API key) in one write.
        var config = _configLoader.Load();
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        config.Integrations.Nexus.OAuth = tokens;
        config.Integrations.Nexus.ApiKey = null;
        _configLoader.Save(config);

        // Notify subscribers that the persisted auth state changed. Raised
        // synchronously.
        AuthStateChanged?.Invoke(this, EventArgs.Empty);

        // Read the display name + Premium state from the access token's JWT
        // payload (Nexus embeds them in a `user` claim; the /oauth/userinfo
        // endpoint returns only `sub` for this client). Pure in-memory parse,
        // so a null/failed parse is non-fatal: the user IS signed in, we just
        // don't know their display name.
        var claims = NexusAccessTokenClaims.TryParse(tokens.AccessToken);
        string? name = claims?.Username;
        bool? premium = claims?.IsPremium;

        _logger.LogInformation("Nexus OAuth login succeeded for user {Name}.", name ?? "(unknown)");
        return NexusAuthResult.Success(name, premium);
    }

    /// <summary>
    /// Validates the supplied API key + sets <c>AuthMethod = ApiKey</c> on
    /// success, clearing any persisted OAuth tokens. Speculative-write +
    /// revert-on-failure: writes the key + AuthMethod first so the v1 client's
    /// auth factory picks it up for the validate call, then reverts if the
    /// validate fails. Returns the user-facing summary (or an error).
    /// </summary>
    public async Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return NexusAuthResult.Failed("API key is empty.");
        }

        // Speculative write: persist the key + AuthMethod=ApiKey so the v1
        // client's auth factory uses it on the validate call. Save the prior
        // state so we can revert on failure.
        //
        // Race note (bounded today): this read-modify-write
        // is NOT under a lock, so a concurrent OAuth refresh landing between the
        // speculative write + the revert would have its freshly-refreshed tokens
        // clobbered by the revert (restoring priorOAuth from before the
        // speculative write). The OAuth refresh path lives in NexusOAuthTokenStore
        // (its own _refreshGate), not here, so a lock in this method alone would
        // not close the race. Today this is safe because Curator is single-instance
        // and the Integrations dialog disables the auth controls while a login
        // is in flight (no concurrent refresh can land). A concurrent refresh
        // triggered elsewhere (e.g., the update-check on profile load) makes the
        // race plausible; revisit if that becomes a concern (likely a shared
        // auth-state lock around this method + NexusOAuthTokenStore.RefreshAsync).
        var prior = _configLoader.Load().Integrations.Nexus;
        var priorMethod = prior.AuthMethod;
        var priorKey = prior.ApiKey;
        var priorOAuth = prior.OAuth;

        var config = _configLoader.Load();
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        config.Integrations.Nexus.ApiKey = apiKey.Trim();
        // Switching methods clears the other method's credentials.
        config.Integrations.Nexus.OAuth = null;
        _configLoader.Save(config);

        try
        {
            var validate = await _client.ValidateAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Nexus API-key login succeeded for user {Name} (premium={Premium}).",
                validate.Data.Name,
                validate.Data.IsPremium);
            // Notify subscribers the persisted auth state changed.
            AuthStateChanged?.Invoke(this, EventArgs.Empty);
            return NexusAuthResult.Success(validate.Data.Name, validate.Data.IsPremium);
        }
        catch (Exception ex)
        {
            // Revert: the new key was rejected. Restore the prior method + its
            // credentials so the user's session is unchanged.
            var reverted = _configLoader.Load();
            reverted.Integrations.Nexus.AuthMethod = priorMethod;
            reverted.Integrations.Nexus.ApiKey = priorKey;
            reverted.Integrations.Nexus.OAuth = priorOAuth;
            _configLoader.Save(reverted);
            _logger.LogWarning(ex, "Nexus API-key login failed; reverted to the prior auth state.");
            // Still notify: the speculative write + revert are both visible
            // state changes.
            AuthStateChanged?.Invoke(this, EventArgs.Empty);
            return NexusAuthResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Signs out: clears OAuth tokens + API key + sets
    /// <c>AuthMethod = None</c>. Idempotent (sign-out when already signed out is
    /// a no-op).
    /// </summary>
    public Task SignOutAsync(CancellationToken ct = default)
    {
        var config = _configLoader.Load();
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.None;
        config.Integrations.Nexus.ApiKey = null;
        config.Integrations.Nexus.OAuth = null;
        _configLoader.Save(config);
        _logger.LogInformation("Nexus auth cleared (signed out).");
        // Notify any subscriber that the persisted auth state changed. Raised
        // synchronously.
        AuthStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The current auth state, for the Integrations dialog's status line. For
    /// OAuth, reads the display name + premium from the access token's JWT
    /// payload (pure in-memory parse, no API call). For API key, hits the v1
    /// <c>validate</c> endpoint to resolve them (one network call per open).
    /// Returns <c>null</c> when <c>AuthMethod == None</c>. On an API-key
    /// network/auth failure, returns a "signed in (couldn't verify)" state
    /// rather than throwing, so the dialog can still render.
    /// </summary>
    public async Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default)
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        if (nexus.AuthMethod == NexusAuthMethod.None)
        {
            return null;
        }

        if (nexus.AuthMethod == NexusAuthMethod.OAuth)
        {
            // Read the access token + parse the user claim out of its JWT
            // payload. No API call: the access token carries the username +
            // membership roles. A null/failed parse yields the unverified
            // state (matches the API-key catch-block degradation).
            var claims = NexusAccessTokenClaims.TryParse(_tokens.GetOAuthTokens()?.AccessToken);
            return new NexusAuthState(nexus.AuthMethod, claims?.Username, claims?.IsPremium);
        }

        try
        {
            var validate = await _client.ValidateAsync(ct).ConfigureAwait(false);
            return new NexusAuthState(nexus.AuthMethod, validate.Data.Name, validate.Data.IsPremium, nexus.ApiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Nexus auth state check failed; the Integrations dialog will show the unverified state.");
            // The user is configured-authenticated but we couldn't verify. Show
            // the configured method with a null name; the dialog renders a
            // generic "signed in" string. Carry the API key (when configured)
            // so the dialog can still show the masked-key indicator + re-validate.
            return new NexusAuthState(nexus.AuthMethod, null, null, nexus.ApiKey);
        }
    }
}

/// <summary>
/// The result of an Integrations-dialog auth action (OAuth login or API-key
/// validate). Carries the resolved display name + premium state on success, or
/// a user-facing error message on failure.
/// </summary>
public sealed record NexusAuthResult
{
    /// <summary>Whether the action succeeded.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// The user's display name on success, or <c>null</c> on failure (or when
    /// the post-login user-info fetch failed but the auth itself succeeded).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Whether the user has any premium role. <c>null</c> when unknown (the
    /// user-info fetch failed but the auth itself succeeded).
    /// </summary>
    public bool? IsPremium { get; init; }

    /// <summary>The user-facing error message on failure. <c>null</c> on
    /// success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Builds a success result.</summary>
    public static NexusAuthResult Success(string? name, bool? isPremium) => new()
    {
        IsSuccess = true,
        Name = name,
        IsPremium = isPremium,
    };

    /// <summary>Builds a failure result.</summary>
    public static NexusAuthResult Failed(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
    };
}

/// <summary>
/// The current Nexus auth state for the Integrations dialog's status line. The
/// <see cref="Name"/> + <see cref="IsPremium"/> may be <c>null</c> when the
/// verify call failed but the configured auth method is non-<c>None</c>.
/// <see cref="ApiKey"/> carries the persisted API key when
/// <see cref="Method"/> is <see cref="NexusAuthMethod.ApiKey"/> (so the dialog
/// can show the masked key + re-validate without re-entering); <c>null</c>
/// otherwise. It never leaves the in-process boundary between the auth service
/// + the Integrations dialog VM.
/// </summary>
public sealed record NexusAuthState(
    NexusAuthMethod Method,
    string? Name,
    bool? IsPremium,
    string? ApiKey = null);
