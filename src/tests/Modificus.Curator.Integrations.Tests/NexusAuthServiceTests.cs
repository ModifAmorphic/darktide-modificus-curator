using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises <see cref="NexusAuthService"/> end-to-end: the OAuth loopback login
/// (driven through OidcClient's full <c>LoginAsync</c> by a <see cref="FakeBrowser"/>
/// that captures the state OidcClient generates + returns a preset authorization
/// code, with stub discovery + token endpoints), the API-key validate flow
/// (speculative-write + revert-on-failure), sign-out, the auth-method switching
/// (clearing the other method's credentials), and 401-reactive refresh via the
/// token store. No real network.
/// </summary>
/// <remarks>
/// <para>
/// The OAuth login path is exercised for both the happy path (success result +
/// persisted tokens + cleared prior API key + JWT claim parse) and the failure
/// paths (<see cref="FakeBrowserMode.Timeout"/> / <see cref="FakeBrowserMode.UserCancel"/>
/// surface a failure result without persisting).</para>
/// <para>
/// The OAuth flow uses OidcClient's <c>BackchannelHandler</c> seam (set via
/// <see cref="NexusOAuthTokenStore.ConfigureOidcOptions"/>) to stub the discovery
/// + token endpoints, so the test runs fully offline. <see cref="FakeBrowser"/>
/// returns a preset authorization response so the loopback listener + browser
/// launch are bypassed entirely.</para>
/// </remarks>
public sealed class NexusAuthServiceTests
{
    // ---- API key login ----------------------------------------------------

    [Fact]
    public async Task LoginWithApiKeyAsync_validates_and_persists_method_and_key()
    {
        // The v1 client's validate call is stubbed to return a 200. The service
        // does a speculative write (key + AuthMethod=ApiKey), validates, then
        // keeps the write on success.
        var stub = new StubHttpMessageHandler(_ =>
            HttpResponses.NexusOk(@"{ ""user_id"": 99, ""name"": ""ApiUser"", ""is_premium"": true }"));
        var (service, _, loader) = BuildService(stub, method: NexusAuthMethod.None);

        var result = await service.LoginWithApiKeyAsync("new-key");

        Assert.True(result.IsSuccess);
        Assert.Equal("ApiUser", result.Name);
        Assert.True(result.IsPremium);
        // Persisted: method + key.
        Assert.Equal(NexusAuthMethod.ApiKey, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Equal("new-key", loader.Config.Integrations.Nexus.ApiKey);
        // Validated against the v1 endpoint.
        var request = Assert.Single(stub.Requests);
        Assert.Equal(new Uri("https://api.nexusmods.com/v1/users/validate.json"), request.RequestUri);
    }

    [Fact]
    public async Task LoginWithApiKeyAsync_clears_prior_oauth_tokens_when_switching_to_apikey()
    {
        // Switching methods clears the OTHER method's credentials: starting
        // from OAuth, validating an API key clears the OAuth tokens.
        var stub = new StubHttpMessageHandler(_ => HttpResponses.NexusOk(@"{ ""user_id"": 1, ""name"": ""U"" }"));
        var priorTokens = new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1));
        var (service, _, loader) = BuildService(stub, method: NexusAuthMethod.OAuth, tokens: priorTokens);

        await service.LoginWithApiKeyAsync("the-key");

        Assert.Equal(NexusAuthMethod.ApiKey, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Equal("the-key", loader.Config.Integrations.Nexus.ApiKey);
        Assert.Null(loader.Config.Integrations.Nexus.OAuth); // cleared
    }

    [Fact]
    public async Task LoginWithApiKeyAsync_reverts_state_on_validation_failure()
    {
        // The validate call returns 401. The service reverts the speculative
        // write so the user's prior auth state is unchanged.
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var priorTokens = new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1));
        var (service, _, loader) = BuildService(stub, method: NexusAuthMethod.OAuth, tokens: priorTokens);

        var result = await service.LoginWithApiKeyAsync("bad-key");

        Assert.False(result.IsSuccess);
        // Reverted: AuthMethod + tokens are back to the prior OAuth state.
        Assert.Equal(NexusAuthMethod.OAuth, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Same(priorTokens, loader.Config.Integrations.Nexus.OAuth);
        Assert.Null(loader.Config.Integrations.Nexus.ApiKey);
    }

    [Fact]
    public async Task LoginWithApiKeyAsync_empty_key_returns_failure_without_call()
    {
        var stub = new StubHttpMessageHandler(_ => HttpResponses.NexusOk(@"{}"));
        var (service, _, _) = BuildService(stub);

        var result = await service.LoginWithApiKeyAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Empty(stub.Requests); // no validate call was made
    }

    // ---- OAuth login ------------------------------------------------------

    [Fact]
    public async Task LoginWithOAuthAsync_runs_full_oidc_flow_and_persists_tokens()
    {
        // End-to-end through OidcClient.LoginAsync: discovery -> authorize URL
        // (with state + PKCE) -> FakeBrowser returns the auth code ->
        // token exchange -> JWT access-token parse. The FakeBrowser captures
        // the state OidcClient generated (out of BrowserOption.StartUrl) +
        // echoes it back so OidcClient's CSRF check passes + the code is
        // actually redeemed.
        //
        // The access token is a JWT carrying the user claim (username +
        // membership_roles); the service parses it for the display name +
        // Premium state (no userinfo API call).
        var accessToken = BuildJwt(
            @"{""user"":{""id"":1,""username"":""OAuthUser"",""membership_roles"":[""premium""]}}");
        var stub = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            // Discovery lives at <Authority>/.well-known/openid-configuration,
            // with Authority = the issuer root (https://users.nexusmods.com).
            if (url.Contains("/.well-known/openid-configuration"))
            {
                return HttpResponses.Json(OAuthDiscoveryDoc("https://users.nexusmods.com"));
            }
            if (url.Contains("/oauth/discovery/keys"))
            {
                // Empty key set: we return no id_token from the token endpoint,
                // so no signature validation occurs; the discovery policy just
                // requires a key set to be present.
                return HttpResponses.Json("""{ "keys": [] }""");
            }
            if (url.Contains("/oauth/token"))
            {
                // OidcClient sends client_secret_post (TokenClientCredentialStyle
                // = PostBody), so both client_id + client_secret land in the
                // form-urlencoded token request body. Assert the secret wiring
                // is in effect (the test injects a non-empty secret via the
                // ConfigureOidcOptions seam below, since the production const
                // is empty in this test build: NEXUS_CLIENT_SECRET is not set,
                // so OidcClient would otherwise omit it).
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("client_id", body, StringComparison.Ordinal);
                Assert.Contains("client_secret", body, StringComparison.Ordinal);
                return HttpResponses.Json(
                    @"{ ""access_token"": """ + accessToken + @""", ""refresh_token"": ""RT"", ""expires_in"": 3600, ""token_type"": ""Bearer"" }");
            }
            return HttpResponses.Json(@"{}");
        });
        var (service, tokenStore, loader) = BuildService(
            stub,
            method: NexusAuthMethod.None,
            apiKey: "leftover-key",
            browser: new FakeBrowser(FakeBrowserMode.Success, code: "the-auth-code"));

        // The production const NexusOAuthConstants.ClientSecret is
        // build-injected from NEXUS_CLIENT_SECRET and empty in this test build
        // (the env var is not set), so OidcClient would omit it from the token
        // body. Inject a non-empty one via the test seam to prove the PostBody
        // + secret wiring actually carries both client credentials into the
        // token request.
        var priorConfigure = tokenStore.ConfigureOidcOptions;
        tokenStore.ConfigureOidcOptions = options =>
        {
            priorConfigure?.Invoke(options);
            // Assert production wired the const + PostBody BEFORE the overwrite,
            // so the body assertion below proves Curator's wiring, not just
            // OidcClient's runtime behavior with any configured secret.
            Assert.Equal(NexusOAuthConstants.ClientSecret, options.ClientSecret);
            Assert.Equal(ClientCredentialStyle.PostBody, options.TokenClientCredentialStyle);
            options.ClientSecret = "test-secret";
        };

        var result = await service.LoginWithOAuthAsync();

        // Success carries the JWT-claim-resolved name + premium flag.
        Assert.True(result.IsSuccess);
        Assert.Equal("OAuthUser", result.Name);
        Assert.True(result.IsPremium);

        // AuthMethod flipped to OAuth + tokens persisted from the token exchange.
        var nexus = loader.Config.Integrations.Nexus;
        Assert.Equal(NexusAuthMethod.OAuth, nexus.AuthMethod);
        Assert.NotNull(nexus.OAuth);
        Assert.Equal(accessToken, nexus.OAuth!.AccessToken);
        Assert.Equal("RT", nexus.OAuth.RefreshToken);
        Assert.Equal("openid", nexus.OAuth.Scope);
        AssertWithin(DateTimeOffset.UtcNow.AddSeconds(3600), nexus.OAuth.ExpiresAt, TimeSpan.FromMinutes(1));

        // Switching methods clears the OTHER method's credentials: the leftover
        // API key is gone.
        Assert.Null(nexus.ApiKey);

        // No userinfo API call: the name + premium come from the access token's
        // JWT payload, not a post-login fetch.
        Assert.DoesNotContain(stub.Requests,
            r => r.RequestUri?.ToString().Contains("/oauth/userinfo") == true);
    }

    [Fact]
    public async Task LoginWithOAuthAsync_treats_user_cancel_as_failure()
    {
        // FakeBrowser returns UserCancel: OidcClient surfaces a login error,
        // the token store returns null, + the auth service returns a failure
        // result without persisting anything.
        var stub = OAuthLoginStub();
        var (service, _, loader) = BuildService(
            stub,
            method: NexusAuthMethod.None,
            browser: new FakeBrowser(FakeBrowserMode.UserCancel));

        var result = await service.LoginWithOAuthAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Nexus OAuth login failed. Try again.", result.ErrorMessage);
        // State unchanged: still None, no tokens, no key.
        Assert.Equal(NexusAuthMethod.None, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Null(loader.Config.Integrations.Nexus.OAuth);
        Assert.Null(loader.Config.Integrations.Nexus.ApiKey);
    }

    [Fact]
    public async Task LoginWithOAuthAsync_treats_browser_timeout_as_failure()
    {
        var stub = OAuthLoginStub();
        var (service, _, loader) = BuildService(
            stub,
            method: NexusAuthMethod.None,
            browser: new FakeBrowser(FakeBrowserMode.Timeout));

        var result = await service.LoginWithOAuthAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Nexus OAuth login failed. Try again.", result.ErrorMessage);
        Assert.Equal(NexusAuthMethod.None, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Null(loader.Config.Integrations.Nexus.OAuth);
    }

    // ---- Sign out ----------------------------------------------------------

    [Fact]
    public async Task SignOutAsync_clears_method_key_and_tokens()
    {
        var stub = new StubHttpMessageHandler(_ => HttpResponses.NexusOk(@"{}"));
        var (service, _, loader) = BuildService(
            stub,
            method: NexusAuthMethod.OAuth,
            tokens: new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1)));

        await service.SignOutAsync();

        Assert.Equal(NexusAuthMethod.None, loader.Config.Integrations.Nexus.AuthMethod);
        Assert.Null(loader.Config.Integrations.Nexus.OAuth);
        Assert.Null(loader.Config.Integrations.Nexus.ApiKey);
    }

    // ---- GetCurrentUser ---------------------------------------------------

    [Fact]
    public async Task GetCurrentState_returns_null_when_None()
    {
        var stub = new StubHttpMessageHandler(_ => HttpResponses.NexusOk(@"{}"));
        var (service, _, _) = BuildService(stub, method: NexusAuthMethod.None);

        Assert.Null(await service.GetCurrentStateAsync());
    }

    [Fact]
    public async Task GetCurrentState_OAuth_parses_access_token_jwt_and_returns_resolved_state()
    {
        // The OAuth branch reads the access token's JWT payload (no API call),
        // so the stub is never hit. Seed a JWT carrying the user claim so the
        // parse resolves the name + Premium state.
        var jwt = BuildJwt(
            @"{""user"":{""id"":1,""username"":""OAuthUser"",""membership_roles"":[""premium""]}}");
        var stub = new StubHttpMessageHandler(_ => HttpResponses.Json(@"{}"));
        var (service, _, _) = BuildService(
            stub,
            method: NexusAuthMethod.OAuth,
            tokens: new NexusOAuthTokens(jwt, "RT", "openid", DateTimeOffset.UtcNow.AddHours(1)));

        var state = await service.GetCurrentStateAsync();

        Assert.NotNull(state);
        Assert.Equal(NexusAuthMethod.OAuth, state!.Method);
        Assert.Equal("OAuthUser", state.Name);
        Assert.True(state.IsPremium);
        // No API call was made (the JWT was parsed in-memory).
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task GetCurrentState_OAuth_returns_unverified_state_when_access_token_is_not_a_jwt()
    {
        // The OAuth branch parses the access token's JWT payload in-memory; a
        // non-JWT access token (opaque, no dot-separated segments) yields null
        // claims, so the service returns an unverified state (null name +
        // null Premium) rather than throwing, so the dialog can still render.
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (service, _, _) = BuildService(stub, method: NexusAuthMethod.OAuth, tokens: AnyTokens());

        var state = await service.GetCurrentStateAsync();

        Assert.NotNull(state);
        Assert.Equal(NexusAuthMethod.OAuth, state!.Method);
        Assert.Null(state.Name);
        Assert.Null(state.IsPremium);
        // No API call was made (the JWT parse is in-memory).
        Assert.Empty(stub.Requests);
    }

    // ---- token store (RefreshAsync) ---------------------------------------

    [Fact]
    public async Task RefreshAsync_persists_new_tokens_when_refresh_succeeds()
    {
        // The OidcClient's refresh path does discovery + then POSTs to the token
        // endpoint. The stub discriminates by URL: discovery gets the real-shape
        // .well-known doc (issuer = host root, endpoints under /oauth/...) so
        // OidcClient populates ProviderInformation + succeeds, the token
        // endpoint gets the canned refresh response.
        var stub = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/.well-known/openid-configuration"))
            {
                return HttpResponses.Json(OAuthDiscoveryDoc("https://users.nexusmods.com"));
            }
            return HttpResponses.Json(
                @"{ ""access_token"": ""NEW-AT"", ""refresh_token"": ""NEW-RT"", ""expires_in"": 3600, ""token_type"": ""Bearer"" }");
        });
        var (service, tokenStore, loader) = BuildService(
            stub,
            method: NexusAuthMethod.OAuth,
            tokens: new NexusOAuthTokens("OLD-AT", "OLD-RT", "openid", DateTimeOffset.UtcNow.AddHours(-1)));

        var refreshed = await tokenStore.RefreshAsync(default);

        Assert.NotNull(refreshed);
        Assert.Equal("NEW-AT", refreshed!.AccessToken);
        Assert.Equal("NEW-RT", refreshed.RefreshToken);
        // Persisted to config.
        Assert.Equal("NEW-AT", loader.Config.Integrations.Nexus.OAuth?.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_on_no_refresh_token()
    {
        var stub = new StubHttpMessageHandler(_ => HttpResponses.Json(@"{}"));
        var (service, tokenStore, _) = BuildService(
            stub,
            method: NexusAuthMethod.OAuth,
            tokens: new NexusOAuthTokens("AT", null, "openid", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Null(await tokenStore.RefreshAsync(default));
    }

    [Fact]
    public async Task RefreshAsync_returns_null_on_token_endpoint_error()
    {
        // The token endpoint returns 400 invalid_grant (refresh token revoked).
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest));
        var (service, tokenStore, _) = BuildService(
            stub,
            method: NexusAuthMethod.OAuth,
            tokens: new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Null(await tokenStore.RefreshAsync(default));
    }

    // ---- helpers -----------------------------------------------------------

    private static NexusOAuthTokens AnyTokens() =>
        new("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>
    /// Builds an unsigned JWT (header.payload.) from the supplied payload JSON.
    /// The parser does not verify the signature, so the signature segment is
    /// empty; the header + payload are base64url-encoded. Used to shape the
    /// access token the token-endpoint stub returns so the service's JWT parse
    /// exercises a real claim set.
    /// </summary>
    private static string BuildJwt(string payloadJson)
    {
        const string HeaderJson = "{\"alg\":\"none\",\"typ\":\"JWT\"}";
        return Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson)) + "."
            + Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson)) + ".";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Asserts <paramref name="actual"/> is within <paramref name="tolerance"/> of
    /// <paramref name="expected"/>. Used for the OAuth token's
    /// <c>ExpiresAt</c> (derived from <c>expires_in</c> + the test's wall clock)
    /// where an exact match is brittle.
    /// </summary>
    private static void AssertWithin(DateTimeOffset expected, DateTimeOffset actual, TimeSpan tolerance)
    {
        var delta = Math.Abs((expected - actual).Ticks);
        Assert.True(delta <= tolerance.Ticks,
            $"Expected {actual:O} within {tolerance} of {expected:O}, delta = {TimeSpan.FromTicks(delta)}");
    }

    /// <summary>
    /// A canned OAuth discovery document shaped like the real Nexus issuer's
    /// (https://users.nexusmods.com/.well-known/openid-configuration): the
    /// issuer is the host root, the endpoints live under
    /// <c>/oauth/...</c>, the JWKS URI under <c>/oauth/discovery/keys</c>.
    /// The stub HTTP handler discriminates the OAuth sub-paths by URL substring
    /// (token, userinfo, discovery/keys), so this shape exercises the real
    /// resolution path OidcClient takes when Authority is the issuer root.
    /// </summary>
    private static string OAuthDiscoveryDoc(string issuer) =>
        $$"""
        {
          "issuer": "{{issuer}}",
          "authorization_endpoint": "{{issuer}}/oauth/authorize",
          "token_endpoint": "{{issuer}}/oauth/token",
          "revocation_endpoint": "{{issuer}}/oauth/revoke",
          "introspection_endpoint": "{{issuer}}/oauth/introspect",
          "userinfo_endpoint": "{{issuer}}/oauth/userinfo",
          "jwks_uri": "{{issuer}}/oauth/discovery/keys",
          "response_types_supported": ["code", "token", "id_token", "id_token token"],
          "subject_types_supported": ["public"],
          "id_token_signing_alg_values_supported": ["RS256"],
          "scopes_supported": ["public", "openid"],
          "code_challenge_methods_supported": ["plain", "S256"]
        }
        """;

    /// <summary>
    /// Minimal stub for the OAuth-login failure paths (timeout / user-cancel):
    /// returns the discovery doc so OidcClient's <c>EnsureConfigurationAsync</c>
    /// succeeds + the FakeBrowser actually gets invoked. Nothing else is hit
    /// (the browser bails before the token endpoint), so the catch-all returns
    /// an empty JSON: a real hit would surface as a parse error in the test
    /// rather than a silent pass.
    /// </summary>
    private static StubHttpMessageHandler OAuthLoginStub() =>
        new(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/.well-known/openid-configuration"))
            {
                return HttpResponses.Json(OAuthDiscoveryDoc("https://users.nexusmods.com"));
            }
            return HttpResponses.Json(@"{}");
        });

    /// <summary>
    /// Builds a NexusAuthService + its NexusOAuthTokenStore wired to a stub HTTP
    /// handler (the v1 client's validate calls). The OidcClient's
    /// backchannel (discovery + token + refresh) is wired to the SAME stub via
    /// the token store's <see cref="NexusOAuthTokenStore.ConfigureOidcOptions"/>
    /// test seam, so refresh + login flows run offline too. The browser defaults
    /// to a <see cref="FailingBrowser"/> so any test that accidentally drives the
    /// OAuth login flow fails loudly; OAuth-login tests pass a
    /// <see cref="FakeBrowser"/>.
    /// </summary>
    private static (NexusAuthService service, NexusOAuthTokenStore tokenStore, FakeConfigLoader loader) BuildService(
        StubHttpMessageHandler stub,
        NexusAuthMethod method = NexusAuthMethod.None,
        string? apiKey = null,
        NexusOAuthTokens? tokens = null,
        IBrowser? browser = null)
    {
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig
            {
                Integrations =
                {
                    Nexus = new NexusConfig
                    {
                        AuthMethod = method,
                        ApiKey = apiKey,
                        OAuth = tokens,
                        BaseUrl = "https://api.nexusmods.com",
                        OAuthBaseUrl = "https://users.nexusmods.com",
                    },
                },
            },
        };

        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.nexusmods.com/") };

        // The OAuth token store: real implementation (so refresh + persist work
        // end-to-end against the stub), with a FailingBrowser so tests that
        // don't drive the OAuth login flow fail loudly if they accidentally do.
        // OAuth-login tests pass in a FakeBrowser. The stub is wired into
        // OidcClient's backchannel via the test seam so discovery + token calls
        // hit the stub (the stub discriminates by URL).
        var tokenStore = new NexusOAuthTokenStore(
            loader,
            browser ?? new FailingBrowser(),
            NullLogger<NexusOAuthTokenStore>.Instance)
        {
            ConfigureOidcOptions = options =>
            {
                options.BackchannelHandler = stub;
            },
        };

        // The auth factory selector wired the same way production wires it.
        var apiKeyFactory = new ApiKeyMessageFactory(loader);
        var oauthFactory = new OAuth2MessageFactory(tokenStore, NullLogger<OAuth2MessageFactory>.Instance);
        var noneFactory = new NoneMessageFactory();
        var selector = new NexusAuthMessageFactorySelector(loader, apiKeyFactory, oauthFactory, noneFactory);

        var client = new NexusClient(http, selector, NullLogger<NexusClient>.Instance);

        var service = new NexusAuthService(loader, client, tokenStore, NullLogger<NexusAuthService>.Instance);
        return (service, tokenStore, loader);
    }

    /// <summary>
    /// An <see cref="IBrowser"/> that always fails. Used for tests that exercise
    /// the API-key + token-store paths and never invoke the OAuth flow.
    /// </summary>
    private sealed class FailingBrowser : IBrowser
    {
        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("OAuth flow should not be invoked in this test.");
    }

    /// <summary>
    /// The mode the <see cref="FakeBrowser"/> drives the OAuth flow into.
    /// </summary>
    private enum FakeBrowserMode
    {
        /// <summary>
        /// Return a successful authorization response: parse the state OidcClient
        /// generated out of <see cref="BrowserOptions.StartUrl"/> + echo it back
        /// with a preset authorization code so OidcClient's CSRF check passes +
        /// the code is actually redeemed at the token endpoint.
        /// </summary>
        Success,

        /// <summary>Return <see cref="BrowserResultType.Timeout"/>.</summary>
        Timeout,

        /// <summary>Return <see cref="BrowserResultType.UserCancel"/>.</summary>
        UserCancel,
    }

    /// <summary>
    /// A scriptable <see cref="IBrowser"/> that drives the OAuth login flow
    /// offline. In <see cref="FakeBrowserMode.Success"/> mode it captures the
    /// <c>state</c> parameter OidcClient generated (out of
    /// <see cref="BrowserOptions.StartUrl"/>) + returns a preset authorization
    /// code with that state, so OidcClient's full <c>LoginAsync</c> runs:
    /// discovery, authorize URL build, code redemption, token persistence. In
    /// <see cref="FakeBrowserMode.Timeout"/> / <see cref="FakeBrowserMode.UserCancel"/>
    /// modes it returns the matching <see cref="BrowserResultType"/> so the
    /// service surfaces the right failure.
    /// </summary>
    /// <remarks>
    /// The state capture is what makes this a real end-to-end exercise of
    /// <c>NexusAuthService.LoginWithOAuthAsync</c> through OidcClient, not a
    /// mock that bypasses it: without echoing the exact state OidcClient
    /// generated, <c>ProcessResponseAsync</c> rejects the response as
    /// "Invalid state" + no tokens are exchanged.
    /// </remarks>
    private sealed class FakeBrowser : IBrowser
    {
        private readonly FakeBrowserMode _mode;
        private readonly string _code;

        public FakeBrowser(FakeBrowserMode mode, string code = "auth-code")
        {
            _mode = mode;
            _code = code;
        }

        /// <summary>
        /// The <see cref="BrowserOptions"/> OidcClient passed to the last
        /// invocation. Carries the generated authorize URL (with state + PKCE
        /// challenge + nonce) so tests can assert on OidcClient's URL shape if
        /// they want.
        /// </summary>
        public BrowserOptions? LastOptions { get; private set; }

        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
        {
            LastOptions = options;

            if (_mode == FakeBrowserMode.Success)
            {
                // Echo back the state OidcClient generated (parsed out of
                // StartUrl) so its CSRF check passes + the token exchange runs.
                var state = ExtractQueryParam(options.StartUrl, "state");
                return Task.FromResult(new BrowserResult
                {
                    ResultType = BrowserResultType.Success,
                    Response = $"?code={Uri.EscapeDataString(_code)}&state={Uri.EscapeDataString(state)}",
                });
            }

            var resultType = _mode == FakeBrowserMode.Timeout
                ? BrowserResultType.Timeout
                : BrowserResultType.UserCancel;
            return Task.FromResult(new BrowserResult
            {
                ResultType = resultType,
                Error = resultType == BrowserResultType.Timeout ? "timed out" : "cancelled",
            });
        }

        /// <summary>
        /// Pulls a single query parameter out of a URL by name. Manual parse so
        /// the test doesn't pull in <c>Microsoft.AspNetCore.WebUtilities</c>
        /// just for this; the values OidcClient generates are URL-safe so
        /// <see cref="Uri.UnescapeDataString"/> is enough.
        /// </summary>
        private static string ExtractQueryParam(string url, string name)
        {
            var queryStart = url.IndexOf('?');
            if (queryStart < 0)
            {
                return string.Empty;
            }
            var query = url[(queryStart + 1)..];
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == name)
                {
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }
            return string.Empty;
        }
    }
}

/// <summary>
/// <see cref="NexusOAuthTokens"/> + <see cref="NexusConfig"/> JSON round-trip
/// through the config loader's serializer, so a Nexus login that persists tokens
/// survives an app restart.
/// </summary>
public sealed class NexusConfigRoundTripTests
{
    [Fact]
    public void NexusConfig_default_round_trips()
    {
        var config = new CuratorConfig();
        var roundTripped = RoundTrip(config);
        Assert.Equal(NexusAuthMethod.None, roundTripped.Integrations.Nexus.AuthMethod);
        Assert.Null(roundTripped.Integrations.Nexus.ApiKey);
        Assert.Null(roundTripped.Integrations.Nexus.OAuth);
    }

    [Fact]
    public void NexusConfig_OAuth_with_tokens_round_trips()
    {
        var config = new CuratorConfig
        {
            Integrations =
            {
                Nexus = new NexusConfig
                {
                    AuthMethod = NexusAuthMethod.OAuth,
                    OAuth = new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UnixEpoch),
                },
            },
        };

        var roundTripped = RoundTrip(config);

        Assert.Equal(NexusAuthMethod.OAuth, roundTripped.Integrations.Nexus.AuthMethod);
        Assert.NotNull(roundTripped.Integrations.Nexus.OAuth);
        Assert.Equal("AT", roundTripped.Integrations.Nexus.OAuth!.AccessToken);
        Assert.Equal("RT", roundTripped.Integrations.Nexus.OAuth.RefreshToken);
        Assert.Equal("openid", roundTripped.Integrations.Nexus.OAuth.Scope);
    }

    [Fact]
    public void NexusConfig_ApiKey_round_trips()
    {
        var config = new CuratorConfig
        {
            Integrations =
            {
                Nexus = new NexusConfig
                {
                    AuthMethod = NexusAuthMethod.ApiKey,
                    ApiKey = "the-key",
                },
            },
        };

        var roundTripped = RoundTrip(config);

        Assert.Equal(NexusAuthMethod.ApiKey, roundTripped.Integrations.Nexus.AuthMethod);
        Assert.Equal("the-key", roundTripped.Integrations.Nexus.ApiKey);
    }

    private static CuratorConfig RoundTrip(CuratorConfig config)
    {
        // The config loader's serializer options: WriteIndented + camelCase
        // enum strings (the same options ConfigLoader.Save uses).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var json = JsonSerializer.Serialize(config, options);
        return JsonSerializer.Deserialize<CuratorConfig>(json, options)!;
    }
}
