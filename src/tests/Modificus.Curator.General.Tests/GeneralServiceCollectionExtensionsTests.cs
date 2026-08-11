using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Proves the General DI registration is resolvable: AddGeneral() registers the
/// logger factory, logging, config loader, and app-state store so any component
/// can take <c>ILogger&lt;T&gt;</c> / <see cref="IConfigLoader"/> /
/// <see cref="IAppStateStore"/> via constructor injection. The config itself is
/// NOT a registered singleton: consumers read it live via
/// <see cref="IConfigLoader"/>.<see cref="IConfigLoader.Load"/> on each op.
/// </summary>
public sealed class GeneralServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGeneral_registers_resolvable_services()
    {
        using var loggerFactory = new LoggerFactory();

        var services = new ServiceCollection();
        services.AddGeneral(loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(loggerFactory, provider.GetRequiredService<ILoggerFactory>());
        Assert.NotNull(provider.GetService<ILogger<GeneralServiceCollectionExtensionsTests>>());
        Assert.IsType<ConfigLoader>(provider.GetRequiredService<IConfigLoader>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IAppStateStore>());
    }

    [Fact]
    public void AddGeneral_allows_an_IConfigLoader_override_via_TryAdd()
    {
        // TryAdd so the composition root (or a test/host) can pre-register the
        // same loader instance it used for its startup snapshot before AddGeneral.
        using var loggerFactory = new LoggerFactory();
        var custom = new FakeConfigLoader();

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(custom);
        services.AddGeneral(loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IConfigLoader>());
    }

    [Fact]
    public void AddGeneral_allows_an_IAppStateStore_override_via_TryAdd()
    {
        // TryAdd so a test/host can pre-register an override before AddGeneral.
        using var loggerFactory = new LoggerFactory();
        var custom = new CustomAppStateStore();

        var services = new ServiceCollection();
        services.AddSingleton<IAppStateStore>(custom);
        services.AddGeneral(loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IAppStateStore>());
    }

    private sealed class CustomAppStateStore : IAppStateStore
    {
        public bool OnboardingCompleted { get; set; }
        public Guid? ActiveProfileId { get; set; }
        public DateTimeOffset? LastUpdateCheckUtc { get; set; }
        public IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
        public IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; }
        public DateTimeOffset? LastNexusMetadataBackfillUtc { get; set; }
    }
}
