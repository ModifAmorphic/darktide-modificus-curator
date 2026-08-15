using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Proves the General DI registration is resolvable: AddGeneral() registers the
/// logger factory, logging, config loader, external launcher, and the app-state
/// role interfaces so any component can take <c>ILogger&lt;T&gt;</c> /
/// <see cref="IConfigLoader"/> / <see cref="IExternalLauncher"/> / a state role
/// via constructor injection. The config itself is NOT a registered singleton:
/// consumers read it live via
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
        Assert.IsType<ShellExternalLauncher>(provider.GetRequiredService<IExternalLauncher>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IOnboardingState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IProfileActivationState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IUpdateCheckScheduleState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IKnownUpdateState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<INexusMetadataBackfillState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IMainWindowStatePersistence>());
    }

    [Fact]
    public void AddGeneral_resolves_every_state_role_to_the_same_store_instance()
    {
        // One concrete store backs every role: all consumers share one cached
        // model + one app-state.json writer, never one instance per role.
        using var loggerFactory = new LoggerFactory();

        var services = new ServiceCollection();
        services.AddGeneral(loggerFactory);
        var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<AppStateStore>();
        Assert.Same(store, provider.GetRequiredService<IOnboardingState>());
        Assert.Same(store, provider.GetRequiredService<IProfileActivationState>());
        Assert.Same(store, provider.GetRequiredService<IUpdateCheckScheduleState>());
        Assert.Same(store, provider.GetRequiredService<IKnownUpdateState>());
        Assert.Same(store, provider.GetRequiredService<INexusMetadataBackfillState>());
        Assert.Same(store, provider.GetRequiredService<IMainWindowStatePersistence>());
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
    public void AddGeneral_allows_a_state_role_override_via_TryAdd()
    {
        // TryAdd per role so a test/host can pre-register an override (e.g. an
        // in-memory schedule state) before AddGeneral; the other roles keep
        // resolving the default store.
        using var loggerFactory = new LoggerFactory();
        var custom = new CustomScheduleState();

        var services = new ServiceCollection();
        services.AddSingleton<IUpdateCheckScheduleState>(custom);
        services.AddGeneral(loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IUpdateCheckScheduleState>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IOnboardingState>());
    }

    private sealed class CustomScheduleState : IUpdateCheckScheduleState
    {
        public DateTimeOffset? LastUpdateCheckUtc { get; set; }
        public IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
    }
}
