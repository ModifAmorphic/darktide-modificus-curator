using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Proves <c>AddRelayClient()</c> registers <see cref="IRelayLaunchService"/>
/// (and the supporting <see cref="IProcessLauncher"/> seam) so it is resolvable
/// from DI with the production-style deps (<c>IProfileService</c> +
/// <c>ISteamService</c> + <c>IConfigLoader</c>), and that pre-registered overrides
/// win over the defaults via TryAdd.
/// </summary>
public sealed class RelayClientServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRelayClient_registers_resolvable_IRelayLaunchService()
    {
        var services = BuildComposition();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetService<IRelayLaunchService>();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<IRelayLaunchService>(service);
    }

    [Fact]
    public void AddRelayClient_registers_default_IProcessLauncher()
    {
        var services = BuildComposition();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IProcessLauncher>());
    }

    [Fact]
    public void AddRelayClient_pre_registered_IProcessLauncher_wins_over_default()
    {
        // A host/tests can inject a custom launch hook; TryAdd must defer.
        var custom = new FakeProcessLauncher();

        var services = BuildComposition();
        services.AddSingleton<IProcessLauncher>(custom);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IProcessLauncher>();

        Assert.Same(custom, resolved);
    }

    [Fact]
    public void AddRelayClient_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddRelayClient();

        Assert.Same(services, returned);
    }

    /// <summary>
    /// Builds the minimal composition that makes
    /// <see cref="IRelayLaunchService"/> resolvable: fakes for the profile +
    /// steam services, a default config loader, logging, then
    /// <see cref="ServiceCollectionExtensions.AddRelayClient"/>.
    /// </summary>
    private static ServiceCollection BuildComposition()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddSingleton<IProfileService, FakeProfileService>();
        services.AddSingleton<ISteamService, FakeSteamService>();
        // The game-dir host is registered by the composition root (it needs
        // the Profiles staging-link primitive + the app-state receipts role),
        // so the minimal AddRelayClient composition supplies a fake like a
        // host/tests would.
        services.AddSingleton<IGameDirModsHost>(new FakeGameDirModsHost());
        services.AddRelayClient();
        return services;
    }
}
