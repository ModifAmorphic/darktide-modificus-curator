using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>DI registration for the Integrations library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Nexus client (a typed <c>HttpClient</c> with per-request
    /// auth applied by the auth message factory selector, which reads
    /// <see cref="NexusConfig.AuthMethod"/> live), the Nexus auth service +
    /// the loopback <see cref="IBrowser"/> + the token store (singletons), the
    /// mod acquisition service (download + extract + place orchestration over
    /// <see cref="INexusClient"/> + <see cref="IModImportService"/>, singleton),
    /// the update-check service (one-call v2 GraphQL <c>modsByUid</c> batch
    /// query for the active profile's LatestPolicy + NexusSource mods, singleton;
    /// the mod-list view binds badges to <see cref="IUpdateCheckService.LastResult"/>
    /// + subscribes to <see cref="IUpdateCheckService.CheckCompleted"/>), and the
    /// Nexus display-metadata backfill service (missing-only, sequential v1
    /// <c>GetModInfoAsync</c> pass capped at 25 attempts per persisted 24-hour
    /// window, singleton).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Nexus API base URL is normalized to end with a trailing slash so
    /// relative request URIs resolve correctly against
    /// <c>HttpClient.BaseAddress</c>.</para>
    /// </remarks>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddNexus(services);
        AddAcquisition(services);
        AddUpdateCheck(services);
        AddMetadataBackfill(services);

        return services;
    }

    private static void AddNexus(IServiceCollection services)
    {
        // The typed Nexus client. Base URL + default headers are configured here
        // (the auth headers are per-request via the auth factory; the BaseAddress
        // is the API root, used for every request the client makes).
        services.AddHttpClient<INexusClient, NexusClient>((serviceProvider, client) =>
        {
            var nexus = serviceProvider.GetRequiredService<IConfigLoader>().Load().Integrations.Nexus;
            var baseUrl = (nexus.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                baseUrl = NexusConfigDefaults.BaseUrl;
            }
            client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
        });

        // Auth message factories (selected live by NexusConfig.AuthMethod).
        // The selector reads AuthMethod per request; the inner factories are
        // singletons that read their credentials live from config.
        services.AddSingleton<ApiKeyMessageFactory>();
        services.AddSingleton<NoneMessageFactory>();
        // OAuth factory depends on INexusTokenStore (= NexusOAuthTokenStore).
        services.AddSingleton<OAuth2MessageFactory>();
        services.AddSingleton<INexusAuthMessageFactory, NexusAuthMessageFactorySelector>();

        // The loopback IBrowser (production default; the OAuth flow constructs a
        // fresh OidcClient per login so this is shared, not per-call).
        services.AddSingleton<IBrowser, LoopbackBrowser>();

        // The Nexus OAuth token store owns the OidcClient + the live token
        // persistence + the loopback login + the refresh path. It is the smaller
        // surface the OAuth message factory depends on (INexusTokenStore),
        // SEPARATE from the auth service so the DI graph has no cycle
        // (AuthService -> Client -> AuthFactory -> TokenStore; TokenStore
        // depends only on config + the browser).
        services.AddSingleton<NexusOAuthTokenStore>();
        services.AddSingleton<INexusTokenStore>(sp => sp.GetRequiredService<NexusOAuthTokenStore>());

        // The Nexus auth service (OAuth login + API-key validate + sign-out +
        // current-state read). Depends on the token store + the v1 client.
        services.AddSingleton<NexusAuthService>();
        services.AddSingleton<INexusAuthService>(sp => sp.GetRequiredService<NexusAuthService>());
    }

    /// <summary>
    /// Registers the mod acquisition service. A thin orchestrator over
    /// <see cref="INexusClient"/> + <see cref="IModImportService"/> + a plain
    /// <c>HttpClient</c> (from the factory, for the raw CDN archive download).
    /// Singleton: holds no per-call state.
    /// </summary>
    private static void AddAcquisition(IServiceCollection services)
    {
        services.AddSingleton<IModAcquisitionService, ModAcquisitionService>();
    }

    /// <summary>
    /// Registers the Nexus update-check service. Queries the v2 GraphQL
    /// <c>modsByUid</c> batch endpoint for the active profile's LatestPolicy +
    /// NexusSource mods via <see cref="INexusClient"/> +
    /// <see cref="IModRepository"/> + <see cref="Profiles.IProfileService"/>.
    /// Singleton: holds the last result (<see cref="IUpdateCheckService.LastResult"/>)
    /// so the mod-list view can bind badges to it without re-running the check, and
    /// publishes updates through <see cref="IUpdateCheckService.CheckCompleted"/>.
    /// Also registers <see cref="IUpdateStateStore"/> (the profile-scoped known-update
    /// persistence rules over <see cref="IKnownUpdateState.KnownUpdates"/>) as a
    /// singleton; the check service records each result through it, and the UI reads
    /// + acknowledges through it.
    /// </summary>
    private static void AddUpdateCheck(IServiceCollection services)
    {
        services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
    }

    /// <summary>
    /// Registers the Nexus display-metadata backfill service. A sequential,
    /// missing-only, active-profile-prioritized pass over the stable v1
    /// <c>GetModInfoAsync</c> endpoint, capped at 25 attempted containers per
    /// persisted 24-hour window. Singleton: holds the semaphore that serializes
    /// overlapping passes.
    /// </summary>
    private static void AddMetadataBackfill(IServiceCollection services)
    {
        services.AddSingleton<INexusModMetadataService, NexusModMetadataService>();
    }

    private static class NexusConfigDefaults
    {
        public const string BaseUrl = "https://api.nexusmods.com";
    }
}
