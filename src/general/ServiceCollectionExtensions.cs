using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.General;

/// <summary>
/// DI registration for the General library (cross-cutting infra). Registers
/// the logging pipeline and the config loader. The composition root builds the
/// logger first (it needs a one-off config snapshot), then calls this.
/// </summary>
/// <remarks>
/// <see cref="CuratorConfig"/> is intentionally NOT registered here: config is
/// read live from disk on each access via the registered <see cref="IConfigLoader"/>
/// singleton (which re-deserializes on every <see cref="IConfigLoader.Load"/>).
/// The startup snapshot used to build the logger is a one-off; logging config
/// does not change at runtime in v1.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers General services: the <paramref name="loggerFactory"/>,
    /// <c>AddLogging()</c>, <see cref="IConfigLoader"/>,
    /// <see cref="IExternalLauncher"/> (the OS shell-open seam), and the
    /// runtime app-state role interfaces (all forwarded to one
    /// <see cref="AppStateStore"/> singleton, persisted separately from
    /// <see cref="CuratorConfig"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="IConfigLoader"/> is registered with <c>TryAdd</c> so the
    /// composition root may pre-register the same loader instance it used for
    /// its one-off startup snapshot (one shared live-read singleton) before
    /// calling <see cref="AddGeneral"/>; the typed default is the fallback for
    /// hosts that do not pre-register (tests, smoke harnesses). The app-state
    /// roles follow the same pattern per role.
    /// </remarks>
    public static IServiceCollection AddGeneral(
        this IServiceCollection services,
        ILoggerFactory loggerFactory)
    {
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.TryAddSingleton<IConfigLoader, ConfigLoader>();
        services.TryAddSingleton<IExternalLauncher, ShellExternalLauncher>();
        // One concrete store singleton; every role interface forwards to that
        // same instance so all consumers share one cached model + one file.
        // TryAdd per role so a test/host may pre-register an override (e.g. an
        // in-memory or temp-path state store) before AddGeneral runs.
        services.TryAddSingleton<AppStateStore>();
        services.TryAddSingleton<IOnboardingState>(sp => sp.GetRequiredService<AppStateStore>());
        services.TryAddSingleton<IProfileActivationState>(sp => sp.GetRequiredService<AppStateStore>());
        services.TryAddSingleton<IUpdateCheckScheduleState>(sp => sp.GetRequiredService<AppStateStore>());
        services.TryAddSingleton<IKnownUpdateState>(sp => sp.GetRequiredService<AppStateStore>());
        services.TryAddSingleton<INexusMetadataBackfillState>(sp => sp.GetRequiredService<AppStateStore>());
        services.TryAddSingleton<IMainWindowStatePersistence>(sp => sp.GetRequiredService<AppStateStore>());
        return services;
    }
}
