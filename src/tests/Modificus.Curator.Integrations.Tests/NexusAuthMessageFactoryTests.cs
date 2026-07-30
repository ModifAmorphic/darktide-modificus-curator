using System.Net.Http.Headers;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises the Nexus auth message factories:
/// <see cref="ApiKeyMessageFactory"/> adds the <c>apikey</c> header;
/// <see cref="OAuth2MessageFactory"/> adds <c>Authorization: Bearer</c> +
/// refreshes on 401 (via a fake <see cref="INexusTokenStore"/>);
/// <see cref="NoneMessageFactory"/> reports not-authenticated;
/// <see cref="NexusAuthMessageFactorySelector"/> picks the right inner factory
/// based on the live <see cref="NexusConfig.AuthMethod"/>.
/// </summary>
public sealed class NexusAuthMessageFactoryTests
{
    // ---- ApiKeyMessageFactory ---------------------------------------------

    [Fact]
    public async Task ApiKeyFactory_adds_apikey_header_and_app_headers()
    {
        var loader = Loader(method: NexusAuthMethod.ApiKey, apiKey: "test-key");
        var factory = new ApiKeyMessageFactory(loader);

        var request = await factory.CreateAsync(HttpMethod.Get, new Uri("https://api.test.local/v1/users/validate.json"), default);

        Assert.Equal("test-key", request.Headers.GetValues("apikey").FirstOrDefault());
        Assert.Equal("Modificus-Curator", request.Headers.GetValues("Application-Name").FirstOrDefault());
        Assert.Equal("1.0.0", request.Headers.GetValues("Protocol-Version").FirstOrDefault());
        Assert.NotNull(request.Headers.UserAgent.ToString());
        Assert.Contains("Modificus-Curator", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        // No Authorization header (apikey goes in a custom header, not as a Bearer).
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task ApiKeyFactory_OnUnauthorized_returns_false_no_refresh()
    {
        var factory = new ApiKeyMessageFactory(Loader(NexusAuthMethod.ApiKey, "k"));
        Assert.False(await factory.OnUnauthorizedAsync(default));
    }

    [Fact]
    public async Task ApiKeyFactory_IsAuthenticated_reflects_configured_key()
    {
        var withKey = new ApiKeyMessageFactory(Loader(NexusAuthMethod.ApiKey, "k"));
        var withoutKey = new ApiKeyMessageFactory(Loader(NexusAuthMethod.ApiKey, apiKey: null));

        Assert.True(await withKey.IsAuthenticatedAsync(default));
        Assert.False(await withoutKey.IsAuthenticatedAsync(default));
    }

    // ---- OAuth2MessageFactory ---------------------------------------------

    [Fact]
    public async Task OAuthFactory_adds_bearer_authorization_and_app_headers()
    {
        var tokens = new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1));
        var store = new FakeTokenStore(tokens);
        var factory = new OAuth2MessageFactory(store, NullLogger<OAuth2MessageFactory>.Instance);

        var request = await factory.CreateAsync(HttpMethod.Get, new Uri("https://api.test.local/v1/users/validate.json"), default);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("AT", request.Headers.Authorization?.Parameter);
        Assert.Equal("Modificus-Curator", request.Headers.GetValues("Application-Name").FirstOrDefault());
    }

    [Fact]
    public async Task OAuthFactory_OnUnauthorized_refreshes_and_returns_true_when_refresh_succeeds()
    {
        var tokens = new NexusOAuthTokens("OLD", "RT", "openid", DateTimeOffset.UtcNow.AddHours(-1));
        var store = new FakeTokenStore(tokens, refreshResult: new NexusOAuthTokens("NEW", "RT2", "openid", DateTimeOffset.UtcNow.AddHours(1)));
        var factory = new OAuth2MessageFactory(store, NullLogger<OAuth2MessageFactory>.Instance);

        var result = await factory.OnUnauthorizedAsync(default);

        Assert.True(result);
        Assert.Equal(1, store.RefreshCalls);
        // After refresh, GetOAuthTokens returns the new token; CreateAsync builds with it.
        var request = await factory.CreateAsync(HttpMethod.Get, new Uri("https://x"), default);
        Assert.Equal("NEW", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task OAuthFactory_OnUnauthorized_returns_false_when_refresh_fails()
    {
        var tokens = new NexusOAuthTokens("OLD", "RT", "openid", DateTimeOffset.UtcNow.AddHours(-1));
        var store = new FakeTokenStore(tokens, refreshResult: null);
        var factory = new OAuth2MessageFactory(store, NullLogger<OAuth2MessageFactory>.Instance);

        var result = await factory.OnUnauthorizedAsync(default);

        Assert.False(result);
        Assert.Equal(1, store.RefreshCalls);
    }

    [Fact]
    public async Task OAuthFactory_OnUnauthorized_concurrent_calls_coalesce_into_one_refresh()
    {
        // Two concurrent 401s (e.g. two in-flight requests) should coalesce:
        // the first wins the refresh gate, the second sees the new token +
        // skips its own refresh call.
        var tokens = new NexusOAuthTokens("OLD", "RT", "openid", DateTimeOffset.UtcNow.AddHours(-1));
        var store = new FakeTokenStore(tokens, refreshResult: new NexusOAuthTokens("NEW", "RT2", "openid", DateTimeOffset.UtcNow.AddHours(1)))
        {
            RefreshDelay = TimeSpan.FromMilliseconds(50),
        };
        var factory = new OAuth2MessageFactory(store, NullLogger<OAuth2MessageFactory>.Instance);

        var task1 = factory.OnUnauthorizedAsync(default).AsTask();
        var task2 = factory.OnUnauthorizedAsync(default).AsTask();
        var results = await Task.WhenAll(task1, task2);

        // Both callers get "retry" (true) but only one refresh network call ran.
        Assert.Contains(true, results);
        Assert.Equal(1, store.RefreshCalls);
    }

    [Fact]
    public async Task OAuthFactory_IsAuthenticated_reflects_configured_tokens()
    {
        var withTokens = new OAuth2MessageFactory(
            new FakeTokenStore(new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1))),
            NullLogger<OAuth2MessageFactory>.Instance);
        var withoutTokens = new OAuth2MessageFactory(
            new FakeTokenStore(null),
            NullLogger<OAuth2MessageFactory>.Instance);

        Assert.True(await withTokens.IsAuthenticatedAsync(default));
        Assert.False(await withoutTokens.IsAuthenticatedAsync(default));
    }

    // ---- NoneMessageFactory ------------------------------------------------

    [Fact]
    public async Task NoneFactory_reports_not_authenticated_and_no_refresh()
    {
        var factory = new NoneMessageFactory();
        Assert.False(await factory.IsAuthenticatedAsync(default));
        Assert.False(await factory.OnUnauthorizedAsync(default));

        var request = await factory.CreateAsync(HttpMethod.Get, new Uri("https://x"), default);
        // None factory applies the app headers (so the request is well-formed)
        // but no auth header.
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Modificus-Curator", request.Headers.GetValues("Application-Name").FirstOrDefault());
    }

    // ---- Selector ----------------------------------------------------------

    [Fact]
    public async Task Selector_picks_factory_matching_live_AuthMethod()
    {
        var loader = Loader(method: NexusAuthMethod.ApiKey, apiKey: "the-key");
        var selector = new NexusAuthMessageFactorySelector(
            loader,
            new ApiKeyMessageFactory(loader),
            new OAuth2MessageFactory(
                new FakeTokenStore(new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1))),
                NullLogger<OAuth2MessageFactory>.Instance),
            new NoneMessageFactory());

        var request = await selector.CreateAsync(HttpMethod.Get, new Uri("https://x"), default);
        Assert.Equal("the-key", request.Headers.GetValues("apikey").FirstOrDefault());
    }

    [Fact]
    public async Task Selector_switches_when_AuthMethod_changes_in_config()
    {
        // The selector re-reads AuthMethod live on each call: a mid-session
        // login (None -> OAuth) takes effect on the next request without
        // restarting the client.
        var loader = Loader(method: NexusAuthMethod.None, apiKey: null);
        var apiKeyFactory = new ApiKeyMessageFactory(loader);
        var oauthFactory = new OAuth2MessageFactory(
            new FakeTokenStore(new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1))),
            NullLogger<OAuth2MessageFactory>.Instance);
        var selector = new NexusAuthMessageFactorySelector(loader, apiKeyFactory, oauthFactory, new NoneMessageFactory());

        // Start: AuthMethod = None. The selector's IsAuthenticated returns false.
        Assert.False(await selector.IsAuthenticatedAsync(default));

        // Flip to OAuth mid-session (the Integrations dialog's login flow does
        // this). The selector picks the OAuth factory on the next call.
        var nexus = loader.Config.Integrations.Nexus;
        nexus.AuthMethod = NexusAuthMethod.OAuth;
        nexus.OAuth = new NexusOAuthTokens("AT", "RT", "openid", DateTimeOffset.UtcNow.AddHours(1));

        Assert.True(await selector.IsAuthenticatedAsync(default));
        var request = await selector.CreateAsync(HttpMethod.Get, new Uri("https://x"), default);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
    }

    // ---- helpers -----------------------------------------------------------

    private static FakeConfigLoader Loader(NexusAuthMethod method, string? apiKey) =>
        new()
        {
            Config = new CuratorConfig
            {
                Integrations =
                {
                    Nexus = new NexusConfig
                    {
                        AuthMethod = method,
                        ApiKey = apiKey,
                        BaseUrl = "https://api.test.local",
                        OAuthBaseUrl = "https://users.test.local",
                    },
                },
            },
        };

    /// <summary>
    /// Fake <see cref="INexusTokenStore"/>: returns the configured tokens +
    /// records refresh calls. Optionally delays the refresh so concurrent-call
    /// coalescing can be exercised.
    /// </summary>
    private sealed class FakeTokenStore : INexusTokenStore
    {
        private NexusOAuthTokens? _current;
        private readonly NexusOAuthTokens? _refreshResult;

        public FakeTokenStore(NexusOAuthTokens? current, NexusOAuthTokens? refreshResult = null)
        {
            _current = current;
            _refreshResult = refreshResult;
        }

        public int RefreshCalls { get; private set; }
        public TimeSpan RefreshDelay { get; set; } = TimeSpan.Zero;

        public NexusOAuthTokens? GetOAuthTokens() => _current;

        public async Task<NexusOAuthTokens?> RefreshAsync(CancellationToken ct)
        {
            if (RefreshDelay > TimeSpan.Zero)
            {
                await Task.Delay(RefreshDelay, ct);
            }
            RefreshCalls++;
            if (_refreshResult is not null)
            {
                _current = _refreshResult; // simulate the persisted write
            }
            return _refreshResult;
        }
    }
}
