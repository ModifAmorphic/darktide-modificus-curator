using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modificus.Curator.General;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Proves <c>AddProfiles()</c> registers <see cref="IProfileService"/> so it is
/// resolvable from DI given its dependencies (<c>IConfigLoader</c> + logging).
/// </summary>
public sealed class ProfileServiceCollectionExtensionsTests
{
    [Fact]
    public void AddProfiles_registers_resolvable_IProfileService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddProfiles();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetService<IProfileService>();
        Assert.IsAssignableFrom<IProfileService>(service);
        Assert.NotNull(service);
    }

    [Fact]
    public void AddProfiles_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddProfiles();

        Assert.Same(services, returned);
    }

    [Fact]
    public void AddProfiles_maps_IProfileService_and_IProfileCloner_to_the_same_singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddProfiles();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<IProfileService>(),
            provider.GetRequiredService<IProfileCloner>());
    }
}
