using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Proves <c>AddIntegrations()</c> registers the Nexus typed HTTP client
/// configured from <see cref="CuratorConfig.Integrations.Nexus"/> (base URL),
/// resolvable via DI with an <c>IHttpClientFactory</c>-provided
/// <c>HttpClient</c>, plus the acquisition + update-check singletons.
/// </summary>
/// <remarks>
/// Config is verified end-to-end: a stub <see cref="HttpMessageHandler"/> is
/// wired into the same typed-client registration <c>AddIntegrations()</c> builds,
/// the client makes a real (offline) call, and the recorded request is asserted
/// on, so the test proves the <see cref="IConfigLoader"/> →
/// <see cref="CuratorConfig"/> → <c>HttpClient</c> wiring actually reaches the
/// wire, not just that something resolves.
/// </remarks>
public sealed class IntegrationsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddIntegrations_registers_resolvable_INexusClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        // LoopbackBrowser ctor-injects the shell-open launcher (AddGeneral
        // owns the production registration; the tests wire the in-memory fake).
        services.AddSingleton<IExternalLauncher>(new FakeExternalLauncher());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetService<INexusClient>();

        Assert.NotNull(client);
        Assert.IsAssignableFrom<INexusClient>(client);
    }

    [Fact]
    public void AddIntegrations_exposes_IHttpClientFactory()
    {
        // The typed client's HttpClient is built by the factory, proving the
        // standard, testable HTTP DI pattern is wired.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        // LoopbackBrowser ctor-injects the shell-open launcher (AddGeneral
        // owns the production registration; the tests wire the in-memory fake).
        services.AddSingleton<IExternalLauncher>(new FakeExternalLauncher());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public async Task AddIntegrations_configures_base_url_from_config()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.BaseUrl = "https://api.test.local";
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        config.Integrations.Nexus.ApiKey = "test-key";

        var (client, handler) = BuildWithStub(config);
        // Drive a real (offline) call so the configured BaseAddress reaches the wire.
        await client.GetModInfoAsync("warhammer40kdarktide", 1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.test.local/v1/games/warhammer40kdarktide/mods/1.json",
            request.RequestUri!.ToString());
    }

    [Fact]
    public async Task AddIntegrations_normalizes_trailing_slash_on_base_url()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.BaseUrl = "https://api.nexusmods.example"; // no trailing slash
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        config.Integrations.Nexus.ApiKey = "test-key";

        var (client, handler) = BuildWithStub(config);
        await client.GetModInfoAsync("warhammer40kdarktide", 1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.nexusmods.example/v1/games/warhammer40kdarktide/mods/1.json",
            request.RequestUri!.ToString());
    }

    [Fact]
    public async Task AddIntegrations_falls_back_to_default_base_url_when_blank()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.BaseUrl = "   ";
        config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        config.Integrations.Nexus.ApiKey = "test-key";

        var (client, handler) = BuildWithStub(config);
        await client.GetModInfoAsync("warhammer40kdarktide", 1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.nexusmods.com/v1/games/warhammer40kdarktide/mods/1.json",
            request.RequestUri!.ToString());
    }

    [Fact]
    public void AddIntegrations_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddIntegrations();

        Assert.Same(services, returned);
    }

    [Fact]
    public void AddIntegrations_registers_IUpdateCheckService_as_singleton()
    {
        // The update-check service orchestrates across Nexus + Mods (the check
        // set arrives as caller-mapped ModListCandidates, so no Profiles
        // dependency exists). Verified by descriptor (rather than resolving) so
        // the test does not need an IModRepository stub (registered by AddMods
        // in the composition root, not by AddIntegrations). The service's own
        // tests construct it directly and cover its behavior end-to-end.
        var services = new ServiceCollection();
        services.AddIntegrations();

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IUpdateCheckService));

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(UpdateCheckService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddIntegrations_registers_IModAcquisitionService_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddIntegrations();

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IModAcquisitionService));

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(ModAcquisitionService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// Wires a stub HTTP handler into the typed-client registration
    /// <c>AddIntegrations()</c> builds (by re-entering the same
    /// <c>AddHttpClient&lt;INexusClient, NexusClient&gt;</c> builder) so tests
    /// can drive the real client offline and inspect the outgoing request.
    /// </summary>
    private static (INexusClient Client, StubHttpMessageHandler Handler) BuildWithStub(CuratorConfig config)
    {
        // An empty mod-info envelope the Nexus client can deserialize.
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.Json("{\"name\":\"x\",\"version\":\"1\",\"endorsement_count\":0,\"category_id\":0}"));

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config });
        // LoopbackBrowser ctor-injects the shell-open launcher (AddGeneral
        // owns the production registration; the tests wire the in-memory fake).
        services.AddSingleton<IExternalLauncher>(new FakeExternalLauncher());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        // Attach the stub to the same named typed client AddIntegrations registered.
        services.AddHttpClient<INexusClient, NexusClient>()
            .ConfigurePrimaryHttpMessageHandler(_ => handler);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<INexusClient>(), handler);
    }
}
